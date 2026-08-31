using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Application.Abstractions;

/// <summary>
/// 保存済みの利用枠観測履歴を指定期間で読み取る処理を表します。
/// </summary>
public interface IUsageHistoryReader
{
    /// <summary>
    /// 指定したUTC期間に含まれる取得履歴を読み取ります。
    /// </summary>
    /// <param name="fromUtc">読み取り開始UTC時刻です。</param>
    /// <param name="toUtc">読み取り終了UTC時刻です。</param>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    /// <returns>取得時刻の昇順に並んだ履歴です。</returns>
    Task<IReadOnlyList<UsageHistoryEntry>> ReadAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken);
}
