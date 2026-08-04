namespace CodexUsageNotifier.Domain.Models;

/// <summary>
/// 1回の取得で観測したすべての利用枠を履歴として表します。
/// </summary>
public sealed class UsageHistoryEntry
{
    /// <summary>
    /// 利用枠を取得したUTC時刻を取得または設定します。
    /// </summary>
    public DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>
    /// 同じ取得処理で観測した利用枠を取得または設定します。
    /// </summary>
    public IReadOnlyList<RateLimitObservation> RateLimits { get; init; } = Array.Empty<RateLimitObservation>();
}

/// <summary>
/// 履歴へ保存する1つの利用枠観測値を表します。
/// </summary>
public sealed class RateLimitObservation
{
    /// <summary>
    /// App Serverが返した利用枠識別子を取得または設定します。
    /// </summary>
    public string? LimitId { get; init; }

    /// <summary>
    /// App Serverレスポンス内の位置を取得または設定します。
    /// </summary>
    public RateLimitPosition Position { get; init; }

    /// <summary>
    /// ウィンドウ長を分単位で取得または設定します。
    /// </summary>
    public int? WindowDurationMinutes { get; init; }

    /// <summary>
    /// 使用率を取得または設定します。
    /// </summary>
    public double UsedPercent { get; init; }

    /// <summary>
    /// UTCのリセット時刻を取得または設定します。
    /// </summary>
    public DateTimeOffset? ResetsAtUtc { get; init; }

    /// <summary>
    /// ウィンドウ長から判定した分類を取得または設定します。
    /// </summary>
    public RateLimitClassification Classification { get; init; }
}
