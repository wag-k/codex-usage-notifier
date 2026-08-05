namespace CodexUsageNotifier.Domain.Models;

/// <summary>
/// リセット時刻がない短期枠の回復遷移と連番を永続化します。
/// </summary>
public sealed record RateLimitRecoveryState
{
    /// <summary>
    /// App Serverが返す利用枠識別子を取得または設定します。
    /// </summary>
    public string LimitId { get; init; } = string.Empty;

    /// <summary>
    /// App Serverレスポンス内の位置を取得または設定します。
    /// </summary>
    public RateLimitPosition Position { get; init; }

    /// <summary>
    /// 利用枠の期間を分単位で取得または設定します。
    /// </summary>
    public int WindowDurationMinutes { get; init; }

    /// <summary>
    /// これまでに正常な残量を観測したかどうかを取得または設定します。
    /// </summary>
    public bool HasObservation { get; init; }

    /// <summary>
    /// 直近観測で残量が回復閾値未満だったかどうかを取得または設定します。
    /// </summary>
    public bool WasBelowThreshold { get; init; }

    /// <summary>
    /// 閾値以上への回復を観測した連番を取得または設定します。
    /// </summary>
    public int RecoverySequence { get; init; }

    /// <summary>
    /// 直近観測の残量を取得または設定します。
    /// </summary>
    public double LastRemainingPercent { get; init; }
}
