namespace CodexUsageNotifier.Application.Maintenance;

/// <summary>
/// 日付別アプリケーションログの保持期間を適用します。
/// </summary>
public interface ILogMaintenance
{
    /// <summary>
    /// 指定保持日数より古い対象ログを削除します。
    /// </summary>
    /// <param name="retentionDays">ログを保持する日数です。</param>
    /// <param name="currentLocalTime">ファイル名の日付判定に使用する現在ローカル時刻です。</param>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    /// <returns>削除件数と失敗件数です。</returns>
    Task<LogMaintenanceResult> MaintainAsync(
        int retentionDays,
        DateTimeOffset currentLocalTime,
        CancellationToken cancellationToken);
}

/// <summary>
/// ログ保守の処理結果を表します。
/// </summary>
public sealed record LogMaintenanceResult
{
    /// <summary>削除した対象ログファイル数を取得または設定します。</summary>
    public int DeletedFileCount { get; init; }

    /// <summary>削除に失敗して保持した対象ログファイル数を取得または設定します。</summary>
    public int FailedFileCount { get; init; }
}
