using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Infrastructure.Codex;

/// <summary>
/// App Serverの全利用枠を位置と分類を分離した内部モデルへ変換します。
/// </summary>
internal static class CodexRateLimitMapper
{
    private const long FiveHourDurationMinutes = 300;
    private const long WeeklyDurationMinutes = 10080;

    /// <summary>
    /// rateLimitsByLimitIdの全バケット、または後方互換バケットを内部モデルへ変換します。
    /// </summary>
    /// <param name="response">App Serverが返した利用枠です。</param>
    /// <param name="trigger">利用枠を取得した契機です。</param>
    /// <param name="capturedAtUtc">利用枠を取得したUTC時刻です。</param>
    /// <returns>全利用枠を含むスナップショットです。</returns>
    public static UsageSnapshot Map(
        CodexRateLimitResponse response,
        UsageCheckTrigger trigger,
        DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(response);
        List<RateLimitWindow> rateLimits = new();

        if (response.RateLimitsByLimitId is { Count: > 0 })
        {
            foreach (KeyValuePair<string, CodexRateLimitSnapshot?> pair in
                     response.RateLimitsByLimitId.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (pair.Value is not null)
                {
                    AddSnapshotWindows(rateLimits, pair.Value, pair.Key);
                }
            }
        }
        else if (response.RateLimits is not null)
        {
            AddSnapshotWindows(rateLimits, response.RateLimits, fallbackLimitId: "legacy");
        }

        return new UsageSnapshot
        {
            CapturedAtUtc = capturedAtUtc,
            Trigger = trigger,
            RateLimits = rateLimits,
            ResetCredits = ConvertResetCredits(response.RateLimitResetCredits?.AvailableCount),
        };
    }

    /// <summary>
    /// 1つのlimitIdに含まれるprimaryとsecondaryを独立した位置情報として追加します。
    /// </summary>
    /// <param name="destination">変換済み利用枠の追加先です。</param>
    /// <param name="snapshot">変換するlimitId単位のスナップショットです。</param>
    /// <param name="fallbackLimitId">レスポンスにlimitIdがない場合の辞書キーまたは後方互換識別子です。</param>
    private static void AddSnapshotWindows(
        List<RateLimitWindow> destination,
        CodexRateLimitSnapshot snapshot,
        string fallbackLimitId)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackLimitId);

        string limitId = string.IsNullOrWhiteSpace(snapshot.LimitId)
            ? fallbackLimitId
            : snapshot.LimitId;
        AddWindow(destination, snapshot, snapshot.Primary, limitId, RateLimitPosition.Primary);
        AddWindow(destination, snapshot, snapshot.Secondary, limitId, RateLimitPosition.Secondary);
    }

    /// <summary>
    /// 値が存在する1つの位置を内部利用枠へ変換して追加します。
    /// </summary>
    /// <param name="destination">変換済み利用枠の追加先です。</param>
    /// <param name="snapshot">limitId共通情報です。</param>
    /// <param name="window">変換元ウィンドウです。</param>
    /// <param name="limitId">内部で使用する非空のlimitIdです。</param>
    /// <param name="position">レスポンス内の位置です。</param>
    private static void AddWindow(
        List<RateLimitWindow> destination,
        CodexRateLimitSnapshot snapshot,
        CodexRateLimitWindow? window,
        string limitId,
        RateLimitPosition position)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(limitId);
        if (window is null)
        {
            return;
        }

        destination.Add(new RateLimitWindow
        {
            LimitId = limitId,
            LimitName = snapshot.LimitName,
            Position = position,
            Classification = ClassifyDuration(window.WindowDurationMins),
            UsedPercent = window.UsedPercent,
            RemainingPercent = 100D - window.UsedPercent,
            WindowDurationMinutes = ConvertDuration(window.WindowDurationMins),
            ResetsAtUtc = ConvertUnixSeconds(window.ResetsAt),
            PlanType = snapshot.PlanType,
            RateLimitReachedType = snapshot.RateLimitReachedType,
        });
    }

    /// <summary>
    /// ウィンドウ長を既知候補またはUnknownへ分類します。
    /// </summary>
    /// <param name="durationMinutes">分単位のウィンドウ長です。</param>
    /// <returns>ウィンドウ長に対応する分類です。</returns>
    private static RateLimitClassification ClassifyDuration(long? durationMinutes) => durationMinutes switch
    {
        FiveHourDurationMinutes => RateLimitClassification.FiveHour,
        WeeklyDurationMinutes => RateLimitClassification.Weekly,
        _ => RateLimitClassification.Unknown,
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
}
