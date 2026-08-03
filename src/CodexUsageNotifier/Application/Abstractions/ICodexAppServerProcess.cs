namespace CodexUsageNotifier.Application.Abstractions;

/// <summary>
/// 本アプリが所有するCodex App Server子プロセスを表します。
/// </summary>
public interface ICodexAppServerProcess : IAsyncDisposable
{
    /// <summary>
    /// 子プロセスのIDを取得します。
    /// </summary>
    int Id { get; }

    /// <summary>
    /// 子プロセスが終了済みかどうかを取得します。
    /// </summary>
    bool HasExited { get; }

    /// <summary>
    /// JSON-RPCを書き込む標準入力を取得します。
    /// </summary>
    TextWriter StandardInput { get; }

    /// <summary>
    /// JSON-RPCを読み取る標準出力を取得します。
    /// </summary>
    TextReader StandardOutput { get; }

    /// <summary>
    /// 診断メッセージを読み取る標準エラー出力を取得します。
    /// </summary>
    TextReader StandardError { get; }

    /// <summary>
    /// 標準入力を閉じて正常終了を要求します。
    /// </summary>
    void CloseStandardInput();

    /// <summary>
    /// 子プロセスの終了を待機します。
    /// </summary>
    /// <param name="cancellationToken">待機のキャンセル通知です。</param>
    /// <returns>終了を待つ非同期処理です。</returns>
    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 本アプリが起動した子プロセスツリーだけを強制終了します。
    /// </summary>
    void KillProcessTree();
}

/// <summary>
/// Codex App Server子プロセスを生成する処理を表します。
/// </summary>
public interface ICodexAppServerProcessFactory
{
    /// <summary>
    /// 新しいApp Server子プロセスを起動します。
    /// </summary>
    /// <param name="cancellationToken">起動のキャンセル通知です。</param>
    /// <returns>本アプリが所有する子プロセスです。</returns>
    Task<ICodexAppServerProcess> StartAsync(CancellationToken cancellationToken);
}
