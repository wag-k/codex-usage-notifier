using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CodexUsageNotifier.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier.Infrastructure.Codex;

/// <summary>
/// リダイレクトした標準入出力を持つCodex App Server子プロセスを起動します。
/// </summary>
public sealed partial class CodexAppServerProcessFactory : ICodexAppServerProcessFactory
{
    private readonly CodexAppServerOptions options;
    private readonly ILogger<CodexAppServerProcessFactory> logger;

    /// <summary>
    /// 起動設定とロガーを受け取ってファクトリを初期化します。
    /// </summary>
    /// <param name="options">App Serverの起動設定です。</param>
    /// <param name="logger">起動結果を記録するロガーです。</param>
    public CodexAppServerProcessFactory(
        CodexAppServerOptions options,
        ILogger<CodexAppServerProcessFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        this.options = options;
        this.logger = logger;
    }

    /// <summary>
    /// 新しいApp Server子プロセスを起動します。
    /// </summary>
    /// <param name="cancellationToken">起動のキャンセル通知です。</param>
    /// <returns>本アプリが所有する子プロセスです。</returns>
    public Task<ICodexAppServerProcess> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            throw new InvalidOperationException("Codex CLIの実行ファイルが設定されていません。");
        }

        Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = options.ExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
            EnableRaisingEvents = true,
        };
        process.StartInfo.ArgumentList.Add("app-server");
        process.StartInfo.ArgumentList.Add("--listen");
        process.StartInfo.ArgumentList.Add("stdio://");

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Codex App Serverを起動できませんでした。");
            }

            LogProcessStarted(logger, process.Id);
            return Task.FromResult<ICodexAppServerProcess>(new CodexAppServerProcess(process));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new InvalidOperationException("Codex CLIを起動できません。インストール状態を確認してください。", exception);
        }
    }

    [LoggerMessage(2100, LogLevel.Information, "Codex App Server子プロセスを起動しました。ProcessId={ProcessId}")]
    private static partial void LogProcessStarted(ILogger logger, int processId);
}

/// <summary>
/// 本アプリが起動した1つのCodex App Server子プロセスをラップします。
/// </summary>
internal sealed class CodexAppServerProcess : ICodexAppServerProcess
{
    private readonly Process process;
    private bool disposed;

    /// <summary>
    /// 起動済みプロセスを受け取ってラッパーを初期化します。
    /// </summary>
    /// <param name="process">本アプリが起動したプロセスです。</param>
    public CodexAppServerProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        this.process = process;
    }

    /// <summary>
    /// 子プロセスのIDを取得します。
    /// </summary>
    public int Id => process.Id;

    /// <summary>
    /// 子プロセスが終了済みかどうかを取得します。
    /// </summary>
    public bool HasExited => process.HasExited;

    /// <summary>
    /// JSON-RPCを書き込む標準入力を取得します。
    /// </summary>
    public TextWriter StandardInput => process.StandardInput;

    /// <summary>
    /// JSON-RPCを読み取る標準出力を取得します。
    /// </summary>
    public TextReader StandardOutput => process.StandardOutput;

    /// <summary>
    /// 診断メッセージを読み取る標準エラー出力を取得します。
    /// </summary>
    public TextReader StandardError => process.StandardError;

    /// <summary>
    /// 標準入力を閉じて正常終了を要求します。
    /// </summary>
    public void CloseStandardInput()
    {
        if (!process.HasExited)
        {
            process.StandardInput.Close();
        }
    }

    /// <summary>
    /// 子プロセスの終了を待機します。
    /// </summary>
    /// <param name="cancellationToken">待機のキャンセル通知です。</param>
    /// <returns>終了を待つ非同期処理です。</returns>
    public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);

    /// <summary>
    /// 本アプリが起動した子プロセスツリーだけを強制終了します。
    /// </summary>
    public void KillProcessTree()
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
    }

    /// <summary>
    /// プロセスラッパーを解放します。
    /// </summary>
    /// <returns>解放完了を表す非同期処理です。</returns>
    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            process.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
