using System.Diagnostics;
using CodexUsageNotifier.Application.Abstractions;
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

    private readonly MainWindow mainWindow;
    private readonly ApplicationLifetime applicationLifetime;
    private readonly IAppDataPaths paths;
    private readonly ILogger<TrayIconService> logger;
    private Forms.NotifyIcon? notifyIcon;
    private bool disposed;

    /// <summary>
    /// 表示対象ウィンドウ、終了制御、保存先、およびロガーを受け取って初期化します。
    /// </summary>
    /// <param name="mainWindow">表示対象の状態ウィンドウです。</param>
    /// <param name="applicationLifetime">アプリケーションの終了制御です。</param>
    /// <param name="paths">アプリケーションデータの保存先です。</param>
    /// <param name="logger">操作結果を記録するロガーです。</param>
    public TrayIconService(
        MainWindow mainWindow,
        ApplicationLifetime applicationLifetime,
        IAppDataPaths paths,
        ILogger<TrayIconService> logger)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        this.mainWindow = mainWindow;
        this.applicationLifetime = applicationLifetime;
        this.paths = paths;
        this.logger = logger;
    }

    /// <summary>
    /// タスクトレイアイコンとコンテキストメニューを生成して表示します。
    /// </summary>
    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (notifyIcon is not null)
        {
            return;
        }

        Forms.ContextMenuStrip menu = new();
        menu.Items.Add("状態を開く", image: null, OnOpenStatus);
        menu.Items.Add("ログフォルダーを開く", image: null, OnOpenLogDirectory);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("終了", image: null, OnExit);

        notifyIcon = new Forms.NotifyIcon
        {
            Text = "Codex Usage Notifier",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        notifyIcon.DoubleClick += OnOpenStatus;
        LogTrayStarted(logger, null);
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
        if (notifyIcon is not null)
        {
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            notifyIcon = null;
        }
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
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
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
