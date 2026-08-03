using System.Text.Json;
using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Domain.Models;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier.Infrastructure.Codex;

/// <summary>
/// Codex App Server子プロセスの所有、初期化、および利用枠要求を管理します。
/// </summary>
internal sealed partial class CodexAppServerClient : ICodexRateLimitClient, IAsyncDisposable
{
    private readonly ICodexAppServerProcessFactory processFactory;
    private readonly CodexAppServerOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<CodexAppServerClient> logger;
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private ICodexAppServerProcess? process;
    private JsonRpcConnection? connection;
    private CancellationTokenSource? processTasksCancellation;
    private Task? standardErrorTask;
    private Task? processExitTask;
    private bool disposing;

    /// <summary>
    /// App Serverから利用枠更新通知を受信したときに発生します。
    /// </summary>
    public event EventHandler? RateLimitsUpdated;

    /// <summary>
    /// App Serverとの接続が予期せず失われたときに発生します。
    /// </summary>
    public event EventHandler? ConnectionLost;

    /// <summary>
    /// 本アプリが起動したApp ServerのプロセスIDを取得します。
    /// </summary>
    public int? ProcessId => process?.Id;

    /// <summary>
    /// プロセス生成、通信設定、時刻、およびログ基盤を受け取って初期化します。
    /// </summary>
    /// <param name="processFactory">本アプリ所有の子プロセスを生成するファクトリです。</param>
    /// <param name="options">起動と通信のタイムアウト設定です。</param>
    /// <param name="timeProvider">時刻とタイムアウトを提供する実装です。</param>
    /// <param name="loggerFactory">通信層のロガーを生成するファクトリです。</param>
    /// <param name="logger">クライアントの診断情報を記録するロガーです。</param>
    public CodexAppServerClient(
        ICodexAppServerProcessFactory processFactory,
        CodexAppServerOptions options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        ILogger<CodexAppServerClient> logger)
    {
        ArgumentNullException.ThrowIfNull(processFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(logger);
        this.processFactory = processFactory;
        this.options = options;
        this.timeProvider = timeProvider;
        this.loggerFactory = loggerFactory;
        this.logger = logger;
    }

    /// <summary>
    /// App Serverを必要に応じて起動・初期化し、現在の利用枠を取得します。
    /// </summary>
    /// <param name="trigger">利用枠を取得する契機です。</param>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    /// <returns>内部モデルへ変換された利用枠です。</returns>
    public async Task<UsageSnapshot> ReadAsync(
        UsageCheckTrigger trigger,
        CancellationToken cancellationToken)
    {
        JsonRpcConnection activeConnection = await EnsureConnectedAsync(cancellationToken);
        try
        {
            JsonElement result = await activeConnection.SendRequestAsync(
                "account/rateLimits/read",
                new { },
                options.RequestTimeout,
                cancellationToken);
            CodexRateLimitResponse? response = JsonSerializer.Deserialize<CodexRateLimitResponse>(result.GetRawText());
            if (response is null)
            {
                throw new InvalidDataException("利用枠レスポンスを解釈できませんでした。");
            }

            UsageSnapshot snapshot = CodexRateLimitMapper.Map(response, trigger, timeProvider.GetUtcNow());
            LogRateLimitDiagnostics(snapshot);
            return snapshot;
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested)
        {
            await ResetConnectionAfterFailureAsync(activeConnection);
            throw;
        }
    }

    /// <summary>
    /// 利用可能な初期化済み接続を返し、必要な場合はApp Serverを新規起動します。
    /// </summary>
    /// <param name="cancellationToken">起動と初期化のキャンセル通知です。</param>
    /// <returns>初期化済みのJSON-RPC接続です。</returns>
    private async Task<JsonRpcConnection> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposing, this);
        await connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (connection is not null && process is not null && !process.HasExited)
            {
                return connection;
            }

            await CleanupCurrentProcessAsync(requestGracefulShutdown: false);
            await StartAndInitializeAsync(cancellationToken);
            return connection ?? throw new InvalidOperationException("App Server接続を初期化できませんでした。");
        }
        finally
        {
            connectionGate.Release();
        }
    }

    /// <summary>
    /// App Serverを起動し、initialize成功後にinitialized通知を送信します。
    /// </summary>
    /// <param name="cancellationToken">起動と初期化のキャンセル通知です。</param>
    private async Task StartAndInitializeAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource startCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<ICodexAppServerProcess> startTask = Task.Run(
            () => processFactory.StartAsync(startCancellation.Token),
            CancellationToken.None);
        try
        {
            process = await startTask.WaitAsync(options.StartupTimeout, timeProvider, cancellationToken);
        }
        catch
        {
            startCancellation.Cancel();
            _ = DisposeLateStartedProcessAsync(startTask);
            throw;
        }

        processTasksCancellation = new CancellationTokenSource();
        connection = new JsonRpcConnection(
            process.StandardOutput,
            process.StandardInput,
            timeProvider,
            loggerFactory.CreateLogger<JsonRpcConnection>());
        connection.NotificationReceived += OnNotificationReceived;
        connection.Start();
        standardErrorTask = Task.Run(
            () => PumpStandardErrorAsync(process, processTasksCancellation.Token),
            CancellationToken.None);
        processExitTask = Task.Run(
            () => WatchProcessExitAsync(process, connection, processTasksCancellation.Token),
            CancellationToken.None);

        try
        {
            await connection.SendRequestAsync(
                "initialize",
                new
                {
                    clientInfo = new
                    {
                        name = "codex_usage_notifier",
                        title = "Codex Usage Notifier",
                        version = "0.2.0",
                    },
                    capabilities = new
                    {
                        experimentalApi = false,
                    },
                },
                options.InitializeTimeout,
                cancellationToken);
            await connection.SendNotificationAsync("initialized", parameters: null, cancellationToken);
            LogInitialized(logger, process.Id);
        }
        catch
        {
            await CleanupCurrentProcessAsync(requestGracefulShutdown: true);
            throw;
        }
    }

    /// <summary>
    /// 起動タイムアウト後に遅れて生成された子プロセスを確実に終了します。
    /// </summary>
    /// <param name="startTask">完了待ちのプロセス起動処理です。</param>
    private static async Task DisposeLateStartedProcessAsync(Task<ICodexAppServerProcess> startTask)
    {
        ArgumentNullException.ThrowIfNull(startTask);
        try
        {
            ICodexAppServerProcess lateProcess = await startTask;
            lateProcess.KillProcessTree();
            await lateProcess.DisposeAsync();
        }
        catch (Exception exception) when (
            exception is OperationCanceledException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>
    /// 標準エラー出力を機密情報の可能性を判定して診断ログへ転送します。
    /// </summary>
    /// <param name="ownedProcess">本アプリが所有する子プロセスです。</param>
    /// <param name="cancellationToken">転送停止の通知です。</param>
    private async Task PumpStandardErrorAsync(
        ICodexAppServerProcess ownedProcess,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownedProcess);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await ownedProcess.StandardError.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    return;
                }

                LogStandardError(logger, SanitizeDiagnosticLine(line));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException exception)
        {
            LogStandardErrorReadFailed(logger, exception);
        }
    }

    /// <summary>
    /// 所有する子プロセスの予期しない終了を検知し、待機中の要求を失敗させます。
    /// </summary>
    /// <param name="ownedProcess">監視する子プロセスです。</param>
    /// <param name="ownedConnection">失敗させるJSON-RPC接続です。</param>
    /// <param name="cancellationToken">監視停止の通知です。</param>
    private async Task WatchProcessExitAsync(
        ICodexAppServerProcess ownedProcess,
        JsonRpcConnection ownedConnection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownedProcess);
        ArgumentNullException.ThrowIfNull(ownedConnection);
        try
        {
            await ownedProcess.WaitForExitAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            ownedConnection.Fail(new EndOfStreamException("Codex App Serverが終了しました。"));
            LogUnexpectedExit(logger, ownedProcess.Id);
            if (!disposing)
            {
                ConnectionLost?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// App Server通知を監視し、利用枠更新通知だけを上位層へ伝えます。
    /// </summary>
    /// <param name="method">通知メソッド名です。</param>
    /// <param name="parameters">通知パラメーターです。</param>
    private void OnNotificationReceived(string method, JsonElement? parameters)
    {
        if (string.Equals(method, "account/rateLimits/updated", StringComparison.Ordinal))
        {
            LogRateLimitsUpdated(logger);
            RateLimitsUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 要求失敗時に同じ接続を再利用しないよう、該当する接続だけを破棄します。
    /// </summary>
    /// <param name="failedConnection">失敗した接続です。</param>
    private async Task ResetConnectionAfterFailureAsync(JsonRpcConnection failedConnection)
    {
        ArgumentNullException.ThrowIfNull(failedConnection);
        await connectionGate.WaitAsync();
        try
        {
            if (ReferenceEquals(connection, failedConnection))
            {
                await CleanupCurrentProcessAsync(requestGracefulShutdown: true);
            }
        }
        finally
        {
            connectionGate.Release();
        }
    }

    /// <summary>
    /// 現在所有する接続と子プロセスだけを停止・解放します。
    /// </summary>
    /// <param name="requestGracefulShutdown">標準入力を閉じて正常終了を待つかどうかです。</param>
    private async Task CleanupCurrentProcessAsync(bool requestGracefulShutdown)
    {
        ICodexAppServerProcess? ownedProcess = process;
        JsonRpcConnection? ownedConnection = connection;
        CancellationTokenSource? ownedCancellation = processTasksCancellation;
        Task? ownedStandardErrorTask = standardErrorTask;
        Task? ownedExitTask = processExitTask;

        process = null;
        connection = null;
        processTasksCancellation = null;
        standardErrorTask = null;
        processExitTask = null;

        ownedCancellation?.Cancel();
        if (ownedConnection is not null)
        {
            ownedConnection.NotificationReceived -= OnNotificationReceived;
            await ownedConnection.DisposeAsync();
        }

        if (ownedProcess is not null)
        {
            if (requestGracefulShutdown && !ownedProcess.HasExited)
            {
                ownedProcess.CloseStandardInput();
                try
                {
                    await ownedProcess.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(options.ShutdownTimeout, timeProvider);
                }
                catch (TimeoutException)
                {
                    LogForcedTermination(logger, ownedProcess.Id);
                    ownedProcess.KillProcessTree();
                }
            }
            else if (!ownedProcess.HasExited)
            {
                ownedProcess.KillProcessTree();
            }

            await ownedProcess.DisposeAsync();
        }

        await IgnoreCancellationAsync(ownedStandardErrorTask);
        await IgnoreCancellationAsync(ownedExitTask);
        ownedCancellation?.Dispose();
    }

    /// <summary>
    /// バックグラウンド処理のキャンセル完了だけを無視して待機します。
    /// </summary>
    /// <param name="task">待機する処理です。</param>
    private static async Task IgnoreCancellationAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// 診断行から機密情報の可能性がある内容を除外し、長さを制限します。
    /// </summary>
    /// <param name="line">標準エラー出力の1行です。</param>
    /// <returns>ログへ安全に出力できる診断文字列です。</returns>
    private static string SanitizeDiagnosticLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string[] sensitiveMarkers =
        [
            "authorization",
            "bearer",
            "token",
            "api_key",
            "apikey",
            "secret",
            "password",
            "credential",
            "cookie",
        ];
        if (line.Contains('@', StringComparison.Ordinal)
            || sensitiveMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return "[機密情報を含む可能性があるため省略]";
        }

        const int maximumLength = 1000;
        return line.Length <= maximumLength ? line : string.Concat(line.AsSpan(0, maximumLength), "…");
    }

    /// <summary>
    /// 取得した利用枠の識別に必要な非機密フィールドだけを診断ログへ記録します。
    /// </summary>
    /// <param name="snapshot">記録する利用枠です。</param>
    private void LogRateLimitDiagnostics(UsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        IEnumerable<RateLimitWindow> windows = new[] { snapshot.Primary, snapshot.Secondary }
            .Where(window => window is not null)
            .Cast<RateLimitWindow>()
            .Concat(snapshot.UnknownWindows);
        foreach (RateLimitWindow window in windows)
        {
            LogRateLimitWindow(
                logger,
                window.LimitId ?? "(null)",
                window.Source,
                window.Kind,
                window.WindowDurationMinutes,
                window.UsedPercent,
                window.ResetsAtUtc);
        }
    }

    /// <summary>
    /// 本アプリが所有するApp Serverを正常終了し、残った場合はそのプロセスツリーだけを終了します。
    /// </summary>
    /// <returns>終了処理を表す非同期処理です。</returns>
    public async ValueTask DisposeAsync()
    {
        if (disposing)
        {
            return;
        }

        disposing = true;
        await connectionGate.WaitAsync();
        try
        {
            await CleanupCurrentProcessAsync(requestGracefulShutdown: true);
        }
        finally
        {
            connectionGate.Release();
            connectionGate.Dispose();
        }
    }

    [LoggerMessage(2120, LogLevel.Information, "Codex App Serverの初期化が完了しました。ProcessId={ProcessId}")]
    private static partial void LogInitialized(ILogger logger, int processId);

    [LoggerMessage(2121, LogLevel.Debug, "Codex App Server stderr: {Diagnostic}")]
    private static partial void LogStandardError(ILogger logger, string diagnostic);

    [LoggerMessage(2122, LogLevel.Warning, "Codex App Serverの標準エラー出力を読み取れませんでした。")]
    private static partial void LogStandardErrorReadFailed(ILogger logger, Exception exception);

    [LoggerMessage(2123, LogLevel.Warning, "Codex App Serverが予期せず終了しました。ProcessId={ProcessId}")]
    private static partial void LogUnexpectedExit(ILogger logger, int processId);

    [LoggerMessage(2124, LogLevel.Debug, "account/rateLimits/updatedを受信しました。再取得を予約します。")]
    private static partial void LogRateLimitsUpdated(ILogger logger);

    [LoggerMessage(2125, LogLevel.Warning, "正常終了しないCodex App Serverを所有プロセスツリー単位で終了します。ProcessId={ProcessId}")]
    private static partial void LogForcedTermination(ILogger logger, int processId);

    [LoggerMessage(2126, LogLevel.Information, "利用枠診断: LimitId={LimitId}, Source={Source}, Kind={Kind}, WindowDurationMins={WindowDurationMins}, UsedPercent={UsedPercent}, ResetsAtUtc={ResetsAtUtc}")]
    private static partial void LogRateLimitWindow(
        ILogger logger,
        string limitId,
        RateLimitWindowSource source,
        RateLimitWindowKind kind,
        int? windowDurationMins,
        double usedPercent,
        DateTimeOffset? resetsAtUtc);
}
