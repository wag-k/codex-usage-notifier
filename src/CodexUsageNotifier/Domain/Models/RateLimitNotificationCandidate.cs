namespace CodexUsageNotifier.Domain.Models;

/// <summary>
/// 通知ポリシーが送信または保留の対象として返す利用枠通知候補を表します。
/// </summary>
public sealed class RateLimitNotificationCandidate
{
    /// <summary>
    /// 通知対象の利用枠を取得または設定します。
    /// </summary>
    public required RateLimitWindow Window { get; init; }

    /// <summary>
    /// 通知対象のリセット期間IDを取得または設定します。
    /// </summary>
    public required string RecoveryWindowId { get; init; }

    /// <summary>
    /// 通知種別を取得または設定します。
    /// </summary>
    public RateLimitNotificationType NotificationType { get; init; }

    /// <summary>
    /// 通知段階を取得または設定します。
    /// </summary>
    public RateLimitNotificationStage NotificationStage { get; init; }

    /// <summary>
    /// 条件が成立したUTC時刻を取得または設定します。
    /// </summary>
    public DateTimeOffset ConditionMetAtUtc { get; init; }
}
