using System.ComponentModel;
using CodexUsageNotifier.Presentation;
using CodexUsageNotifier.Presentation.ViewModels;

namespace CodexUsageNotifier;

/// <summary>
/// Codex利用枠監視の基本状態を表示するウィンドウです。
/// </summary>
public partial class MainWindow : System.Windows.Window
{
    private readonly ApplicationLifetime applicationLifetime;
    private readonly SettingsWindow settingsWindow;
    private readonly StatusViewModel viewModel;

    /// <summary>
    /// 状態表示用のデータとアプリケーション終了状態を受け取って初期化します。
    /// </summary>
    /// <param name="viewModel">状態表示用のビューモデルです。</param>
    /// <param name="applicationLifetime">アプリケーションの終了状態です。</param>
    /// <param name="settingsWindow">表示する設定画面です。</param>
    public MainWindow(
        StatusViewModel viewModel,
        ApplicationLifetime applicationLifetime,
        SettingsWindow settingsWindow)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(settingsWindow);

        InitializeComponent();
        ConfigureInitialBounds();
        DataContext = viewModel;
        this.viewModel = viewModel;
        this.applicationLifetime = applicationLifetime;
        this.settingsWindow = settingsWindow;
        Closing += OnClosing;
        Activated += OnActivated;
    }

    /// <summary>
    /// 高DPI環境でも初期表示が作業領域からはみ出さない大きさへ調整します。
    /// </summary>
    private void ConfigureInitialBounds()
    {
        const double workAreaMargin = 32D;
        System.Windows.Rect workArea = System.Windows.SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(Width, workArea.Width - workAreaMargin));
        Height = Math.Max(MinHeight, Math.Min(Height, workArea.Height - workAreaMargin));
    }

    /// <summary>
    /// 通常のウィンドウクローズでは終了せず、タスクトレイへ格納します。
    /// </summary>
    /// <param name="sender">イベント送信元です。</param>
    /// <param name="e">キャンセル可能な終了イベントです。</param>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (applicationLifetime.IsExitRequested)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    /// <summary>
    /// 状態画面から最新設定を読み込んで設定画面を開きます。
    /// </summary>
    /// <param name="sender">設定ボタンです。</param>
    /// <param name="e">クリックイベントです。</param>
    private async void OnOpenSettings(object sender, System.Windows.RoutedEventArgs e)
    {
        await settingsWindow.ShowSettingsAsync(this, CancellationToken.None);
    }

    /// <summary>状態画面が表示されるたびにGmail認証状態を非同期で更新します。</summary>
    /// <param name="sender">状態画面です。</param>
    /// <param name="e">アクティブ化イベントです。</param>
    private async void OnActivated(object? sender, EventArgs e)
    {
        await viewModel.RefreshGmailAuthenticationStatusAsync(CancellationToken.None);
    }
}
