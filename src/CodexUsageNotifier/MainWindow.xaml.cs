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

    /// <summary>
    /// 状態表示用のデータとアプリケーション終了状態を受け取って初期化します。
    /// </summary>
    /// <param name="viewModel">状態表示用のビューモデルです。</param>
    /// <param name="applicationLifetime">アプリケーションの終了状態です。</param>
    public MainWindow(StatusViewModel viewModel, ApplicationLifetime applicationLifetime)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(applicationLifetime);

        InitializeComponent();
        DataContext = viewModel;
        this.applicationLifetime = applicationLifetime;
        Closing += OnClosing;
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
}
