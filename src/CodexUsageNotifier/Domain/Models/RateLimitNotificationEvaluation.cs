namespace CodexUsageNotifier.Domain.Models;

/// <summary>
/// 全利用枠の通知候補と更新後の回復状態をまとめます。
/// </summary>
public sealed class RateLimitNotificationEvaluation
{
    /// <summary>
    /// 現在送信または保留できる通知候補を取得または設定します。
    /// </summary>
    public IReadOnlyList<RateLimitNotificationCandidate> Candidates { get; init; } =
        Array.Empty<RateLimitNotificationCandidate>();

    /// <summary>
    /// 永続化する利用枠別の回復状態を取得または設定します。
    /// </summary>
    public IReadOnlyList<RateLimitRecoveryState> RecoveryStates { get; init; } =
        Array.Empty<RateLimitRecoveryState>();
}
