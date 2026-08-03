namespace CodexUsageNotifier.Domain.Models;

/// <summary>
/// 1つのCodex利用制限枠の値を表します。
/// </summary>
public sealed class RateLimitWindow
{
    /// <summary>
    /// この枠の識別結果を取得または設定します。
    /// </summary>
    public RateLimitWindowKind Kind { get; init; }

    /// <summary>
    /// App Serverが返した利用枠識別子を取得または設定します。
    /// </summary>
    public string? LimitId { get; init; }

    /// <summary>
    /// App Serverが返した利用枠表示名を取得または設定します。
    /// </summary>
    public string? LimitName { get; init; }

    /// <summary>
    /// App Serverの利用枠内での由来を取得または設定します。
    /// </summary>
    public RateLimitWindowSource Source { get; init; }

    /// <summary>
    /// 使用率を取得または設定します。
    /// </summary>
    public double UsedPercent { get; init; }

    /// <summary>
    /// 残量を取得または設定します。
    /// </summary>
    public double RemainingPercent { get; init; }

    /// <summary>
    /// ウィンドウ長を分単位で取得または設定します。
    /// </summary>
    public int? WindowDurationMinutes { get; init; }

    /// <summary>
    /// UTCのリセット時刻を取得または設定します。
    /// </summary>
    public DateTimeOffset? ResetsAtUtc { get; init; }
}

/// <summary>
/// ウィンドウ長から識別した利用枠の種類を表します。
/// </summary>
public enum RateLimitWindowKind
{
    /// <summary>
    /// 既知のウィンドウ長に一致しない枠を表します。
    /// </summary>
    Unknown,

    /// <summary>
    /// 300分の5時間枠候補を表します。
    /// </summary>
    FiveHour,

    /// <summary>
    /// 10080分の週間枠候補を表します。
    /// </summary>
    Weekly
}

/// <summary>
/// App Serverレスポンス内で利用枠が格納されていた位置を表します。
/// </summary>
public enum RateLimitWindowSource
{
    /// <summary>
    /// 由来が不明であることを表します。
    /// </summary>
    Unknown,

    /// <summary>
    /// primaryフィールド由来であることを表します。
    /// </summary>
    Primary,

    /// <summary>
    /// secondaryフィールド由来であることを表します。
    /// </summary>
    Secondary
}
