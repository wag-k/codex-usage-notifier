using System.ComponentModel;
using System.Windows;
using CodexUsageNotifier.Presentation;
using CodexUsageNotifier.Presentation.ViewModels;

namespace CodexUsageNotifier;

/// <summary>
/// Phase 4Aのアプリケーション設定を編集するWPFウィンドウです。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel viewModel;
    private readonly ApplicationLifetime applicationLifetime;
    private bool hideWithoutConfirmation;

    /// <summary>
    /// 設定編集ViewModelと終了状態を受け取って初期化します。
    /// </summary>
    /// <param name="viewModel">設定の読み込み、検証、保存を担当するViewModelです。</param>
    /// <param name="applicationLifetime">アプリケーションの終了状態です。</param>
    public SettingsWindow(SettingsViewModel viewModel, ApplicationLifetime applicationLifetime)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        InitializeComponent();
        DataContext = viewModel;
        this.viewModel = viewModel;
        this.applicationLifetime = applicationLifetime;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>
    /// 最新設定を読み込み、設定画面を表示して前面へ移動します。
    /// </summary>
    /// <param name="owner">状態画面から開く場合の所有ウィンドウです。</param>
    /// <param name="cancellationToken">読み込みのキャンセル通知です。</param>
    /// <returns>表示準備の完了を表す非同期処理です。</returns>
    public async Task ShowSettingsAsync(Window? owner, CancellationToken cancellationToken)
    {
        if (IsVisible)
        {
            Activate();
            return;
        }

        await viewModel.LoadAsync(cancellationToken);
        Owner = owner?.IsVisible == true ? owner : null;

        Show();
        Activate();
    }

    /// <summary>
    /// 有効な編集値を保存し、成功した場合は設定画面を隠します。
    /// </summary>
    /// <param name="sender">保存ボタンです。</param>
    /// <param name="e">クリックイベントです。</param>
    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (await viewModel.SaveAsync(CancellationToken.None))
        {
            HideSettings();
        }
    }

    /// <summary>
    /// 編集値を初期値へ戻します。保存するまでは永続化しません。
    /// </summary>
    /// <param name="sender">初期値へ戻すボタンです。</param>
    /// <param name="e">クリックイベントです。</param>
    private void OnRestoreDefaults(object sender, RoutedEventArgs e)
    {
        viewModel.RestoreDefaults();
    }

    /// <summary>
    /// 未保存変更を破棄して設定画面を隠します。
    /// </summary>
    /// <param name="sender">キャンセルボタンです。</param>
    /// <param name="e">クリックイベントです。</param>
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        viewModel.DiscardChanges();
        HideSettings();
    }

    /// <summary>
    /// Escapeキーでキャンセルと同じ操作を行います。
    /// </summary>
    /// <param name="sender">設定ウィンドウです。</param>
    /// <param name="e">キー入力イベントです。</param>
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            OnCancel(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    /// <summary>
    /// タイトルバーから閉じる場合に未保存変更の破棄確認を行います。
    /// </summary>
    /// <param name="sender">設定ウィンドウです。</param>
    /// <param name="e">キャンセル可能な終了イベントです。</param>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (applicationLifetime.IsExitRequested || hideWithoutConfirmation)
        {
            return;
        }

        e.Cancel = true;
        if (viewModel.HasUnsavedChanges)
        {
            MessageBoxResult result = System.Windows.MessageBox.Show(
                this,
                "保存していない変更を破棄しますか？",
                "設定",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        viewModel.DiscardChanges();
        Hide();
    }

    /// <summary>
    /// 再表示可能なようウィンドウを閉じずに隠します。
    /// </summary>
    private void HideSettings()
    {
        hideWithoutConfirmation = true;
        try
        {
            Hide();
        }
        finally
        {
            hideWithoutConfirmation = false;
        }
    }
}
