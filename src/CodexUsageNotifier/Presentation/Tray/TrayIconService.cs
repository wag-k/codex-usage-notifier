using System.Diagnostics;
using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Application.Monitoring;
using CodexUsageNotifier.Domain.Models;
using Microsoft.Extensions.Logging;
using Forms = System.Windows.Forms;

namespace CodexUsageNotifier.Presentation.Tray;

/// <summary>
/// タスクトレイアイコンとPhase 1で利用可能なメニューを管理します。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private static readonly Action<ILogger, Exception?> LogTrayStarted =
        LoggerMessage.Define(LogLevel.Information, new EventId(3000, "TrayStarted"), "タスクトレイへの常駐を開始しました。");

    private static readonly Action<ILogger, Exception?> LogOpenLogDirectoryFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(3001, "OpenLogDirectoryFailed"), "ログフォルダーを開けませんでした。");

    private static readonly Action<ILogger, Exception?> LogManualRefreshFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(3002, "ManualRefreshFailed"), "手動の利用枠確認に失敗しました。");

    private readonly MainWindow mainWindow;
    private readonly ApplicationLifetime applicationLifetime;
    private readonly IAppDataPaths paths;
    private readonly UsageMonitor usageMonitor;
    private readonly TrayIconHost trayIconHost;
    private readonly ILogger<TrayIconService> logger;
    private bool disposed;

    /// <summary>
    /// 表示対象ウィンドウ、終了制御、保存先、およびロガーを受け取って初期化します。
    /// </summary>
    /// <param name="mainWindow">表示対象の状態ウィンドウです。</param>
    /// <param name="applicationLifetime">アプリケーションの終了制御です。</param>
    /// <param name="paths">アプリケーションデータの保存先です。</param>
    /// <param name="usageMonitor">手動確認を受け付ける利用枠監視です。</param>
    /// <param name="trayIconHost">メニューと通知で共有するタスクトレイアイコンです。</param>
    /// <param name="logger">操作結果を記録するロガーです。</param>
    public TrayIconService(
        MainWindow mainWindow,
        ApplicationLifetime applicationLifetime,
        IAppDataPaths paths,
        UsageMonitor usageMonitor,
        TrayIconHost trayIconHost,
        ILogger<TrayIconService> logger)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(usageMonitor);
        ArgumentNullException.ThrowIfNull(trayIconHost);
        ArgumentNullException.ThrowIfNull(logger);

        this.mainWindow = mainWindow;
        this.applicationLifetime = applicationLifetime;
        this.paths = paths;
        this.usageMonitor = usageMonitor;
        this.trayIconHost = trayIconHost;
        this.logger = logger;
    }

    /// <summary>
    /// タスクトレイアイコンとコンテキストメニューを生成して表示します。
    /// </summary>
    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Forms.ContextMenuStrip menu = new();
        menu.Items.Add("状態を開く", image: null, OnOpenStatus);
        menu.Items.Add("今すぐ確認", image: null, OnRefreshNow);
        menu.Items.Add("ログフォルダーを開く", image: null, OnOpenLogDirectory);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("終了", image: null, OnExit);

        trayIconHost.Initialize(menu, OnOpenStatus);
        trayIconHost.NotificationClicked += OnOpenStatus;
        LogTrayStarted(logger, null);
    }

    /// <summary>
    /// ユーザー操作による利用枠の再取得を要求します。
    /// </summary>
    /// <param name="sender">イベント送信元です。</param>
    /// <param name="e">イベント引数です。</param>
    private async void OnRefreshNow(object? sender, EventArgs e)
    {
        try
        {
            await usageMonitor.RequestRefreshAsync(UsageCheckTrigger.Manual, CancellationToken.None);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
                && exception is not ObjectDisposedException)
        {
            LogManualRefreshFailed(logger, exception);
        }
    }

    /// <summary>
    /// タスクトレイアイコンとメニューを破棄します。
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        trayIconHost.NotificationClicked -= OnOpenStatus;
    }

    /// <summary>
    /// 状態ウィンドウを表示し、前面へ移動します。
    /// </summary>
    /// <param name="sender">イベント送信元です。</param>
    /// <param name="e">イベント引数です。</param>
    private void OnOpenStatus(object? sender, EventArgs e)
    {
        mainWindow.Show();
        if (mainWindow.WindowState == System.Windows.WindowState.Minimized)
        {
            mainWindow.WindowState = System.Windows.WindowState.Normal;
        }

        mainWindow.Activate();
    }

    /// <summary>
    /// Windowsエクスプローラーでログフォルダーを開きます。
    /// </summary>
    /// <param name="sender">イベント送信元です。</param>
    /// <param name="e">イベント引数です。</param>
    private void OnOpenLogDirectory(object? sender, EventArgs e)
    {
        try
        {
            Directory.CreateDirectory(paths.LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = paths.LogDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            LogOpenLogDirectoryFailed(logger, exception);
        }
    }

    /// <summary>
    /// 明示的な終了要求をアプリケーションへ通知します。
    /// </summary>
    /// <param name="sender">イベント送信元です。</param>
    /// <param name="e">イベント引数です。</param>
    private void OnExit(object? sender, EventArgs e)
    {
        applicationLifetime.RequestExit();
    }
}
