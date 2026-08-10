using System.Text.Json.Serialization;

namespace CodexUsageNotifier.Infrastructure.Codex;

/// <summary>
/// account/rateLimits/readのresultフィールドを表します。
/// </summary>
internal sealed class CodexRateLimitResponse
{
    /// <summary>
    /// 後方互換用の単一利用枠を取得または設定します。
    /// </summary>
    [JsonPropertyName("rateLimits")]
    public CodexRateLimitSnapshot? RateLimits { get; init; }

    /// <summary>
    /// limitId別の現在の利用枠を取得または設定します。
    /// </summary>
    [JsonPropertyName("rateLimitsByLimitId")]
    public Dictionary<string, CodexRateLimitSnapshot?>? RateLimitsByLimitId { get; init; }

    /// <summary>
    /// 利用可能なrate-limit reset credit数の概要を取得または設定します。
    /// </summary>
    [JsonPropertyName("rateLimitResetCredits")]
    public CodexRateLimitResetCredits? RateLimitResetCredits { get; init; }
}

/// <summary>
/// 1つのlimitIdに対応する利用枠スナップショットを表します。
/// </summary>
internal sealed class CodexRateLimitSnapshot
{
    /// <summary>
    /// 利用枠識別子を取得または設定します。
    /// </summary>
    [JsonPropertyName("limitId")]
    public string? LimitId { get; init; }

    /// <summary>
    /// 利用枠表示名を取得または設定します。
    /// </summary>
    [JsonPropertyName("limitName")]
    public string? LimitName { get; init; }

    /// <summary>
    /// App Serverが返したプラン種別を取得または設定します。
    /// </summary>
    [JsonPropertyName("planType")]
    public string? PlanType { get; init; }

    /// <summary>
    /// App Serverが返した利用枠到達理由を取得または設定します。
    /// </summary>
    [JsonPropertyName("rateLimitReachedType")]
    public string? RateLimitReachedType { get; init; }

    /// <summary>
    /// primary位置のウィンドウを取得または設定します。
    /// </summary>
    [JsonPropertyName("primary")]
    public CodexRateLimitWindow? Primary { get; init; }

    /// <summary>
    /// secondary位置のウィンドウを取得または設定します。
    /// </summary>
    [JsonPropertyName("secondary")]
    public CodexRateLimitWindow? Secondary { get; init; }
}

/// <summary>
/// App Serverが返す1つの利用枠ウィンドウを表します。
/// </summary>
internal sealed class CodexRateLimitWindow
{
    /// <summary>
    /// 使用率を取得または設定します。
    /// </summary>
    [JsonPropertyName("usedPercent")]
    public double UsedPercent { get; init; }

    /// <summary>
    /// ウィンドウ長を分単位で取得または設定します。
    /// </summary>
    [JsonPropertyName("windowDurationMins")]
    public long? WindowDurationMins { get; init; }

    /// <summary>
    /// Unix秒のリセット時刻を取得または設定します。
    /// </summary>
    [JsonPropertyName("resetsAt")]
    public long? ResetsAt { get; init; }
}

/// <summary>
/// 利用可能なrate-limit reset credit数の概要を表します。
/// </summary>
internal sealed class CodexRateLimitResetCredits
{
    /// <summary>
    /// 利用可能なrate-limit reset credit数を取得または設定します。
    /// </summary>
    [JsonPropertyName("availableCount")]
    public long AvailableCount { get; init; }
}
