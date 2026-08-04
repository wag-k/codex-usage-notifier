namespace CodexUsageNotifier.Domain.Models;

/// <summary>
/// 1つのlimitIdのprimaryまたはsecondary位置にある利用枠を表します。
/// </summary>
public sealed class RateLimitWindow
{
    /// <summary>
    /// App Serverが返した利用枠識別子を取得または設定します。
    /// </summary>
    public string? LimitId { get; init; }

    /// <summary>
    /// App Serverが返した利用枠表示名を取得または設定します。
    /// </summary>
    public string? LimitName { get; init; }

    /// <summary>
    /// App Serverレスポンス内の位置を取得または設定します。
    /// </summary>
    public RateLimitPosition Position { get; init; }

    /// <summary>
    /// ウィンドウ長から判定した分類を取得または設定します。
    /// </summary>
    public RateLimitClassification Classification { get; init; }

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

    /// <summary>
    /// App Serverが返したプラン種別を取得または設定します。
    /// </summary>
    public string? PlanType { get; init; }

    /// <summary>
    /// App Serverが返した利用枠到達理由を取得または設定します。
    /// </summary>
    public string? RateLimitReachedType { get; init; }
}

/// <summary>
/// App Serverレスポンス内で利用枠が格納されていた位置を表します。
/// </summary>
public enum RateLimitPosition
{
    /// <summary>
    /// primary位置を表します。
    /// </summary>
    Primary,

    /// <summary>
    /// secondary位置を表します。
    /// </summary>
    Secondary
}

/// <summary>
/// ウィンドウ長から識別した利用枠の分類を表します。
/// </summary>
public enum RateLimitClassification
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
