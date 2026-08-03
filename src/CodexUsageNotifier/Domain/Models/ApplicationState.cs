namespace CodexUsageNotifier.Domain.Models;

/// <summary>
/// アプリケーション再起動後も維持する実行状態を表します。
/// </summary>
public sealed record ApplicationState
{
    /// <summary>
    /// 現在の状態スキーマのバージョンです。
    /// </summary>
    public const int CurrentSchemaVersion = 1;

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
    /// 保留中の通知を取得または設定します。
    /// </summary>
    public DeferredNotificationState? DeferredNotification { get; init; }

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
    /// 送信が成功した状態を表します。
    /// </summary>
    Succeeded,

    /// <summary>
    /// 送信が失敗した状態を表します。
    /// </summary>
    Failed
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
