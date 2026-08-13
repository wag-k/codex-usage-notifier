using CodexUsageNotifier.Application.Gmail;
using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Presentation;
using System.Globalization;

namespace CodexUsageNotifier.Presentation.ViewModels;

/// <summary>
/// 設定画面のGmail認証、解除、テスト送信に関する表示と操作を管理します。
/// </summary>
public sealed partial class SettingsViewModel
{
    private string gmailAuthenticationStatus = "未認証";
    private string oauthClientConfigurationStatus = "未確認";
    private string oauthClientConfigurationPath = string.Empty;
    private string authenticatedGmailAddress = "未認証";
    private string lastGmailAuthenticationAt = "なし";
    private string lastTestMailResult = "未実行";
    private string gmailReauthenticationStatus = "不要";
    private string gmailAvailabilityDescription =
        "OAuthクライアントを登録するとGoogle認証を開始できます。Windows通知は引き続き利用できます。";
    private bool isGmailAuthenticationAvailable;
    private bool isGmailReauthenticationAvailable;
    private bool isGmailDisconnectAvailable;
    private bool isTestEmailAvailable;
    private GmailAuthenticationStatus gmailStatus = new() { State = GmailAuthenticationState.Unauthenticated };

    /// <summary>Gmail通知が任意であることを示す説明を取得します。</summary>
    public string GmailOptionalDescription => GmailOnboardingContent.OptionalDescription;

    /// <summary>利用者自身のOAuthクライアントが必要であることを示す説明を取得します。</summary>
    public string OAuthClientRequirementDescription => GmailOnboardingContent.OAuthClientRequirementDescription;

    /// <summary>Gmail通知を設定する概要手順を取得します。</summary>
    public string GmailSetupSteps => GmailOnboardingContent.SetupSteps;

    /// <summary>Google認証とGmail権限に関するプライバシー説明を取得します。</summary>
    public string GmailPrivacyDescription => GmailOnboardingContent.PrivacyDescription;

    /// <summary>現在の状態で利用できないGmail操作と、その理由を取得します。</summary>
    public string GmailAvailabilityDescription
    {
        get => gmailAvailabilityDescription;
        private set => SetProperty(ref gmailAvailabilityDescription, value);
    }

    /// <summary>OAuthクライアント設定の状態を取得します。</summary>
    public string OAuthClientConfigurationStatus
    {
        get => oauthClientConfigurationStatus;
        private set => SetProperty(ref oauthClientConfigurationStatus, value);
    }

    /// <summary>OAuthクライアント設定の標準配置先を取得します。</summary>
    public string OAuthClientConfigurationPath
    {
        get => oauthClientConfigurationPath;
        private set => SetProperty(ref oauthClientConfigurationPath, value);
    }

    /// <summary>認証済みGoogleアカウントを取得します。</summary>
    public string AuthenticatedGmailAddress
    {
        get => authenticatedGmailAddress;
        private set => SetProperty(ref authenticatedGmailAddress, value);
    }

    /// <summary>最後に認証へ成功したローカル時刻を取得します。</summary>
    public string LastGmailAuthenticationAt
    {
        get => lastGmailAuthenticationAt;
        private set => SetProperty(ref lastGmailAuthenticationAt, value);
    }

    /// <summary>最後のテストメール送信結果を取得します。</summary>
    public string LastTestMailResult
    {
        get => lastTestMailResult;
        private set => SetProperty(ref lastTestMailResult, value);
    }

    /// <summary>再認証が必要かどうかの表示を取得します。</summary>
    public string GmailReauthenticationStatus
    {
        get => gmailReauthenticationStatus;
        private set => SetProperty(ref gmailReauthenticationStatus, value);
    }

    /// <summary>再認証操作が利用可能かを取得します。</summary>
    public bool IsGmailReauthenticationAvailable
    {
        get => isGmailReauthenticationAvailable;
        private set => SetProperty(ref isGmailReauthenticationAvailable, value);
    }

    /// <summary>認証解除操作が利用可能かを取得します。</summary>
    public bool IsGmailDisconnectAvailable
    {
        get => isGmailDisconnectAvailable;
        private set => SetProperty(ref isGmailDisconnectAvailable, value);
    }

    /// <summary>Gmail通知を有効として保存できるかを取得します。</summary>
    public bool CanEnableGmailNotification => (gmailStatus.State is GmailAuthenticationState.Authenticated
            or GmailAuthenticationState.RefreshRequired)
        && !string.IsNullOrWhiteSpace(GmailRecipient)
        && AppSettings.IsValidOptionalEmailAddress(GmailRecipient.Trim());

    /// <summary>選択されたOAuthクライアントJSONを検証して標準配置先へ保存します。</summary>
    public async Task ImportGoogleOAuthClientAsync(string sourcePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            GmailOperationResult result = await Task.Run(
                () => googleOAuthConfigurationService.ImportAsync(sourcePath, cancellationToken),
                cancellationToken);
            OperationMessage = result.Message;
            await RefreshGmailStatusAsync(cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Googleアカウントのブラウザー認証を実行します。</summary>
    public async Task AuthenticateGmailAsync(bool forceReauthentication, CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            bool wasReauthenticationRequired = gmailStatus.State == GmailAuthenticationState.ReauthenticationRequired;
            GmailOperationResult result = await Task.Run(
                () => gmailAuthenticationService.AuthenticateAsync(forceReauthentication, cancellationToken),
                cancellationToken);
            OperationMessage = result.Message;
            await RefreshGmailStatusAsync(cancellationToken);
            if (result.Succeeded && wasReauthenticationRequired && baselineSettings.GmailNotificationEnabled)
            {
                await UpdateGmailDeliveryEnabledBoundaryAsync(cancellationToken);
            }

            if (result.Succeeded && string.IsNullOrWhiteSpace(GmailRecipient)
                && !string.IsNullOrWhiteSpace(gmailStatus.AuthenticatedEmailAddress))
            {
                GmailRecipient = gmailStatus.AuthenticatedEmailAddress;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Google側の失効を試み、ローカル認証情報を削除します。</summary>
    public async Task DisconnectGmailAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            GmailOperationResult result = await Task.Run(
                () => gmailAuthenticationService.DisconnectAsync(cancellationToken),
                cancellationToken);
            OperationMessage = result.Message;
            if (result.LocalCredentialsRemoved)
            {
                GmailNotificationEnabled = false;
                await PersistDisabledGmailSettingAsync(cancellationToken);
            }

            await RefreshGmailStatusAsync(cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>現在の送信先へ本番通知状態を変更しないテストメールを送信します。</summary>
    public async Task SendGmailTestMailAsync(CancellationToken cancellationToken)
    {
        if (IsBusy || !IsTestEmailAvailable)
        {
            return;
        }

        IsBusy = true;
        try
        {
            GmailOperationResult result = await Task.Run(
                () => gmailTestMailSender.SendAsync(GmailRecipient, cancellationToken),
                cancellationToken);
            LastTestMailResult = $"{DateTimeOffset.Now:yyyy/MM/dd HH:mm} {result.Message}";
            OperationMessage = result.Message;
            await RefreshGmailStatusAsync(cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>OAuth設定と認証状態を再読み込みして画面表示を更新します。</summary>
    private async Task RefreshGmailStatusAsync(CancellationToken cancellationToken)
    {
        (GoogleOAuthClientConfigurationStatus configuration, GmailAuthenticationStatus authentication) =
            await Task.Run(
                async () =>
                {
                    GoogleOAuthClientConfigurationStatus configurationStatus =
                        await googleOAuthConfigurationService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                    GmailAuthenticationStatus authenticationStatus =
                        await gmailAuthenticationService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                    return (configurationStatus, authenticationStatus);
                },
                cancellationToken);
        gmailStatus = authentication;
        OAuthClientConfigurationStatus = !configuration.Exists
            ? "未設定"
            : configuration.IsValid
                ? "設定済み"
                : $"設定エラー：{configuration.Message}";
        OAuthClientConfigurationPath = configuration.StandardPath;
        GmailAuthenticationStatus = FormatAuthenticationState(gmailStatus.State);
        AuthenticatedGmailAddress = gmailStatus.AuthenticatedEmailAddress ?? "未認証";
        LastGmailAuthenticationAt = gmailStatus.LastAuthenticatedAtUtc is DateTimeOffset authenticatedAt
            ? authenticatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)
            : "なし";
        GmailReauthenticationStatus = gmailStatus.RequiresReauthentication
            ? $"必要：{gmailStatus.LastErrorSummary}"
            : "不要";
        GmailAvailabilityDescription = FormatGmailAvailability(gmailStatus.State);
        UpdateGmailActionAvailability();
        OnPropertyChanged(nameof(CanEnableGmailNotification));
    }

    /// <summary>認証状態と入力値から各Gmail操作の有効状態を更新します。</summary>
    private void UpdateGmailActionAvailability()
    {
        bool available = !IsBusy;
        bool configurationReady = gmailStatus.HasClientConfiguration
            && gmailStatus.State != GmailAuthenticationState.NotConfigured;
        SetProperty(ref isGmailAuthenticationAvailable,
            available && configurationReady && gmailStatus.State == GmailAuthenticationState.Unauthenticated,
            nameof(IsGmailAuthenticationAvailable));
        IsGmailReauthenticationAvailable = available && configurationReady
            && (gmailStatus.State is GmailAuthenticationState.Authenticated
                or GmailAuthenticationState.RefreshRequired
                or GmailAuthenticationState.ReauthenticationRequired
                or GmailAuthenticationState.Error);
        IsGmailDisconnectAvailable = available
            && (gmailStatus.State is GmailAuthenticationState.Authenticated
                or GmailAuthenticationState.RefreshRequired
                or GmailAuthenticationState.ReauthenticationRequired
                or GmailAuthenticationState.Error);
        SetProperty(ref isTestEmailAvailable,
            available && gmailStatus.CanSendTestMail
                && !string.IsNullOrWhiteSpace(GmailRecipient)
                && AppSettings.IsValidOptionalEmailAddress(GmailRecipient.Trim()),
            nameof(IsTestEmailAvailable));
        OnPropertyChanged(nameof(CanEnableGmailNotification));
    }

    /// <summary>認証解除後にGmail有効設定だけを即時かつ原子的に無効化します。</summary>
    private async Task PersistDisabledGmailSettingAsync(CancellationToken cancellationToken)
    {
        await Task.Run(
            async () =>
            {
                AppSettings persisted = await settingsRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
                if (persisted.GmailNotificationEnabled)
                {
                    AppSettings disabled = persisted with { GmailNotificationEnabled = false };
                    await settingsRepository.SaveAsync(disabled, cancellationToken).ConfigureAwait(false);
                    await settingsChangeSink.ApplyAsync(disabled, cancellationToken).ConfigureAwait(false);
                }
            },
            cancellationToken);

        baselineSettings = baselineSettings with { GmailNotificationEnabled = false };
        baselineSignature = CaptureSettingsSignature(baselineSettings);
        ValidateAndTrackChanges();
    }

    /// <summary>認証状態を日本語表示へ変換します。</summary>
    private static string FormatAuthenticationState(GmailAuthenticationState state)
    {
        return state switch
        {
            GmailAuthenticationState.NotConfigured => "利用不可（OAuthクライアント未設定）",
            GmailAuthenticationState.Unauthenticated => "未認証",
            GmailAuthenticationState.Authenticating => "認証中",
            GmailAuthenticationState.Authenticated => "認証済み",
            GmailAuthenticationState.RefreshRequired => "トークン更新待ち",
            GmailAuthenticationState.ReauthenticationRequired => "再認証が必要",
            GmailAuthenticationState.Error => "エラー",
            _ => "不明",
        };
    }

    /// <summary>現在の認証状態に対応する操作案内を日本語へ変換します。</summary>
    /// <param name="state">トークンを含まない認証状態です。</param>
    /// <returns>利用可能な次の操作、または操作できない理由です。</returns>
    private static string FormatGmailAvailability(GmailAuthenticationState state)
    {
        return state switch
        {
            GmailAuthenticationState.NotConfigured =>
                "OAuthクライアントを登録するとGoogle認証を開始できます。Google認証とGmail通知は現在利用できませんが、Windows通知は利用できます。",
            GmailAuthenticationState.Unauthenticated =>
                "Googleアカウントで認証すると、テストメールとGmail通知を利用できます。",
            GmailAuthenticationState.Authenticating => "Google認証の完了を待っています。",
            GmailAuthenticationState.Authenticated or GmailAuthenticationState.RefreshRequired =>
                "Google認証済みです。有効な送信先を入力するとテストメールとGmail通知を利用できます。",
            GmailAuthenticationState.ReauthenticationRequired =>
                "Googleアカウントの再認証が必要です。再認証まではGmail通知を利用できません。Windows通知は継続します。",
            GmailAuthenticationState.Error =>
                "Google認証状態を確認できません。設定内容を確認してください。Windows通知は継続します。",
            _ => "Google認証状態を確認できません。Windows通知は継続します。",
        };
    }

    /// <summary>指定設定モデルから未保存変更比較用の署名を生成します。</summary>
    private static string CaptureSettingsSignature(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return string.Join(
            '\u001f',
            settings.WindowsNotificationEnabled,
            settings.QuietHoursEnabled,
            settings.QuietHoursStart.ToString("HH:mm", CultureInfo.InvariantCulture),
            settings.QuietHoursEnd.ToString("HH:mm", CultureInfo.InvariantCulture),
            settings.FallbackPollingMinutes,
            settings.AutoStartEnabled,
            settings.ShortWindowRecoveryEnabled,
            settings.ShortWindowRecoveryThresholdPercent,
            settings.LongWindowEarlyWarningEnabled,
            settings.LongWindowEarlyWarningHours,
            settings.LongWindowEarlyWarningThresholdPercent,
            settings.LongWindowStandardWarningEnabled,
            settings.LongWindowStandardWarningHours,
            settings.LongWindowStandardWarningThresholdPercent,
            settings.LongWindowFinalWarningEnabled,
            settings.LongWindowFinalWarningHours,
            settings.LongWindowFinalWarningThresholdPercent,
            settings.LongWindowResetCompletedEnabled,
            settings.GmailNotificationEnabled,
            settings.GmailRecipient ?? string.Empty,
            settings.ResetInferenceUsageDropPoints);
    }

    /// <summary>指定プロパティが更新されたことを通知します。</summary>
    private void OnPropertyChanged(string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }
}
