using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Presentation.ViewModels;

/// <summary>
/// 利用枠の内部列挙値を一般利用者向けの日本語表示へ変換します。
/// </summary>
internal static class RateLimitDisplayFormatter
{
    /// <summary>App Server内の位置を利用者向け表示へ変換します。</summary>
    /// <param name="position">変換する内部位置です。</param>
    /// <returns>第1枠または第2枠です。</returns>
    internal static string FormatPosition(RateLimitPosition position) => position switch
    {
        RateLimitPosition.Primary => "第1枠",
        RateLimitPosition.Secondary => "第2枠",
        _ => "不明",
    };

    /// <summary>利用枠分類を利用者向け表示へ変換します。</summary>
    /// <param name="classification">変換する内部分類です。</param>
    /// <returns>短期枠、週間枠、または期間不明です。</returns>
    internal static string FormatClassification(RateLimitClassification classification) => classification switch
    {
        RateLimitClassification.FiveHour => "短期枠（5時間枠候補）",
        RateLimitClassification.Weekly => "週間枠",
        RateLimitClassification.Unknown => "期間不明",
        _ => "期間不明",
    };

    /// <summary>通知種別を利用者向け表示へ変換します。</summary>
    /// <param name="notificationType">変換する内部通知種別です。</param>
    /// <returns>通知目的を表す日本語です。</returns>
    internal static string FormatNotificationType(RateLimitNotificationType notificationType) => notificationType switch
    {
        RateLimitNotificationType.ShortWindowRecovered => "短期枠回復",
        RateLimitNotificationType.LongWindowEarlyWarning => "早期警告",
        RateLimitNotificationType.LongWindowStandardWarning => "通常警告",
        RateLimitNotificationType.LongWindowFinalWarning => "最終警告",
        RateLimitNotificationType.LongWindowResetCompleted => "新しい利用期間の開始",
        RateLimitNotificationType.NewRateLimitDetected => "新しい利用枠の検出",
        RateLimitNotificationType.MonitoringFailure => "監視障害",
        _ => "通知",
    };

    /// <summary>通知段階を利用者向け表示へ変換します。</summary>
    /// <param name="stage">変換する内部通知段階です。</param>
    /// <returns>通知段階を表す日本語です。</returns>
    internal static string FormatNotificationStage(RateLimitNotificationStage stage) => stage switch
    {
        RateLimitNotificationStage.None => "段階なし",
        RateLimitNotificationStage.Recovered => "回復",
        RateLimitNotificationStage.Early => "早期警告",
        RateLimitNotificationStage.Standard => "通常警告",
        RateLimitNotificationStage.Final => "最終警告",
        RateLimitNotificationStage.Completed => "確認済み",
        _ => "不明",
    };

    /// <summary>リセット完了の内部判定理由を利用者向け表示へ変換します。</summary>
    /// <param name="reason">変換する内部判定理由です。</param>
    /// <returns>判定方法を表す日本語です。</returns>
    internal static string FormatResetCompletionReason(RateLimitResetCompletionReason reason) => reason switch
    {
        RateLimitResetCompletionReason.ResetTimeAdvanced => "次回リセット時刻の更新を確認",
        RateLimitResetCompletionReason.UsageDropInference => "使用率の大幅な低下から推定",
        _ => "不明",
    };
}
