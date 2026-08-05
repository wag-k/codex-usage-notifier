namespace CodexUsageNotifier.Domain.Models;

/// <summary>
/// 1つの利用枠・リセット期間・通知種別・通知段階に対する送信状態を表します。
/// </summary>
public sealed record RateLimitNotificationState
{
    /// <summary>
    /// App Serverが返した利用枠識別子を取得または設定します。
    /// </summary>
    public string LimitId { get; init; } = string.Empty;

    /// <summary>
    /// App Serverレスポンス内の位置を取得または設定します。
    /// </summary>
    public RateLimitPosition Position { get; init; }

    /// <summary>
    /// 利用枠の期間を分単位で取得または設定します。
    /// </summary>
    public int WindowDurationMinutes { get; init; }

    /// <summary>
    /// 同一のリセット期間を識別する値を取得または設定します。
    /// </summary>
    public string RecoveryWindowId { get; init; } = string.Empty;

    /// <summary>
    /// 通知の目的を取得または設定します。
    /// </summary>
    public RateLimitNotificationType NotificationType { get; init; }

    /// <summary>
    /// 通知種別内の段階を取得または設定します。
    /// </summary>
    public RateLimitNotificationStage NotificationStage { get; init; }

    /// <summary>
    /// 通知条件が成立したUTC時刻を取得または設定します。
    /// </summary>
    public DateTimeOffset ConditionMetAtUtc { get; init; }

    /// <summary>
    /// 最初にいずれかの通知先へ送信できたUTC時刻を取得または設定します。
    /// </summary>
    public DateTimeOffset? DeliveredAtUtc { get; init; }

    /// <summary>
    /// Windows通知の送信状態を取得または設定します。
    /// </summary>
    public DeliveryStatus WindowsDeliveryStatus { get; init; }

    /// <summary>
    /// Gmail通知の送信状態を取得または設定します。
    /// </summary>
    public DeliveryStatus GmailDeliveryStatus { get; init; }

    /// <summary>
    /// 通知禁止時間による保留終了UTC時刻を取得または設定します。
    /// </summary>
    public DateTimeOffset? DeferredUntilUtc { get; init; }

    /// <summary>
    /// リセット完了通知を判定した理由を取得または設定します。
    /// </summary>
    public RateLimitResetCompletionReason? ResetCompletionReason { get; init; }
}

/// <summary>
/// 長期枠のリセット完了を判定した根拠を表します。
/// </summary>
public enum RateLimitResetCompletionReason
{
    /// <summary>
    /// App Serverのリセット時刻が次の期間へ進んだことを表します。
    /// </summary>
    ResetTimeAdvanced,

    /// <summary>
    /// 使用率が設定されたポイント数以上低下した推定を表します。
    /// </summary>
    UsageDropInference,
}

/// <summary>
/// 利用枠に関する通知の目的を表します。
/// </summary>
public enum RateLimitNotificationType
{
    /// <summary>
    /// 短期枠が回復したことを表します。
    /// </summary>
    ShortWindowRecovered,

    /// <summary>
    /// 長期枠の早期リセット前通知を表します。
    /// </summary>
    LongWindowEarlyWarning,

    /// <summary>
    /// 長期枠の通常リセット前通知を表します。
    /// </summary>
    LongWindowStandardWarning,

    /// <summary>
    /// 長期枠の最終リセット前通知を表します。
    /// </summary>
    LongWindowFinalWarning,

    /// <summary>
    /// 長期枠の新しい利用期間が始まったことを表します。
    /// </summary>
    LongWindowResetCompleted,

    /// <summary>
    /// 新しい利用枠を初めて観測したことを表します。
    /// </summary>
    NewRateLimitDetected,

    /// <summary>
    /// 監視処理が連続して失敗したことを表します。
    /// </summary>
    MonitoringFailure,
}

/// <summary>
/// 利用枠通知の段階を表します。
/// </summary>
public enum RateLimitNotificationStage
{
    /// <summary>
    /// 段階を持たない通知を表します。
    /// </summary>
    None,

    /// <summary>
    /// 短期枠の回復段階を表します。
    /// </summary>
    Recovered,

    /// <summary>
    /// 長期枠の早期通知段階を表します。
    /// </summary>
    Early,

    /// <summary>
    /// 長期枠の通常通知段階を表します。
    /// </summary>
    Standard,

    /// <summary>
    /// 長期枠の最終通知段階を表します。
    /// </summary>
    Final,

    /// <summary>
    /// 長期枠のリセット完了段階を表します。
    /// </summary>
    Completed,
}
