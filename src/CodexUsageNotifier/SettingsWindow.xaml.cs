using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using CodexUsageNotifier.Presentation;
using CodexUsageNotifier.Presentation.ViewModels;
using Microsoft.Win32;

namespace CodexUsageNotifier;

/// <summary>
/// アプリケーション設定を編集するWPFウィンドウです。
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
    /// デスクトップアプリ用OAuthクライアントJSONを選択して標準配置先へ登録します。
    /// </summary>
    /// <param name="sender">設定ファイル選択ボタンです。</param>
    /// <param name="e">クリックイベントです。</param>
    private async void OnSelectGoogleOAuthClient(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            CheckFileExists = true,
            DefaultExt = ".json",
            Filter = "JSONファイル (*.json)|*.json|すべてのファイル (*.*)|*.*",
            Title = "Google OAuthクライアント設定を選択",
        };
        if (dialog.ShowDialog(this) == true)
        {
            await viewModel.ImportGoogleOAuthClientAsync(dialog.FileName, CancellationToken.None);
        }
    }

    /// <summary>固定された信頼済みURLを既定ブラウザーで開き、Gmail OAuth設定手順を表示します。</summary>
    /// <param name="sender">設定手順ボタンです。</param>
    /// <param name="e">クリックイベントです。</param>
    private void OnOpenGmailOAuthSetup(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = PublicDocumentationLinks.GmailOAuthSetupUri.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            System.Windows.MessageBox.Show(
                this,
                "Gmail通知の設定手順をブラウザーで開けませんでした。READMEから設定手順を確認してください。",
                "設定手順を開けません",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    /// <summary>Googleアカウントの初回認証を開始します。</summary>
    private async void OnAuthenticateGmail(object sender, RoutedEventArgs e)
    {
        await viewModel.AuthenticateGmailAsync(forceReauthentication: false, CancellationToken.None);
    }

    /// <summary>保存済み認証情報を破棄してGoogleアカウントを再認証します。</summary>
    private async void OnReauthenticateGmail(object sender, RoutedEventArgs e)
    {
        await viewModel.AuthenticateGmailAsync(forceReauthentication: true, CancellationToken.None);
    }

    /// <summary>確認後にGoogle側の失効とローカル認証情報の削除を実行します。</summary>
    private async void OnDisconnectGmail(object sender, RoutedEventArgs e)
    {
        MessageBoxResult result = System.Windows.MessageBox.Show(
            this,
            "Googleアカウントの認証を解除しますか？ローカルの認証情報は削除されます。",
            "Gmail認証解除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            await viewModel.DisconnectGmailAsync(CancellationToken.None);
        }
    }

    /// <summary>入力済み送信先へGmail APIのテストメールを送信します。</summary>
    private async void OnSendGmailTestMail(object sender, RoutedEventArgs e)
    {
        await viewModel.SendGmailTestMailAsync(CancellationToken.None);
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
