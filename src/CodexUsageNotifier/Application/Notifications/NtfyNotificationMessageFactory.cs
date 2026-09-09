using System.Globalization;
using System.Text;
using CodexUsageNotifier.Application.Ntfy;
using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Application.Notifications;

/// <summary>共通通知候補をスマートフォンで要点を把握できるntfy通知へ整形します。</summary>
public static class NtfyNotificationMessageFactory
{
    /// <summary>同じ取得で成立した候補を1件のntfy通知へ集約します。</summary>
    public static NtfyNotificationMessage CreateAggregate(
        IReadOnlyList<RateLimitNotificationCandidate> candidates,
        DateTimeOffset confirmedAtUtc,
        TimeZoneInfo localTimeZone,
        RateLimitNotificationDisplayContext? displayContext)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(localTimeZone);
        ArgumentNullException.ThrowIfNull(displayContext);
        if (candidates.Count == 0)
        {
            throw new ArgumentException("スマホ通知候補が1件以上必要です。", nameof(candidates));
        }

        string title = candidates.Count == 1
            ? CreateTitle(candidates[0], displayContext)
            : $"Codex利用枠 {candidates.Count.ToString(CultureInfo.InvariantCulture)}件｜週間残り{displayContext.FormatWeeklyRemainingPercent(CultureInfo.InvariantCulture)}";
        StringBuilder body = new();
        RateLimitNotificationCandidate firstCandidate = candidates
            .FirstOrDefault(candidate => candidate.Window.Classification == RateLimitClassification.Weekly
                && candidate.Window.ResetsAtUtc is not null)
            ?? candidates.FirstOrDefault(candidate => candidate.Window.Classification == RateLimitClassification.Weekly)
            ?? candidates.FirstOrDefault(candidate => candidate.Window.ResetsAtUtc is not null)
            ?? candidates[0];
        DateTimeOffset? firstReset = firstCandidate.Window.ResetsAtUtc;
        body.Append("次回リセット ")
            .AppendLine(firstReset is null ? "未取得" : FormatLocal(firstReset.Value, localTimeZone));
        for (int index = 0; index < candidates.Count; index++)
        {
            RateLimitNotificationCandidate candidate = candidates[index];
            RateLimitWindow window = candidate.Window;
            if (candidates.Count > 1)
            {
                body.Append('[').Append(index + 1).AppendLine("]");
            }

            body.Append("通知内容: ").AppendLine(FormatType(candidate))
                .Append("通知種別: ").AppendLine(FormatType(candidate))
                .Append("通知段階: ").AppendLine(FormatStage(candidate.NotificationStage))
                .Append("残量: ").Append(window.RemainingPercent.ToString("0.##", CultureInfo.InvariantCulture)).AppendLine("%")
                .Append("週間枠残量: ").AppendLine(displayContext.FormatWeeklyRemainingPercent(CultureInfo.InvariantCulture))
                .Append("LimitId: ").AppendLine(window.LimitId ?? "未取得")
                .Append("位置: ").AppendLine(FormatPosition(window.Position))
                .Append("分類: ").AppendLine(FormatClassification(window.Classification))
                .Append("期間: ").Append(window.WindowDurationMinutes?.ToString(CultureInfo.InvariantCulture) ?? "未取得").AppendLine("分")
                .Append("次回リセット: ").AppendLine(window.ResetsAtUtc is null
                    ? "未取得"
                    : FormatLocal(window.ResetsAtUtc.Value, localTimeZone))
                .Append("条件成立: ").AppendLine(FormatLocal(candidate.ConditionMetAtUtc, localTimeZone));
            if (window.ResetsAtUtc is not null)
            {
                TimeSpan remaining = window.ResetsAtUtc.Value - confirmedAtUtc;
                if (remaining > TimeSpan.Zero)
                {
                    body.Append("リセットまで: ").Append(Math.Ceiling(remaining.TotalHours)).AppendLine("時間");
                }
            }

            if (candidate.ResetCompletionReason is not null)
            {
                body.Append("完了判定: ").AppendLine(FormatResetCompletionReason(candidate.ResetCompletionReason.Value));
            }
        }

        return new NtfyNotificationMessage { Title = title, Body = body.ToString().TrimEnd() };
    }

    /// <summary>単一候補から残量を先頭で確認できるタイトルを生成します。</summary>
    private static string CreateTitle(RateLimitNotificationCandidate candidate, RateLimitNotificationDisplayContext context)
    {
        string remaining = candidate.Window.RemainingPercent.ToString("0.##", CultureInfo.InvariantCulture);
        return candidate.NotificationType switch
        {
            RateLimitNotificationType.ShortWindowRecovered => $"短期枠回復｜週間残り{context.FormatWeeklyRemainingPercent(CultureInfo.InvariantCulture)}",
            RateLimitNotificationType.LongWindowEarlyWarning => $"週間枠 Early｜残り{remaining}%",
            RateLimitNotificationType.LongWindowStandardWarning => $"週間枠 Standard｜残り{remaining}%",
            RateLimitNotificationType.LongWindowFinalWarning => $"週間枠 Final｜残り{remaining}%",
            RateLimitNotificationType.LongWindowResetCompleted => $"週間枠リセット完了｜残り{remaining}%",
            _ => $"Codex利用枠｜残り{remaining}%",
        };
    }

    /// <summary>通知種別と段階を利用者向けの短い説明へ変換します。</summary>
    private static string FormatType(RateLimitNotificationCandidate candidate) => candidate.NotificationType switch
    {
        RateLimitNotificationType.ShortWindowRecovered => "短期枠の回復",
        RateLimitNotificationType.LongWindowEarlyWarning => "長期枠の早期通知",
        RateLimitNotificationType.LongWindowStandardWarning => "長期枠の通常通知",
        RateLimitNotificationType.LongWindowFinalWarning => "長期枠の最終通知",
        RateLimitNotificationType.LongWindowResetCompleted => "長期枠のリセット完了",
        RateLimitNotificationType.NewRateLimitDetected => "新しい利用枠の検出",
        RateLimitNotificationType.MonitoringFailure => "監視障害",
        _ => "利用枠のお知らせ",
    };

    /// <summary>通知段階を利用者向けの表示へ変換します。</summary>
    private static string FormatStage(RateLimitNotificationStage stage) => stage switch
    {
        RateLimitNotificationStage.Recovered => "回復",
        RateLimitNotificationStage.Early => "Early",
        RateLimitNotificationStage.Standard => "Standard",
        RateLimitNotificationStage.Final => "Final",
        RateLimitNotificationStage.Completed => "完了",
        _ => "なし",
    };

    /// <summary>利用枠の位置を利用者向けの日本語へ変換します。</summary>
    private static string FormatPosition(RateLimitPosition position) => position switch
    {
        RateLimitPosition.Primary => "プライマリ",
        RateLimitPosition.Secondary => "セカンダリ",
        _ => "不明",
    };

    /// <summary>利用枠分類を利用者向けの日本語へ変換します。</summary>
    private static string FormatClassification(RateLimitClassification classification) => classification switch
    {
        RateLimitClassification.FiveHour => "5時間枠",
        RateLimitClassification.Weekly => "週間枠",
        _ => "未分類",
    };

    /// <summary>リセット完了の判定理由を利用者向けの日本語へ変換します。</summary>
    private static string FormatResetCompletionReason(RateLimitResetCompletionReason reason) => reason switch
    {
        RateLimitResetCompletionReason.ResetTimeAdvanced => "リセット時刻の更新",
        RateLimitResetCompletionReason.UsageDropInference => "使用率低下による推定",
        _ => "不明",
    };

    /// <summary>UTC時刻を利用者のローカル時刻へ分精度で変換します。</summary>
    private static string FormatLocal(DateTimeOffset value, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTime(value, timeZone).ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);
}
