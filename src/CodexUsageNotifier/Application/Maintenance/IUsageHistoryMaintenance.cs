namespace CodexUsageNotifier.Application.Maintenance;

/// <summary>
/// 利用履歴JSONLから保持期間外の取得行を安全に整理します。
/// </summary>
public interface IUsageHistoryMaintenance
{
    /// <summary>
    /// 指定境界より古い正常な履歴行を削除します。
    /// </summary>
    /// <param name="retainedFromUtc">このUTC時刻以降を保持する境界です。</param>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    /// <returns>保守で削除・保持した行数です。</returns>
    Task<UsageHistoryMaintenanceResult> MaintainAsync(
        DateTimeOffset retainedFromUtc,
        CancellationToken cancellationToken);
}

/// <summary>
/// 利用履歴保守の処理結果を表します。
/// </summary>
public sealed record UsageHistoryMaintenanceResult
{
    /// <summary>保持期間外として削除した正常行数を取得または設定します。</summary>
    public int DeletedLineCount { get; init; }

    /// <summary>保持した全行数を取得または設定します。</summary>
    public int RetainedLineCount { get; init; }

    /// <summary>データ損失防止のため保持した破損行数を取得または設定します。</summary>
    public int CorruptedLineCount { get; init; }
}
