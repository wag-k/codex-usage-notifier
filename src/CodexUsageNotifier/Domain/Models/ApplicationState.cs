namespace CodexUsageNotifier.Domain.Models;

/// <summary>
/// アプリケーション再起動後も維持する実行状態を表します。
/// </summary>
public sealed record ApplicationState
{
    /// <summary>
    /// 現在の状態スキーマのバージョンです。
    /// </summary>
    public const int CurrentSchemaVersion = 3;

    /// <summary>
    /// 状態スキーマのバージョンを取得または設定します。
    /// </summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>
    /// 最後に通知した回復期間IDを取得または設定します。
    /// </summary>
    public string? LastNotifiedRecoveryWindowId { get; init; }

    /// <summary>
    /// Windows通知の直近結果を取得または設定します。
    /// </summary>
    public DeliveryResultState? WindowsDeliveryResult { get; init; }

    /// <summary>
    /// Gmail通知の直近結果を取得または設定します。
    /// </summary>
    public DeliveryResultState? GmailDeliveryResult { get; init; }

    /// <summary>
    /// Phase 4Cの本番Gmail配送を開始したUTC時刻を取得または設定します。
    /// </summary>
    public DateTimeOffset? GmailProductionDeliveryStartedAtUtc { get; init; }

    /// <summary>
    /// 現在のGmail配送有効期間が始まったUTC時刻を取得または設定します。
    /// </summary>
    public DateTimeOffset? GmailDeliveryEnabledSinceUtc { get; init; }

    /// <summary>
    /// 前回の正常取得時にGmail通知設定が有効だったかを取得または設定します。
    /// </summary>
    public bool GmailDeliveryEnabledLastObserved { get; init; }

    /// <summary>
    /// 前回の正常取得時にGmail認証が送信可能だったかを取得または設定します。
    /// </summary>
    public bool GmailAuthenticationWasUsable { get; init; }

    /// <summary>
    /// 保留中の通知を取得または設定します。
    /// </summary>
    public DeferredNotificationState? DeferredNotification { get; init; }

    /// <summary>
    /// 利用枠・リセット期間・通知種別・通知段階ごとの通知状態を取得または設定します。
    /// </summary>
    public IReadOnlyList<RateLimitNotificationState> RateLimitNotificationStates { get; init; } =
        Array.Empty<RateLimitNotificationState>();

    /// <summary>
    /// リセット時刻がない短期枠を含む利用枠別の回復状態を取得または設定します。
    /// </summary>
    public IReadOnlyList<RateLimitRecoveryState> RateLimitRecoveryStates { get; init; } =
        Array.Empty<RateLimitRecoveryState>();

    /// <summary>
    /// 最後に利用枠を正常取得したUTC時刻を取得または設定します。
    /// </summary>
    public DateTimeOffset? LastSuccessfulFetchAtUtc { get; init; }

    /// <summary>
    /// 最後に正常取得した利用枠を取得または設定します。
    /// </summary>
    public UsageSnapshot? LastUsageSnapshot { get; init; }

    /// <summary>
    /// 現在の連続失敗回数を取得または設定します。
    /// </summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>
    /// 現在の障害について通知済みかどうかを取得または設定します。
    /// </summary>
    public bool FailureNotificationSent { get; init; }

    /// <summary>
    /// 初回設定が完了しているかどうかを取得または設定します。
    /// </summary>
    public bool InitialSetupCompleted { get; init; }

    /// <summary>
    /// 初期状態を生成します。
    /// </summary>
    /// <returns>初期状態です。</returns>
    public static ApplicationState CreateDefault() => new();
}

/// <summary>
/// 通知先ごとの直近の送信結果を表します。
/// </summary>
public sealed class DeliveryResultState
{
    /// <summary>
    /// 通知の送信状態を取得または設定します。
    /// </summary>
    public DeliveryStatus Status { get; init; }

    /// <summary>
    /// 送信を試みたUTC時刻を取得または設定します。
    /// </summary>
    public DateTimeOffset? AttemptedAtUtc { get; init; }

    /// <summary>
    /// 機密情報を含まない結果概要を取得または設定します。
    /// </summary>
    public string? Summary { get; init; }
}

/// <summary>
/// 通知の送信状態を表します。
/// </summary>
public enum DeliveryStatus
{
    /// <summary>
    /// まだ送信を試みていない状態を表します。
    /// </summary>
    NotAttempted,

    /// <summary>
    /// 重複送信を防ぐため、送信前に処理中として保存した状態を表します。
    /// </summary>
    InProgress,

    /// <summary>
    /// 送信が成功した状態を表します。
    /// </summary>
    Succeeded,

    /// <summary>
    /// 送信が失敗した状態を表します。
    /// </summary>
    Failed,

    /// <summary>
    /// 保留期限または利用期間が無効になり、送信対象から除外した状態を表します。
    /// </summary>
    Expired
}

/// <summary>
/// 通知禁止時間中に保留した通知の状態を表します。
/// </summary>
public sealed class DeferredNotificationState
{
    /// <summary>
    /// 回復期間IDを取得または設定します。
    /// </summary>
    public string RecoveryWindowId { get; init; } = string.Empty;

    /// <summary>
    /// 条件が成立したUTC時刻を取得または設定します。
    /// </summary>
    public DateTimeOffset ConditionMetAtUtc { get; init; }

    /// <summary>
    /// 保留を解除するUTC時刻を取得または設定します。
    /// </summary>
    public DateTimeOffset DeferredUntilUtc { get; init; }
}
