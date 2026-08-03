namespace CodexUsageNotifier.Domain.Models;

/// <summary>
/// ある時点で取得したCodex利用枠のスナップショットを表します。
/// </summary>
public sealed class UsageSnapshot
{
    /// <summary>
    /// UTCの取得時刻を取得または設定します。
    /// </summary>
    public DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>
    /// 5時間枠を取得または設定します。
    /// </summary>
    public RateLimitWindow? Primary { get; init; }

    /// <summary>
    /// 週間枠を取得または設定します。
    /// </summary>
    public RateLimitWindow? Secondary { get; init; }

    /// <summary>
    /// リセット回数を取得または設定します。
    /// </summary>
    public int? ResetCredits { get; init; }

    /// <summary>
    /// 取得契機を取得または設定します。
    /// </summary>
    public UsageCheckTrigger Trigger { get; init; }

    /// <summary>
    /// App Serverが返した制限識別子を取得または設定します。
    /// </summary>
    public string? RawLimitId { get; init; }

    /// <summary>
    /// 既知の5時間枠・週間枠として識別できなかった利用枠を取得または設定します。
    /// </summary>
    public IReadOnlyList<RateLimitWindow> UnknownWindows { get; init; } = Array.Empty<RateLimitWindow>();
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
