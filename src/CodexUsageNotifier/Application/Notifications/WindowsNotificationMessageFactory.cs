using System.Globalization;
using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Application.Notifications;

/// <summary>
/// 利用枠通知候補をWindows通知向けの日本語メッセージへ変換します。
/// </summary>
public static class WindowsNotificationMessageFactory
{
    /// <summary>
    /// 通知種別に応じたタイトルと本文を生成します。
    /// </summary>
    /// <param name="candidate">通知対象と種別を含む候補です。</param>
    /// <param name="capturedAtUtc">通知判定に使用した取得UTC時刻です。</param>
    /// <returns>Windows通知へ渡すメッセージです。</returns>
    public static WindowsNotificationMessage Create(
        RateLimitNotificationCandidate candidate,
        DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        string targetName = candidate.Window.Classification switch
        {
            RateLimitClassification.FiveHour => "5時間枠",
            RateLimitClassification.Weekly => "週間枠",
            _ => $"{candidate.Window.WindowDurationMinutes?.ToString(CultureInfo.CurrentCulture) ?? "不明"}分枠",
        };
        string resetAt = candidate.Window.ResetsAtUtc?.ToLocalTime()
            .ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture) ?? "不明";
        string identity = $"LimitId：{candidate.Window.LimitId ?? "不明"} / 位置：{candidate.Window.Position}";

        return candidate.NotificationType switch
        {
            RateLimitNotificationType.MonitoringFailure => new WindowsNotificationMessage
            {
                Title = "Codex利用枠の監視に失敗しています",
                Body = "これは監視障害通知のテストです。状態画面とログを確認してください。",
            },
            RateLimitNotificationType.ShortWindowRecovered => new WindowsNotificationMessage
            {
                Title = "Codexの短期利用枠が回復しました",
                Body = $"対象：{targetName}{Environment.NewLine}残り使用量：{candidate.Window.RemainingPercent:0.#}%{Environment.NewLine}次回リセット：{resetAt}{Environment.NewLine}{identity}",
            },
            RateLimitNotificationType.LongWindowResetCompleted => new WindowsNotificationMessage
            {
                Title = "Codex長期枠の新しい利用期間が始まりました",
                Body = $"対象：{targetName}{Environment.NewLine}残り使用量：{candidate.Window.RemainingPercent:0.#}%{Environment.NewLine}次回リセット：{resetAt}{Environment.NewLine}判定理由：{candidate.ResetCompletionReason?.ToString() ?? "不明"}{Environment.NewLine}{identity}",
            },
            _ => CreateLongWindowWarning(candidate, capturedAtUtc, resetAt, identity),
        };
    }

    /// <summary>
    /// 長期枠のリセット前通知本文を生成します。
    /// </summary>
    /// <param name="candidate">長期枠通知候補です。</param>
    /// <param name="capturedAtUtc">通知判定に使用した取得UTC時刻です。</param>
    /// <param name="resetAt">ローカル時刻へ変換済みのリセット時刻です。</param>
    /// <param name="identity">利用枠の識別表示です。</param>
    /// <returns>長期枠リセット前通知です。</returns>
    private static WindowsNotificationMessage CreateLongWindowWarning(
        RateLimitNotificationCandidate candidate,
        DateTimeOffset capturedAtUtc,
        string resetAt,
        string identity)
    {
        double remainingHours = candidate.Window.ResetsAtUtc is null
            ? 0D
            : Math.Max(0D, (candidate.Window.ResetsAtUtc.Value - capturedAtUtc).TotalHours);
        return new WindowsNotificationMessage
        {
            Title = "Codex週間枠のリセットが近づいています",
            Body = $"段階：{candidate.NotificationStage}{Environment.NewLine}残り使用量：{candidate.Window.RemainingPercent:0.#}%{Environment.NewLine}リセットまで：約{Math.Ceiling(remainingHours).ToString(CultureInfo.CurrentCulture)}時間{Environment.NewLine}リセット予定：{resetAt}{Environment.NewLine}{identity}{Environment.NewLine}実行したい作業がある場合は、バックログを確認してください。",
        };
    }
}
