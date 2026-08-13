using System.Diagnostics;
using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Application.Monitoring;
using CodexUsageNotifier.Application.Notifications;
using CodexUsageNotifier.Domain.Models;
using Microsoft.Extensions.Logging;
using Forms = System.Windows.Forms;

namespace CodexUsageNotifier.Presentation.Tray;

/// <summary>
/// タスクトレイアイコン、監視操作、およびテスト通知メニューを管理します。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private static readonly Action<ILogger, Exception?> LogTrayStarted =
        LoggerMessage.Define(LogLevel.Information, new EventId(3000, "TrayStarted"), "タスクトレイへの常駐を開始しました。");

    private static readonly Action<ILogger, Exception?> LogOpenLogDirectoryFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(3001, "OpenLogDirectoryFailed"), "ログフォルダーを開けませんでした。");

    private static readonly Action<ILogger, Exception?> LogManualRefreshFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(3002, "ManualRefreshFailed"), "手動の利用枠確認に失敗しました。");

    private static readonly Action<ILogger, Exception?> LogTestNotificationRequestFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(3003, "TestNotificationRequestFailed"), "テスト通知の要求に失敗しました。");

    private readonly MainWindow mainWindow;
    private readonly SettingsWindow settingsWindow;
    private readonly ApplicationLifetime applicationLifetime;
    private readonly IAppDataPaths paths;
    private readonly UsageMonitor usageMonitor;
    private readonly TestNotificationService testNotificationService;
    private readonly TrayIconHost trayIconHost;
    private readonly ILogger<TrayIconService> logger;
    private bool disposed;

    /// <summary>
    /// 表示対象ウィンドウ、終了制御、保存先、およびロガーを受け取って初期化します。
    /// </summary>
    /// <param name="mainWindow">表示対象の状態ウィンドウです。</param>
    /// <param name="settingsWindow">表示対象の設定ウィンドウです。</param>
    /// <param name="applicationLifetime">アプリケーションの終了制御です。</param>
    /// <param name="paths">アプリケーションデータの保存先です。</param>
    /// <param name="usageMonitor">手動確認を受け付ける利用枠監視です。</param>
    /// <param name="testNotificationService">永続状態を変更しないテスト通知処理です。</param>
    /// <param name="trayIconHost">メニューと通知で共有するタスクトレイアイコンです。</param>
    /// <param name="logger">操作結果を記録するロガーです。</param>
    public TrayIconService(
        MainWindow mainWindow,
        SettingsWindow settingsWindow,
        ApplicationLifetime applicationLifetime,
        IAppDataPaths paths,
        UsageMonitor usageMonitor,
        TestNotificationService testNotificationService,
        TrayIconHost trayIconHost,
        ILogger<TrayIconService> logger)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);
        ArgumentNullException.ThrowIfNull(settingsWindow);
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(usageMonitor);
        ArgumentNullException.ThrowIfNull(testNotificationService);
        ArgumentNullException.ThrowIfNull(trayIconHost);
        ArgumentNullException.ThrowIfNull(logger);

        this.mainWindow = mainWindow;
        this.settingsWindow = settingsWindow;
        this.applicationLifetime = applicationLifetime;
        this.paths = paths;
        this.usageMonitor = usageMonitor;
        this.testNotificationService = testNotificationService;
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
        menu.Items.Add("設定", image: null, OnOpenSettings);
        menu.Items.Add("今すぐ確認", image: null, OnRefreshNow);
        menu.Items.Add(CreateTestNotificationMenu());
        menu.Items.Add("ログフォルダーを開く", image: null, OnOpenLogDirectory);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("終了", image: null, OnExit);

        trayIconHost.Initialize(menu, OnOpenStatus);
        trayIconHost.NotificationClicked += OnOpenStatus;
        LogTrayStarted(logger, null);
    }

    /// <summary>
    /// 通知種類を個別に選べるテスト通知サブメニューを生成します。
    /// </summary>
    /// <returns>6種類のテスト通知項目を持つメニューです。</returns>
    private Forms.ToolStripMenuItem CreateTestNotificationMenu()
    {
        Forms.ToolStripMenuItem menu = new("テスト通知");
        AddTestNotificationItem(menu, "短期枠回復通知", RateLimitNotificationType.ShortWindowRecovered);
        AddTestNotificationItem(menu, "早期警告通知", RateLimitNotificationType.LongWindowEarlyWarning);
        AddTestNotificationItem(menu, "通常警告通知", RateLimitNotificationType.LongWindowStandardWarning);
        AddTestNotificationItem(menu, "最終警告通知", RateLimitNotificationType.LongWindowFinalWarning);
        AddTestNotificationItem(menu, "リセット完了通知", RateLimitNotificationType.LongWindowResetCompleted);
        AddTestNotificationItem(menu, "監視障害通知", RateLimitNotificationType.MonitoringFailure);
        return menu;
    }

    /// <summary>
    /// 指定種類をTagへ保持するテスト通知項目を追加します。
    /// </summary>
    /// <param name="parent">項目を追加する親メニューです。</param>
    /// <param name="text">表示する項目名です。</param>
    /// <param name="notificationType">送信する通知種類です。</param>
    private void AddTestNotificationItem(
        Forms.ToolStripMenuItem parent,
        string text,
        RateLimitNotificationType notificationType)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        Forms.ToolStripMenuItem item = new(text) { Tag = notificationType };
        item.Click += OnTestNotification;
        parent.DropDownItems.Add(item);
    }

    /// <summary>
    /// 選択された通知種類のテスト通知を送信します。
    /// </summary>
    /// <param name="sender">通知種類をTagへ保持するメニュー項目です。</param>
    /// <param name="e">イベント引数です。</param>
    private async void OnTestNotification(object? sender, EventArgs e)
    {
        if (sender is not Forms.ToolStripMenuItem { Tag: RateLimitNotificationType notificationType })
        {
            return;
        }

        try
        {
            await testNotificationService.SendAsync(notificationType, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogTestNotificationRequestFailed(logger, exception);
        }
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
    /// タスクトレイから最新設定を読み込んで設定画面を開きます。
    /// </summary>
    /// <param name="sender">イベント送信元です。</param>
    /// <param name="e">イベント引数です。</param>
    private async void OnOpenSettings(object? sender, EventArgs e)
    {
        await settingsWindow.ShowSettingsAsync(owner: null, CancellationToken.None);
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
    private async void OnExit(object? sender, EventArgs e)
    {
        await applicationLifetime.RequestExitAsync();
    }
}
