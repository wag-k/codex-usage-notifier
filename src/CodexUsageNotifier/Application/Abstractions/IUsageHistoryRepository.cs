using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Application.Abstractions;

/// <summary>
/// 利用枠の観測履歴を追記し、初めて観測した枠を返す処理を表します。
/// </summary>
public interface IUsageHistoryRepository
{
    /// <summary>
    /// 取得成功時の全利用枠を履歴へ追記します。
    /// </summary>
    /// <param name="snapshot">保存する全利用枠スナップショットです。</param>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    /// <returns>LimitId、Position、WindowDurationMinutesの組み合わせを初めて観測した枠です。</returns>
    Task<IReadOnlyList<RateLimitObservation>> AppendAsync(
        UsageSnapshot snapshot,
        CancellationToken cancellationToken);
}
