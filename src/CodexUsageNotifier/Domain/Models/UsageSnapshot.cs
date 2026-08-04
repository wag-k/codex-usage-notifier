namespace CodexUsageNotifier.Domain.Models;

/// <summary>
/// ある時点で取得したCodexの全利用枠スナップショットを表します。
/// </summary>
public sealed class UsageSnapshot
{
    /// <summary>
    /// UTCの取得時刻を取得または設定します。
    /// </summary>
    public DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>
    /// App Serverから取得したすべての利用枠を取得または設定します。
    /// </summary>
    public IReadOnlyList<RateLimitWindow> RateLimits { get; init; } = Array.Empty<RateLimitWindow>();

    /// <summary>
    /// 最初に観測された300分の5時間枠候補を取得します。
    /// </summary>
    public RateLimitWindow? FiveHourCandidate => RateLimits.FirstOrDefault(
        window => window.Classification == RateLimitClassification.FiveHour);

    /// <summary>
    /// 最初に観測された10080分の週間枠候補を取得します。
    /// </summary>
    public RateLimitWindow? WeeklyCandidate => RateLimits.FirstOrDefault(
        window => window.Classification == RateLimitClassification.Weekly);

    /// <summary>
    /// リセット回数を取得または設定します。
    /// </summary>
    public int? ResetCredits { get; init; }

    /// <summary>
    /// 取得契機を取得または設定します。
    /// </summary>
    public UsageCheckTrigger Trigger { get; init; }
}

/// <summary>
/// 利用枠を取得した契機を表します。
/// </summary>
public enum UsageCheckTrigger
{
    /// <summary>
    /// 取得契機が不明であることを表します。
    /// </summary>
    Unknown,

    /// <summary>
    /// アプリケーション起動による取得を表します。
    /// </summary>
    Startup,

    /// <summary>
    /// ユーザー操作による取得を表します。
    /// </summary>
    Manual,

    /// <summary>
    /// 定期確認による取得を表します。
    /// </summary>
    Scheduled,

    /// <summary>
    /// スリープ復帰による取得を表します。
    /// </summary>
    Resume
}
