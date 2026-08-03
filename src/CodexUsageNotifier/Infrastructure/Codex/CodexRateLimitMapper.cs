using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Infrastructure.Codex;

/// <summary>
/// App Serverの利用枠レスポンスを、未知の枠を保持した内部モデルへ変換します。
/// </summary>
internal static class CodexRateLimitMapper
{
    private const long FiveHourDurationMinutes = 300;
    private const long WeeklyDurationMinutes = 10080;

    /// <summary>
    /// App Serverレスポンスをウィンドウ長に基づいて識別し、内部モデルへ変換します。
    /// </summary>
    /// <param name="response">App Serverが返した利用枠です。</param>
    /// <param name="trigger">利用枠を取得した契機です。</param>
    /// <param name="capturedAtUtc">利用枠を取得したUTC時刻です。</param>
    /// <returns>変換済みの利用枠スナップショットです。</returns>
    public static UsageSnapshot Map(
        CodexRateLimitResponse response,
        UsageCheckTrigger trigger,
        DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(response);

        CodexRateLimitSnapshot? selected = SelectCodexSnapshot(response);
        List<RateLimitWindow> unknownWindows = new();
        RateLimitWindow? fiveHour = null;
        RateLimitWindow? weekly = null;

        if (selected is not null)
        {
            ClassifySelectedWindow(
                ConvertWindow(selected, selected.Primary, RateLimitWindowSource.Primary),
                ref fiveHour,
                ref weekly,
                unknownWindows);
            ClassifySelectedWindow(
                ConvertWindow(selected, selected.Secondary, RateLimitWindowSource.Secondary),
                ref fiveHour,
                ref weekly,
                unknownWindows);
        }

        AddOtherLimitIdsAsUnknown(response, selected, unknownWindows);

        return new UsageSnapshot
        {
            CapturedAtUtc = capturedAtUtc,
            Trigger = trigger,
            RawLimitId = selected?.LimitId,
            Primary = fiveHour,
            Secondary = weekly,
            ResetCredits = ConvertResetCredits(response.RateLimitResetCredits?.AvailableCount),
            UnknownWindows = unknownWindows,
        };
    }

    /// <summary>
    /// limitIdがcodexの現在形式を優先し、存在しない場合だけ後方互換形式を選択します。
    /// </summary>
    /// <param name="response">App Serverレスポンスです。</param>
    /// <returns>識別対象とする利用枠です。</returns>
    private static CodexRateLimitSnapshot? SelectCodexSnapshot(CodexRateLimitResponse response)
    {
        if (response.RateLimitsByLimitId is not null
            && response.RateLimitsByLimitId.TryGetValue("codex", out CodexRateLimitSnapshot? codex)
            && codex is not null)
        {
            return codex;
        }

        return response.RateLimits;
    }

    /// <summary>
    /// 選択した利用枠のウィンドウを、長さだけに基づいて既知枠またはUnknownへ分類します。
    /// </summary>
    /// <param name="window">分類する内部ウィンドウです。</param>
    /// <param name="fiveHour">識別済みの5時間枠です。</param>
    /// <param name="weekly">識別済みの週間枠です。</param>
    /// <param name="unknownWindows">識別できなかった枠の格納先です。</param>
    private static void ClassifySelectedWindow(
        RateLimitWindow? window,
        ref RateLimitWindow? fiveHour,
        ref RateLimitWindow? weekly,
        List<RateLimitWindow> unknownWindows)
    {
        ArgumentNullException.ThrowIfNull(unknownWindows);
        if (window is null)
        {
            return;
        }

        if (window.Kind == RateLimitWindowKind.FiveHour && fiveHour is null)
        {
            fiveHour = window;
        }
        else if (window.Kind == RateLimitWindowKind.Weekly && weekly is null)
        {
            weekly = window;
        }
        else
        {
            unknownWindows.Add(CopyAsUnknown(window));
        }
    }

    /// <summary>
    /// 選択対象以外のlimitIdに含まれる枠を破棄せずUnknownとして追加します。
    /// </summary>
    /// <param name="response">App Serverレスポンスです。</param>
    /// <param name="selected">既知枠の識別に使用した利用枠です。</param>
    /// <param name="unknownWindows">Unknown枠の格納先です。</param>
    private static void AddOtherLimitIdsAsUnknown(
        CodexRateLimitResponse response,
        CodexRateLimitSnapshot? selected,
        List<RateLimitWindow> unknownWindows)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(unknownWindows);
        if (response.RateLimitsByLimitId is null)
        {
            return;
        }

        foreach (CodexRateLimitSnapshot? snapshot in response.RateLimitsByLimitId.Values)
        {
            if (snapshot is null || ReferenceEquals(snapshot, selected))
            {
                continue;
            }

            AddUnknownWindow(ConvertWindow(snapshot, snapshot.Primary, RateLimitWindowSource.Primary), unknownWindows);
            AddUnknownWindow(ConvertWindow(snapshot, snapshot.Secondary, RateLimitWindowSource.Secondary), unknownWindows);
        }
    }

    /// <summary>
    /// 値があるウィンドウをUnknownとして格納します。
    /// </summary>
    /// <param name="window">格納候補のウィンドウです。</param>
    /// <param name="unknownWindows">Unknown枠の格納先です。</param>
    private static void AddUnknownWindow(RateLimitWindow? window, List<RateLimitWindow> unknownWindows)
    {
        ArgumentNullException.ThrowIfNull(unknownWindows);
        if (window is not null)
        {
            unknownWindows.Add(CopyAsUnknown(window));
        }
    }

    /// <summary>
    /// App Serverのウィンドウを内部モデルへ変換します。
    /// </summary>
    /// <param name="snapshot">ウィンドウを所有する利用枠です。</param>
    /// <param name="window">変換元ウィンドウです。</param>
    /// <param name="source">レスポンス内での位置です。</param>
    /// <returns>変換済みウィンドウです。元がnullの場合はnullです。</returns>
    private static RateLimitWindow? ConvertWindow(
        CodexRateLimitSnapshot snapshot,
        CodexRateLimitWindow? window,
        RateLimitWindowSource source)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (window is null)
        {
            return null;
        }

        return new RateLimitWindow
        {
            Kind = ClassifyDuration(window.WindowDurationMins),
            LimitId = snapshot.LimitId,
            LimitName = snapshot.LimitName,
            Source = source,
            UsedPercent = window.UsedPercent,
            RemainingPercent = 100D - window.UsedPercent,
            WindowDurationMinutes = ConvertDuration(window.WindowDurationMins),
            ResetsAtUtc = ConvertUnixSeconds(window.ResetsAt),
        };
    }

    /// <summary>
    /// ウィンドウ長を既知枠へ分類します。
    /// </summary>
    /// <param name="durationMinutes">分単位のウィンドウ長です。</param>
    /// <returns>識別した枠の種類です。</returns>
    private static RateLimitWindowKind ClassifyDuration(long? durationMinutes) => durationMinutes switch
    {
        FiveHourDurationMinutes => RateLimitWindowKind.FiveHour,
        WeeklyDurationMinutes => RateLimitWindowKind.Weekly,
        _ => RateLimitWindowKind.Unknown,
    };

    /// <summary>
    /// 内部モデルで保持可能な範囲のウィンドウ長へ変換します。
    /// </summary>
    /// <param name="durationMinutes">App Serverが返した分数です。</param>
    /// <returns>保持可能な場合は分数、それ以外はnullです。</returns>
    private static int? ConvertDuration(long? durationMinutes)
    {
        return durationMinutes is >= int.MinValue and <= int.MaxValue
            ? (int)durationMinutes.Value
            : null;
    }

    /// <summary>
    /// Unix秒をUTC時刻へ安全に変換します。
    /// </summary>
    /// <param name="unixSeconds">App Serverが返したUnix秒です。</param>
    /// <returns>有効な場合はUTC時刻、それ以外はnullです。</returns>
    private static DateTimeOffset? ConvertUnixSeconds(long? unixSeconds)
    {
        if (unixSeconds is null)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// リセット回数を内部モデルで保持可能な場合だけ変換します。
    /// </summary>
    /// <param name="availableCount">App Serverが返した利用可能回数です。</param>
    /// <returns>保持可能な回数、またはnullです。</returns>
    private static int? ConvertResetCredits(long? availableCount)
    {
        return availableCount is >= int.MinValue and <= int.MaxValue
            ? (int)availableCount.Value
            : null;
    }

    /// <summary>
    /// ウィンドウ値を保ったまま識別結果だけUnknownへ変更します。
    /// </summary>
    /// <param name="window">複製元のウィンドウです。</param>
    /// <returns>Unknownとして複製したウィンドウです。</returns>
    private static RateLimitWindow CopyAsUnknown(RateLimitWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return new RateLimitWindow
        {
            Kind = RateLimitWindowKind.Unknown,
            LimitId = window.LimitId,
            LimitName = window.LimitName,
            Source = window.Source,
            UsedPercent = window.UsedPercent,
            RemainingPercent = window.RemainingPercent,
            WindowDurationMinutes = window.WindowDurationMinutes,
            ResetsAtUtc = window.ResetsAtUtc,
        };
    }
}
