using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Application.Notifications;

/// <summary>
/// 通知の成立条件や識別子に含めない、配送時点の補助表示情報を保持します。
/// </summary>
public sealed record RateLimitNotificationDisplayContext
{
    /// <summary>週間枠を観測できない場合に使用する空の表示Contextを取得します。</summary>
    public static RateLimitNotificationDisplayContext Empty { get; } = new();

    /// <summary>同じ正常取得で観測した週間枠の残量を取得または設定します。</summary>
    public double? WeeklyRemainingPercent { get; init; }

    /// <summary>
    /// 配送判断に使用する正常取得Snapshotから、既存の週間枠候補を抽出します。
    /// </summary>
    /// <param name="snapshot">配送時点の全利用枠Snapshotです。</param>
    /// <returns>週間枠がなければ未観測を表す表示Contextです。</returns>
    public static RateLimitNotificationDisplayContext FromSnapshot(UsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new RateLimitNotificationDisplayContext
        {
            WeeklyRemainingPercent = snapshot.WeeklyCandidate?.RemainingPercent,
        };
    }

    /// <summary>
    /// 週間枠残量を百分率、または値がない場合は未観測として整形します。
    /// </summary>
    /// <param name="formatProvider">数値整形に使用するカルチャーです。</param>
    /// <returns>百分率記号を含む残量、または「未観測」です。</returns>
    public string FormatWeeklyRemainingPercent(IFormatProvider formatProvider)
    {
        ArgumentNullException.ThrowIfNull(formatProvider);
        return WeeklyRemainingPercent is null
            ? "未観測"
            : $"{WeeklyRemainingPercent.Value.ToString("0.##", formatProvider)}%";
    }
}
