namespace CodexUsageNotifier.Application.Maintenance;

/// <summary>
/// 起動時と24時間ごとの運用保守を管理します。
/// </summary>
public interface IApplicationMaintenanceService : IAsyncDisposable
{
    /// <summary>バックグラウンドの保守スケジュールを開始します。</summary>
    void Start();

    /// <summary>
    /// 前回保守から24時間以上経過している場合だけ保守を実行します。
    /// </summary>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    /// <returns>今回保守を実行した場合はtrueです。</returns>
    Task<bool> RunIfDueAsync(CancellationToken cancellationToken);
}
