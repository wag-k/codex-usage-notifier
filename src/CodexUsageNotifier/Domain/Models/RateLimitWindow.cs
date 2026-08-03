namespace CodexUsageNotifier.Domain.Models;

/// <summary>
/// 1つのCodex利用制限枠の値を表します。
/// </summary>
public sealed class RateLimitWindow
{
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
