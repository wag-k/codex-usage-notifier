namespace CodexUsageNotifier.Domain.Models;

/// <summary>
/// アプリケーション設定の保存モデルを表します。
/// </summary>
public sealed record AppSettings
{
    private static readonly HashSet<string> SupportedLogLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Trace",
        "Debug",
        "Information",
        "Warning",
        "Error",
        "Critical",
        "None",
    };

    /// <summary>
    /// 現在の設定スキーマのバージョンです。
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// 設定スキーマのバージョンを取得または設定します。
    /// </summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>
    /// PATH上のコマンド名、またはCodex CLI実行ファイルのパスを取得または設定します。
    /// </summary>
    public string CodexExecutablePath { get; init; } = "codex";

    /// <summary>
    /// 利用枠ごとに上書きする通知設定を取得または設定します。
    /// </summary>
    public IReadOnlyList<RateLimitNotificationSetting> RateLimitNotifications { get; init; } =
        Array.Empty<RateLimitNotificationSetting>();

    /// <summary>
    /// FiveHour枠の短期回復通知を既定で有効にするかどうかを取得または設定します。
    /// </summary>
    public bool ShortWindowRecoveryEnabled { get; init; } = true;

    /// <summary>
    /// 短期枠の回復通知に使用する残量閾値を取得または設定します。
    /// </summary>
    public int ShortWindowRecoveryThresholdPercent { get; init; } = 99;

    /// <summary>
    /// Weekly枠のEarly通知を既定で有効にするかどうかを取得または設定します。
    /// </summary>
    public bool LongWindowEarlyWarningEnabled { get; init; } = true;

    /// <summary>
    /// 長期枠の早期通知に使用する残量閾値を取得または設定します。
    /// </summary>
    public int LongWindowEarlyWarningThresholdPercent { get; init; } = 75;

    /// <summary>
    /// 長期枠の早期通知を開始する残り時間を時間単位で取得または設定します。
    /// </summary>
    public int LongWindowEarlyWarningHours { get; init; } = 120;

    /// <summary>
    /// Weekly枠のStandard通知を既定で有効にするかどうかを取得または設定します。
    /// </summary>
    public bool LongWindowStandardWarningEnabled { get; init; } = true;

    /// <summary>
    /// 長期枠の通常通知に使用する残量閾値を取得または設定します。
    /// </summary>
    public int LongWindowStandardWarningThresholdPercent { get; init; } = 20;

    /// <summary>
    /// 長期枠の通常通知を開始する残り時間を時間単位で取得または設定します。
    /// </summary>
    public int LongWindowStandardWarningHours { get; init; } = 24;

    /// <summary>
    /// Weekly枠のFinal通知を既定で有効にするかどうかを取得または設定します。
    /// </summary>
    public bool LongWindowFinalWarningEnabled { get; init; } = true;

    /// <summary>
    /// 長期枠の最終通知に使用する残量閾値を取得または設定します。
    /// </summary>
    public int LongWindowFinalWarningThresholdPercent { get; init; } = 10;

    /// <summary>
    /// 長期枠の最終通知を開始する残り時間を時間単位で取得または設定します。
    /// </summary>
    public int LongWindowFinalWarningHours { get; init; } = 6;

    /// <summary>
    /// Weekly枠のリセット完了通知を既定で有効にするかどうかを取得または設定します。
    /// </summary>
    public bool LongWindowResetCompletedEnabled { get; init; } = true;

    /// <summary>
    /// 使用率低下からリセット完了を推定する最小ポイント数を取得または設定します。
    /// </summary>
    public int ResetInferenceUsageDropPoints { get; init; } = 50;

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
    /// 設定ファイルから読み込んだ個別フォールバック対象を安全な初期値へ補正します。
    /// </summary>
    /// <returns>使用率低下推定閾値が有効範囲内に補正された設定です。</returns>
    public AppSettings NormalizeLoadedValues()
    {
        AppSettings defaults = CreateDefault();
        return this with
        {
            ResetInferenceUsageDropPoints = ResetInferenceUsageDropPoints is >= 1 and <= 100
                ? ResetInferenceUsageDropPoints
                : defaults.ResetInferenceUsageDropPoints,
            HistoryRetentionDays = HistoryRetentionDays is >= 7 and <= 3650
                ? HistoryRetentionDays
                : defaults.HistoryRetentionDays,
            LogRetentionDays = LogRetentionDays is >= 7 and <= 3650
                ? LogRetentionDays
                : defaults.LogRetentionDays,
        };
    }

    /// <summary>
    /// 永続化可能な設定値かどうかを検証します。
    /// </summary>
    /// <returns>すべての値が有効ならtrueです。</returns>
    public bool IsValid()
    {
        return SchemaVersion == CurrentSchemaVersion
            && !string.IsNullOrWhiteSpace(CodexExecutablePath)
            && RateLimitNotifications is not null
            && RateLimitNotifications.All(setting => setting is not null && setting.IsValid())
            && RateLimitNotifications
                .GroupBy(setting => new { setting.LimitId, setting.Position, setting.WindowDurationMinutes })
                .All(group => group.Count() == 1)
            && ShortWindowRecoveryThresholdPercent is >= 1 and <= 100
            && LongWindowEarlyWarningThresholdPercent is >= 1 and <= 100
            && LongWindowStandardWarningThresholdPercent is >= 1 and <= 100
            && LongWindowFinalWarningThresholdPercent is >= 1 and <= 100
            && LongWindowEarlyWarningHours > LongWindowStandardWarningHours
            && LongWindowStandardWarningHours > LongWindowFinalWarningHours
            && LongWindowFinalWarningHours > 0
            && ResetInferenceUsageDropPoints is >= 1 and <= 100
            && IsValidOptionalEmailAddress(GmailRecipient)
            && FallbackPollingMinutes is >= 1 and <= 1440
            && ResetCheckDelaySeconds >= 0
            && HistoryRetentionDays is >= 7 and <= 3650
            && LogRetentionDays is >= 7 and <= 3650
            && SupportedLogLevels.Contains(MinimumLogLevel);
    }

    /// <summary>
    /// 省略可能なGmail送信先が単純なメールアドレス形式か検証します。
    /// </summary>
    /// <param name="value">検証する送信先です。</param>
    /// <returns>未入力またはメールアドレスとして解釈できる場合はtrueです。</returns>
    public static bool IsValidOptionalEmailAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return System.Net.Mail.MailAddress.TryCreate(value, out System.Net.Mail.MailAddress? address)
            && string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase);
    }
}
