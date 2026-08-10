namespace CodexUsageNotifier.Application.Startup;

/// <summary>
/// 現在ユーザーのWindowsログイン時自動起動を管理します。
/// </summary>
public interface IAutoStartManager
{
    /// <summary>
    /// 現在の実行ファイルが自動起動へ正しく登録されているか確認します。
    /// </summary>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    /// <returns>正しい登録が存在する場合はtrueです。</returns>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 設定値と実際のWindows登録状態を比較します。
    /// </summary>
    /// <param name="expectedEnabled">設定ファイル上の有効状態です。</param>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    /// <returns>比較結果です。</returns>
    Task<AutoStartStatus> GetStatusAsync(bool expectedEnabled, CancellationToken cancellationToken);

    /// <summary>
    /// 現在の実行ファイルをWindows自動起動へ登録します。
    /// </summary>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    /// <returns>登録結果です。</returns>
    Task<AutoStartOperationResult> EnableAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Windows自動起動の登録を削除します。
    /// </summary>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    /// <returns>削除結果です。</returns>
    Task<AutoStartOperationResult> DisableAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Windows自動起動状態を設定値へ同期します。
    /// </summary>
    /// <param name="enabled">同期後に有効にする場合はtrueです。</param>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    /// <returns>同期結果です。</returns>
    Task<AutoStartOperationResult> SynchronizeAsync(bool enabled, CancellationToken cancellationToken);
}
