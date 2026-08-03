namespace CodexUsageNotifier.Domain.Models;

/// <summary>
/// アプリケーション設定の保存モデルを表します。
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// 現在の設定スキーマのバージョンです。
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// 設定スキーマのバージョンを取得または設定します。
    /// </summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>
    /// 5時間枠の通知閾値を取得または設定します。
    /// </summary>
    public int NotificationThresholdPercent { get; init; } = 99;

    /// <summary>
    /// 週間枠の警告閾値を取得または設定します。
    /// </summary>
    public int WeeklyWarningThresholdPercent { get; init; } = 20;

    /// <summary>
    /// Windows通知が有効かどうかを取得または設定します。
    /// </summary>
    public bool WindowsNotificationEnabled { get; init; } = true;

    /// <summary>
    /// Gmail通知が有効かどうかを取得または設定します。
    /// </summary>
    public bool GmailNotificationEnabled { get; init; }

    /// <summary>
    /// Gmail通知の送信先を取得または設定します。
    /// </summary>
    public string? GmailRecipient { get; init; }

    /// <summary>
    /// 通知禁止時間が有効かどうかを取得または設定します。
    /// </summary>
    public bool QuietHoursEnabled { get; init; } = true;

    /// <summary>
    /// 通知禁止時間の開始時刻を取得または設定します。
    /// </summary>
    public TimeOnly QuietHoursStart { get; init; } = new(0, 0);

    /// <summary>
    /// 通知禁止時間の終了時刻を取得または設定します。
    /// </summary>
    public TimeOnly QuietHoursEnd { get; init; } = new(7, 0);

    /// <summary>
    /// 補助確認間隔を分単位で取得または設定します。
    /// </summary>
    public int FallbackPollingMinutes { get; init; } = 60;

    /// <summary>
    /// リセット後の確認待機時間を秒単位で取得または設定します。
    /// </summary>
    public int ResetCheckDelaySeconds { get; init; } = 60;

    /// <summary>
    /// 利用履歴の保持日数を取得または設定します。
    /// </summary>
    public int HistoryRetentionDays { get; init; } = 90;

    /// <summary>
    /// ログの保持日数を取得または設定します。
    /// </summary>
    public int LogRetentionDays { get; init; } = 30;

    /// <summary>
    /// Windowsログイン時の自動起動が有効かどうかを取得または設定します。
    /// </summary>
    public bool AutoStartEnabled { get; init; } = true;

    /// <summary>
    /// 保存するログの最小レベルを取得または設定します。
    /// </summary>
    public string MinimumLogLevel { get; init; } = "Information";

    /// <summary>
    /// 仕様書に定義された初期値を持つ設定を生成します。
    /// </summary>
    /// <returns>初期設定です。</returns>
    public static AppSettings CreateDefault() => new();

    /// <summary>
    /// 永続化可能な設定値かどうかを検証します。
    /// </summary>
    /// <returns>すべての値が有効ならtrueです。</returns>
    public bool IsValid()
    {
        return SchemaVersion == CurrentSchemaVersion
            && NotificationThresholdPercent is >= 1 and <= 100
            && WeeklyWarningThresholdPercent is >= 0 and <= 100
            && FallbackPollingMinutes >= 1
            && ResetCheckDelaySeconds >= 0
            && HistoryRetentionDays >= 1
            && LogRetentionDays >= 1
            && !string.IsNullOrWhiteSpace(MinimumLogLevel);
    }
}
