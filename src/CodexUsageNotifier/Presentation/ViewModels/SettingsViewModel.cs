using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Application.State;
using CodexUsageNotifier.Application.Gmail;
using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Domain.Services;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier.Presentation.ViewModels;

/// <summary>
/// Phase 4A設定画面の編集値、入力検証、保存、および変更破棄を管理します。
/// </summary>
public sealed partial class SettingsViewModel : INotifyPropertyChanged
{
    private static readonly Action<ILogger, Exception?> LogSettingsSaved =
        LoggerMessage.Define(LogLevel.Information, new EventId(2500, "SettingsSaved"), "設定画面から設定を保存しました。");
    private static readonly Action<ILogger, Exception?> LogSettingsSaveFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(2501, "SettingsSaveFailed"), "設定画面から設定を保存できませんでした。");
    private static readonly Action<ILogger, Exception?> LogSettingsLoadFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(2502, "SettingsLoadFailed"), "設定画面へ設定を読み込めませんでした。");
    private static readonly Action<ILogger, Exception?> LogSettingsApplyFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(2503, "SettingsApplyFailed"), "保存済み設定を監視処理へ反映できませんでした。");

    private readonly ISettingsRepository settingsRepository;
    private readonly ApplicationStateStore stateStore;
    private readonly ISettingsChangeSink settingsChangeSink;
    private readonly IGoogleOAuthClientConfigurationService googleOAuthConfigurationService;
    private readonly IGmailAuthenticationService gmailAuthenticationService;
    private readonly IGmailTestMailSender gmailTestMailSender;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SettingsViewModel> logger;
    private AppSettings baselineSettings = AppSettings.CreateDefault();
    private UsageSnapshot? observedSnapshot;
    private string baselineSignature = string.Empty;
    private bool isApplyingValues;
    private bool windowsNotificationEnabled;
    private bool quietHoursEnabled;
    private string quietHoursStart = "00:00";
    private string quietHoursEnd = "07:00";
    private string fallbackPollingMinutes = "60";
    private bool autoStartEnabled;
    private bool shortWindowRecoveryEnabled;
    private string shortWindowRecoveryThresholdPercent = "99";
    private bool longWindowEarlyWarningEnabled;
    private string longWindowEarlyWarningHours = "48";
    private string longWindowEarlyWarningThresholdPercent = "50";
    private bool longWindowStandardWarningEnabled;
    private string longWindowStandardWarningHours = "24";
    private string longWindowStandardWarningThresholdPercent = "20";
    private bool longWindowFinalWarningEnabled;
    private string longWindowFinalWarningHours = "6";
    private string longWindowFinalWarningThresholdPercent = "10";
    private bool longWindowResetCompletedEnabled;
    private bool gmailNotificationEnabled;
    private string gmailRecipient = string.Empty;
    private int resetInferenceUsageDropPoints = 50;
    private bool hasUnsavedChanges;
    private bool canSave;
    private bool isBusy;
    private string quietHoursError = string.Empty;
    private string fallbackPollingError = string.Empty;
    private string shortWindowThresholdError = string.Empty;
    private string earlyHoursError = string.Empty;
    private string earlyThresholdError = string.Empty;
    private string standardHoursError = string.Empty;
    private string standardThresholdError = string.Empty;
    private string finalHoursError = string.Empty;
    private string finalThresholdError = string.Empty;
    private string gmailNotificationError = string.Empty;
    private string gmailRecipientError = string.Empty;
    private string operationMessage = string.Empty;

    /// <summary>
    /// 設定値または検証状態が変更されたときに発生します。
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 設定永続化、状態読み込み、監視反映、およびログ出力先を受け取ります。
    /// </summary>
    /// <param name="settingsRepository">設定の読み書き先です。</param>
    /// <param name="stateStore">観測済み利用枠の読み込み元です。</param>
    /// <param name="settingsChangeSink">保存後の設定反映先です。</param>
    /// <param name="logger">読み書き結果の記録先です。</param>
    public SettingsViewModel(
        ISettingsRepository settingsRepository,
        ApplicationStateStore stateStore,
        ISettingsChangeSink settingsChangeSink,
        IGoogleOAuthClientConfigurationService googleOAuthConfigurationService,
        IGmailAuthenticationService gmailAuthenticationService,
        IGmailTestMailSender gmailTestMailSender,
        TimeProvider timeProvider,
        ILogger<SettingsViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(settingsRepository);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(settingsChangeSink);
        ArgumentNullException.ThrowIfNull(googleOAuthConfigurationService);
        ArgumentNullException.ThrowIfNull(gmailAuthenticationService);
        ArgumentNullException.ThrowIfNull(gmailTestMailSender);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        this.settingsRepository = settingsRepository;
        this.stateStore = stateStore;
        this.settingsChangeSink = settingsChangeSink;
        this.googleOAuthConfigurationService = googleOAuthConfigurationService;
        this.gmailAuthenticationService = gmailAuthenticationService;
        this.gmailTestMailSender = gmailTestMailSender;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>
    /// 設定画面に表示する観測済み利用枠を取得します。
    /// </summary>
    public ObservableCollection<RateLimitSettingItemViewModel> RateLimits { get; } = [];

    /// <summary>
    /// Windows通知が有効かどうかを取得または設定します。
    /// </summary>
    public bool WindowsNotificationEnabled
    {
        get => windowsNotificationEnabled;
        set => SetEditableProperty(ref windowsNotificationEnabled, value);
    }

    /// <summary>
    /// 通知禁止時間が有効かどうかを取得または設定します。
    /// </summary>
    public bool QuietHoursEnabled
    {
        get => quietHoursEnabled;
        set => SetEditableProperty(ref quietHoursEnabled, value);
    }

    /// <summary>
    /// 通知禁止時間の開始時刻をHH:mm形式で取得または設定します。
    /// </summary>
    public string QuietHoursStart
    {
        get => quietHoursStart;
        set => SetEditableProperty(ref quietHoursStart, value);
    }

    /// <summary>
    /// 通知禁止時間の終了時刻をHH:mm形式で取得または設定します。
    /// </summary>
    public string QuietHoursEnd
    {
        get => quietHoursEnd;
        set => SetEditableProperty(ref quietHoursEnd, value);
    }

    /// <summary>
    /// 補助確認間隔を分単位の入力文字列として取得または設定します。
    /// </summary>
    public string FallbackPollingMinutes
    {
        get => fallbackPollingMinutes;
        set => SetEditableProperty(ref fallbackPollingMinutes, value);
    }

    /// <summary>
    /// Windowsログイン時の自動起動設定値を取得または設定します。
    /// </summary>
    public bool AutoStartEnabled
    {
        get => autoStartEnabled;
        set => SetEditableProperty(ref autoStartEnabled, value);
    }

    /// <summary>
    /// FiveHour枠の短期回復通知が有効かどうかを取得または設定します。
    /// </summary>
    public bool ShortWindowRecoveryEnabled
    {
        get => shortWindowRecoveryEnabled;
        set => SetEditableProperty(ref shortWindowRecoveryEnabled, value);
    }

    /// <summary>
    /// 短期回復通知の残量閾値を入力文字列として取得または設定します。
    /// </summary>
    public string ShortWindowRecoveryThresholdPercent
    {
        get => shortWindowRecoveryThresholdPercent;
        set => SetEditableProperty(ref shortWindowRecoveryThresholdPercent, value);
    }

    /// <summary>
    /// Weekly枠のEarly通知が有効かどうかを取得または設定します。
    /// </summary>
    public bool LongWindowEarlyWarningEnabled
    {
        get => longWindowEarlyWarningEnabled;
        set => SetEditableProperty(ref longWindowEarlyWarningEnabled, value);
    }

    /// <summary>
    /// Early通知の残り時間を入力文字列として取得または設定します。
    /// </summary>
    public string LongWindowEarlyWarningHours
    {
        get => longWindowEarlyWarningHours;
        set => SetEditableProperty(ref longWindowEarlyWarningHours, value);
    }

    /// <summary>
    /// Early通知の残量閾値を入力文字列として取得または設定します。
    /// </summary>
    public string LongWindowEarlyWarningThresholdPercent
    {
        get => longWindowEarlyWarningThresholdPercent;
        set => SetEditableProperty(ref longWindowEarlyWarningThresholdPercent, value);
    }

    /// <summary>
    /// Weekly枠のStandard通知が有効かどうかを取得または設定します。
    /// </summary>
    public bool LongWindowStandardWarningEnabled
    {
        get => longWindowStandardWarningEnabled;
        set => SetEditableProperty(ref longWindowStandardWarningEnabled, value);
    }

    /// <summary>
    /// Standard通知の残り時間を入力文字列として取得または設定します。
    /// </summary>
    public string LongWindowStandardWarningHours
    {
        get => longWindowStandardWarningHours;
        set => SetEditableProperty(ref longWindowStandardWarningHours, value);
    }

    /// <summary>
    /// Standard通知の残量閾値を入力文字列として取得または設定します。
    /// </summary>
    public string LongWindowStandardWarningThresholdPercent
    {
        get => longWindowStandardWarningThresholdPercent;
        set => SetEditableProperty(ref longWindowStandardWarningThresholdPercent, value);
    }

    /// <summary>
    /// Weekly枠のFinal通知が有効かどうかを取得または設定します。
    /// </summary>
    public bool LongWindowFinalWarningEnabled
    {
        get => longWindowFinalWarningEnabled;
        set => SetEditableProperty(ref longWindowFinalWarningEnabled, value);
    }

    /// <summary>
    /// Final通知の残り時間を入力文字列として取得または設定します。
    /// </summary>
    public string LongWindowFinalWarningHours
    {
        get => longWindowFinalWarningHours;
        set => SetEditableProperty(ref longWindowFinalWarningHours, value);
    }

    /// <summary>
    /// Final通知の残量閾値を入力文字列として取得または設定します。
    /// </summary>
    public string LongWindowFinalWarningThresholdPercent
    {
        get => longWindowFinalWarningThresholdPercent;
        set => SetEditableProperty(ref longWindowFinalWarningThresholdPercent, value);
    }

    /// <summary>
    /// Weekly枠のリセット完了通知が有効かどうかを取得または設定します。
    /// </summary>
    public bool LongWindowResetCompletedEnabled
    {
        get => longWindowResetCompletedEnabled;
        set => SetEditableProperty(ref longWindowResetCompletedEnabled, value);
    }

    /// <summary>
    /// Gmail通知の設定値を取得または設定します。認証済みかつ送信先が有効な場合だけ保存できます。
    /// </summary>
    public bool GmailNotificationEnabled
    {
        get => gmailNotificationEnabled;
        set => SetEditableProperty(ref gmailNotificationEnabled, value);
    }

    /// <summary>
    /// Gmail通知の送信先メールアドレスを取得または設定します。
    /// </summary>
    public string GmailRecipient
    {
        get => gmailRecipient;
        set => SetEditableProperty(ref gmailRecipient, value);
    }

    /// <summary>
    /// Gmail認証状態の表示を取得します。
    /// </summary>
    public string GmailAuthenticationStatus
    {
        get => gmailAuthenticationStatus;
        private set => SetProperty(ref gmailAuthenticationStatus, value);
    }

    /// <summary>
    /// Google認証操作が利用可能かどうかを取得します。
    /// </summary>
    public bool IsGmailAuthenticationAvailable => isGmailAuthenticationAvailable;

    /// <summary>
    /// テストメール操作が利用可能かどうかを取得します。
    /// </summary>
    public bool IsTestEmailAvailable => isTestEmailAvailable;

    /// <summary>
    /// 未保存変更があるかどうかを取得します。
    /// </summary>
    public bool HasUnsavedChanges
    {
        get => hasUnsavedChanges;
        private set => SetProperty(ref hasUnsavedChanges, value);
    }

    /// <summary>
    /// 現在の入力を保存できるかどうかを取得します。
    /// </summary>
    public bool CanSave
    {
        get => canSave;
        private set => SetProperty(ref canSave, value);
    }

    /// <summary>
    /// 読み込みまたは保存処理中かどうかを取得します。
    /// </summary>
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                UpdateCanSave();
                UpdateGmailActionAvailability();
            }
        }
    }

    /// <summary>
    /// 通知禁止時間の入力エラーを取得します。
    /// </summary>
    public string QuietHoursError { get => quietHoursError; private set => SetProperty(ref quietHoursError, value); }

    /// <summary>
    /// 補助確認間隔の入力エラーを取得します。
    /// </summary>
    public string FallbackPollingError { get => fallbackPollingError; private set => SetProperty(ref fallbackPollingError, value); }

    /// <summary>
    /// 短期回復閾値の入力エラーを取得します。
    /// </summary>
    public string ShortWindowThresholdError { get => shortWindowThresholdError; private set => SetProperty(ref shortWindowThresholdError, value); }

    /// <summary>
    /// Early残り時間の入力エラーを取得します。
    /// </summary>
    public string EarlyHoursError { get => earlyHoursError; private set => SetProperty(ref earlyHoursError, value); }

    /// <summary>
    /// Early残量閾値の入力エラーを取得します。
    /// </summary>
    public string EarlyThresholdError { get => earlyThresholdError; private set => SetProperty(ref earlyThresholdError, value); }

    /// <summary>
    /// Standard残り時間の入力エラーを取得します。
    /// </summary>
    public string StandardHoursError { get => standardHoursError; private set => SetProperty(ref standardHoursError, value); }

    /// <summary>
    /// Standard残量閾値の入力エラーを取得します。
    /// </summary>
    public string StandardThresholdError { get => standardThresholdError; private set => SetProperty(ref standardThresholdError, value); }

    /// <summary>
    /// Final残り時間の入力エラーを取得します。
    /// </summary>
    public string FinalHoursError { get => finalHoursError; private set => SetProperty(ref finalHoursError, value); }

    /// <summary>
    /// Final残量閾値の入力エラーを取得します。
    /// </summary>
    public string FinalThresholdError { get => finalThresholdError; private set => SetProperty(ref finalThresholdError, value); }

    /// <summary>
    /// Gmail有効化の入力エラーを取得します。
    /// </summary>
    public string GmailNotificationError { get => gmailNotificationError; private set => SetProperty(ref gmailNotificationError, value); }

    /// <summary>
    /// Gmail送信先の入力エラーを取得します。
    /// </summary>
    public string GmailRecipientError { get => gmailRecipientError; private set => SetProperty(ref gmailRecipientError, value); }

    /// <summary>
    /// 読み込み、保存、または反映結果のメッセージを取得します。
    /// </summary>
    public string OperationMessage
    {
        get => operationMessage;
        private set => SetProperty(ref operationMessage, value);
    }

    /// <summary>
    /// 設定と観測済み利用枠をバックグラウンドで読み込み、編集開始状態にします。
    /// </summary>
    /// <param name="cancellationToken">読み込みのキャンセル通知です。</param>
    /// <returns>読み込み完了を表す非同期処理です。</returns>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        OperationMessage = string.Empty;
        try
        {
            AppSettings settings = await Task.Run(
                () => settingsRepository.LoadAsync(cancellationToken),
                cancellationToken);
            ApplicationState state = await Task.Run(
                () => stateStore.LoadAsync(cancellationToken),
                cancellationToken);
            baselineSettings = settings;
            observedSnapshot = state.LastUsageSnapshot;
            ApplySettings(settings);
            await RefreshGmailStatusAsync(cancellationToken);
            baselineSignature = CaptureEditSignature();
            ValidateAndTrackChanges();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            OperationMessage = "設定を読み込めませんでした。ログを確認してください。";
            LogSettingsLoadFailed(logger, exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 有効な編集値を原子的に保存し、監視処理へ再起動なしで反映します。
    /// </summary>
    /// <param name="cancellationToken">保存のキャンセル通知です。</param>
    /// <returns>保存と監視反映が完了した場合はtrueです。</returns>
    public async Task<bool> SaveAsync(CancellationToken cancellationToken)
    {
        ValidateAndTrackChanges();
        if (!CanSave || !TryCreateSettings(out AppSettings settings))
        {
            return false;
        }

        IsBusy = true;
        OperationMessage = string.Empty;
        AppSettings previousSettings = baselineSettings;
        try
        {
            await Task.Run(
                () => settingsRepository.SaveAsync(settings, cancellationToken),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            OperationMessage = "設定を保存できませんでした。元の設定を維持しています。";
            LogSettingsSaveFailed(logger, exception);
            IsBusy = false;
            return false;
        }

        baselineSettings = settings;
        baselineSignature = CaptureEditSignature();
        HasUnsavedChanges = false;
        LogSettingsSaved(logger, null);
        try
        {
            if (!previousSettings.GmailNotificationEnabled && settings.GmailNotificationEnabled)
            {
                await UpdateGmailDeliveryEnabledBoundaryAsync(cancellationToken);
            }

            await settingsChangeSink.ApplyAsync(settings, cancellationToken);
            OperationMessage = "設定を保存しました。次の正常取得から通知判定へ適用します。";
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            OperationMessage = "設定は保存しましたが、監視への反映に失敗しました。再起動すると反映されます。";
            LogSettingsApplyFailed(logger, exception);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Gmailを再有効化した時刻を、過去通知の後送防止境界として永続化します。
    /// </summary>
    /// <param name="cancellationToken">状態保存のキャンセル通知です。</param>
    /// <returns>境界保存の完了を表す非同期処理です。</returns>
    private Task<ApplicationState> UpdateGmailDeliveryEnabledBoundaryAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset enabledSinceUtc = timeProvider.GetUtcNow();
        return stateStore.UpdateAsync(
            state => state with
            {
                GmailDeliveryEnabledSinceUtc = enabledSinceUtc,
                GmailDeliveryEnabledLastObserved = true,
                GmailAuthenticationWasUsable = true,
            },
            cancellationToken);
    }

    /// <summary>
    /// 未保存の編集値を破棄し、最後に読み込んだ設定へ戻します。
    /// </summary>
    public void DiscardChanges()
    {
        ApplySettings(baselineSettings);
        baselineSignature = CaptureEditSignature();
        ValidateAndTrackChanges();
        OperationMessage = string.Empty;
    }

    /// <summary>
    /// 画面で編集できる項目と非表示の推定閾値を初期値へ戻します。
    /// </summary>
    public void RestoreDefaults()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        AppSettings restored = baselineSettings with
        {
            WindowsNotificationEnabled = defaults.WindowsNotificationEnabled,
            QuietHoursEnabled = defaults.QuietHoursEnabled,
            QuietHoursStart = defaults.QuietHoursStart,
            QuietHoursEnd = defaults.QuietHoursEnd,
            FallbackPollingMinutes = defaults.FallbackPollingMinutes,
            AutoStartEnabled = defaults.AutoStartEnabled,
            ShortWindowRecoveryEnabled = defaults.ShortWindowRecoveryEnabled,
            ShortWindowRecoveryThresholdPercent = defaults.ShortWindowRecoveryThresholdPercent,
            LongWindowEarlyWarningEnabled = defaults.LongWindowEarlyWarningEnabled,
            LongWindowEarlyWarningHours = defaults.LongWindowEarlyWarningHours,
            LongWindowEarlyWarningThresholdPercent = defaults.LongWindowEarlyWarningThresholdPercent,
            LongWindowStandardWarningEnabled = defaults.LongWindowStandardWarningEnabled,
            LongWindowStandardWarningHours = defaults.LongWindowStandardWarningHours,
            LongWindowStandardWarningThresholdPercent = defaults.LongWindowStandardWarningThresholdPercent,
            LongWindowFinalWarningEnabled = defaults.LongWindowFinalWarningEnabled,
            LongWindowFinalWarningHours = defaults.LongWindowFinalWarningHours,
            LongWindowFinalWarningThresholdPercent = defaults.LongWindowFinalWarningThresholdPercent,
            LongWindowResetCompletedEnabled = defaults.LongWindowResetCompletedEnabled,
            ResetInferenceUsageDropPoints = defaults.ResetInferenceUsageDropPoints,
            GmailNotificationEnabled = false,
            GmailRecipient = defaults.GmailRecipient,
        };
        ApplySettings(restored);
        ValidateAndTrackChanges();
        OperationMessage = "画面の設定を初期値へ戻しました。保存するまで反映されません。";
    }

    /// <summary>
    /// 設定モデルを画面編集値へ反映します。
    /// </summary>
    /// <param name="settings">表示する設定です。</param>
    private void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        isApplyingValues = true;
        WindowsNotificationEnabled = settings.WindowsNotificationEnabled;
        QuietHoursEnabled = settings.QuietHoursEnabled;
        QuietHoursStart = settings.QuietHoursStart.ToString("HH:mm", CultureInfo.InvariantCulture);
        QuietHoursEnd = settings.QuietHoursEnd.ToString("HH:mm", CultureInfo.InvariantCulture);
        FallbackPollingMinutes = settings.FallbackPollingMinutes.ToString(CultureInfo.InvariantCulture);
        AutoStartEnabled = settings.AutoStartEnabled;
        ShortWindowRecoveryEnabled = settings.ShortWindowRecoveryEnabled;
        ShortWindowRecoveryThresholdPercent = settings.ShortWindowRecoveryThresholdPercent.ToString(CultureInfo.InvariantCulture);
        LongWindowEarlyWarningEnabled = settings.LongWindowEarlyWarningEnabled;
        LongWindowEarlyWarningHours = settings.LongWindowEarlyWarningHours.ToString(CultureInfo.InvariantCulture);
        LongWindowEarlyWarningThresholdPercent = settings.LongWindowEarlyWarningThresholdPercent.ToString(CultureInfo.InvariantCulture);
        LongWindowStandardWarningEnabled = settings.LongWindowStandardWarningEnabled;
        LongWindowStandardWarningHours = settings.LongWindowStandardWarningHours.ToString(CultureInfo.InvariantCulture);
        LongWindowStandardWarningThresholdPercent = settings.LongWindowStandardWarningThresholdPercent.ToString(CultureInfo.InvariantCulture);
        LongWindowFinalWarningEnabled = settings.LongWindowFinalWarningEnabled;
        LongWindowFinalWarningHours = settings.LongWindowFinalWarningHours.ToString(CultureInfo.InvariantCulture);
        LongWindowFinalWarningThresholdPercent = settings.LongWindowFinalWarningThresholdPercent.ToString(CultureInfo.InvariantCulture);
        LongWindowResetCompletedEnabled = settings.LongWindowResetCompletedEnabled;
        GmailNotificationEnabled = settings.GmailNotificationEnabled;
        GmailRecipient = settings.GmailRecipient ?? string.Empty;
        resetInferenceUsageDropPoints = settings.ResetInferenceUsageDropPoints;
        isApplyingValues = false;
        ValidateAndTrackChanges();
    }

    /// <summary>
    /// すべての入力を検証し、エラー表示、変更状態、および保存可否を更新します。
    /// </summary>
    private void ValidateAndTrackChanges()
    {
        bool quietStartValid = TryParseTime(QuietHoursStart, out _);
        bool quietEndValid = TryParseTime(QuietHoursEnd, out _);
        QuietHoursError = quietStartValid && quietEndValid
            ? string.Empty
            : "開始時刻と終了時刻はHH:mm形式で入力してください。日付をまたぐ指定も使用できます。";

        FallbackPollingError = TryParseRange(FallbackPollingMinutes, 1, 1440, out _)
            ? string.Empty
            : "補助確認間隔は1～1440分で入力してください。";
        ShortWindowThresholdError = TryParseRange(ShortWindowRecoveryThresholdPercent, 1, 100, out _)
            ? string.Empty
            : "回復通知閾値は1～100%で入力してください。";
        EarlyThresholdError = TryParseRange(LongWindowEarlyWarningThresholdPercent, 1, 100, out _)
            ? string.Empty
            : "Early残量閾値は1～100%で入力してください。";
        StandardThresholdError = TryParseRange(LongWindowStandardWarningThresholdPercent, 1, 100, out _)
            ? string.Empty
            : "Standard残量閾値は1～100%で入力してください。";
        FinalThresholdError = TryParseRange(LongWindowFinalWarningThresholdPercent, 1, 100, out _)
            ? string.Empty
            : "Final残量閾値は1～100%で入力してください。";

        bool earlyValid = TryParsePositive(LongWindowEarlyWarningHours, out int earlyHours);
        bool standardValid = TryParsePositive(LongWindowStandardWarningHours, out int standardHours);
        bool finalValid = TryParsePositive(LongWindowFinalWarningHours, out int finalHours);
        EarlyHoursError = earlyValid ? string.Empty : "Early残り時間は正の整数で入力してください。";
        StandardHoursError = standardValid ? string.Empty : "Standard残り時間は正の整数で入力してください。";
        FinalHoursError = finalValid ? string.Empty : "Final残り時間は正の整数で入力してください。";
        if (earlyValid && standardValid && finalValid && !(earlyHours > standardHours && standardHours > finalHours))
        {
            const string orderError = "残り時間はEarly > Standard > Finalの順にしてください。";
            EarlyHoursError = orderError;
            StandardHoursError = orderError;
            FinalHoursError = orderError;
        }

        GmailRecipientError = AppSettings.IsValidOptionalEmailAddress(GmailRecipient.Trim())
            ? string.Empty
            : "送信先をメールアドレス形式で入力してください。";
        if (GmailNotificationEnabled && string.IsNullOrWhiteSpace(GmailRecipient))
        {
            GmailRecipientError = "Gmail通知を有効にする場合は送信先を入力してください。";
        }
        GmailNotificationError = GmailNotificationEnabled && !CanEnableGmailNotification
            ? "Gmail通知は、Googleアカウント認証済みかつ有効な送信先がある場合だけ有効にできます。"
            : string.Empty;

        HasUnsavedChanges = !string.Equals(
            baselineSignature,
            CaptureEditSignature(),
            StringComparison.Ordinal);
        UpdateGmailActionAvailability();
        UpdateCanSave();
        if (TryCreateSettings(out AppSettings candidate))
        {
            RefreshRateLimits(candidate);
        }
    }

    /// <summary>
    /// 入力エラー、変更状態、および処理中状態から保存可否を更新します。
    /// </summary>
    private void UpdateCanSave()
    {
        bool hasErrors = new[]
        {
            QuietHoursError,
            FallbackPollingError,
            ShortWindowThresholdError,
            EarlyHoursError,
            EarlyThresholdError,
            StandardHoursError,
            StandardThresholdError,
            FinalHoursError,
            FinalThresholdError,
            GmailNotificationError,
            GmailRecipientError,
        }.Any(value => !string.IsNullOrEmpty(value));
        CanSave = HasUnsavedChanges && !hasErrors && !IsBusy;
    }

    /// <summary>
    /// 検証済みの画面入力から保存用設定を生成します。
    /// </summary>
    /// <param name="settings">生成できた設定です。</param>
    /// <returns>すべての値を解釈してモデル検証にも成功した場合はtrueです。</returns>
    private bool TryCreateSettings(out AppSettings settings)
    {
        settings = null!;
        if (!TryParseTime(QuietHoursStart, out TimeOnly quietStart)
            || !TryParseTime(QuietHoursEnd, out TimeOnly quietEnd)
            || !TryParseRange(FallbackPollingMinutes, 1, 1440, out int pollingMinutes)
            || !TryParseRange(ShortWindowRecoveryThresholdPercent, 1, 100, out int shortThreshold)
            || !TryParsePositive(LongWindowEarlyWarningHours, out int earlyHours)
            || !TryParseRange(LongWindowEarlyWarningThresholdPercent, 1, 100, out int earlyThreshold)
            || !TryParsePositive(LongWindowStandardWarningHours, out int standardHours)
            || !TryParseRange(LongWindowStandardWarningThresholdPercent, 1, 100, out int standardThreshold)
            || !TryParsePositive(LongWindowFinalWarningHours, out int finalHours)
            || !TryParseRange(LongWindowFinalWarningThresholdPercent, 1, 100, out int finalThreshold)
            || !(earlyHours > standardHours && standardHours > finalHours)
            || (GmailNotificationEnabled && string.IsNullOrWhiteSpace(GmailRecipient))
            || !AppSettings.IsValidOptionalEmailAddress(GmailRecipient.Trim()))
        {
            return false;
        }

        settings = baselineSettings with
        {
            WindowsNotificationEnabled = WindowsNotificationEnabled,
            QuietHoursEnabled = QuietHoursEnabled,
            QuietHoursStart = quietStart,
            QuietHoursEnd = quietEnd,
            FallbackPollingMinutes = pollingMinutes,
            AutoStartEnabled = AutoStartEnabled,
            ShortWindowRecoveryEnabled = ShortWindowRecoveryEnabled,
            ShortWindowRecoveryThresholdPercent = shortThreshold,
            LongWindowEarlyWarningEnabled = LongWindowEarlyWarningEnabled,
            LongWindowEarlyWarningHours = earlyHours,
            LongWindowEarlyWarningThresholdPercent = earlyThreshold,
            LongWindowStandardWarningEnabled = LongWindowStandardWarningEnabled,
            LongWindowStandardWarningHours = standardHours,
            LongWindowStandardWarningThresholdPercent = standardThreshold,
            LongWindowFinalWarningEnabled = LongWindowFinalWarningEnabled,
            LongWindowFinalWarningHours = finalHours,
            LongWindowFinalWarningThresholdPercent = finalThreshold,
            LongWindowResetCompletedEnabled = LongWindowResetCompletedEnabled,
            ResetInferenceUsageDropPoints = resetInferenceUsageDropPoints,
            GmailNotificationEnabled = GmailNotificationEnabled,
            GmailRecipient = string.IsNullOrWhiteSpace(GmailRecipient) ? null : GmailRecipient.Trim(),
        };
        return settings.IsValid();
    }

    /// <summary>
    /// 観測済み利用枠へ現在の編集設定を適用した表示一覧を再構築します。
    /// </summary>
    /// <param name="settings">表示に使用する未保存を含む設定です。</param>
    private void RefreshRateLimits(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        RateLimits.Clear();
        if (observedSnapshot is null)
        {
            return;
        }

        foreach (RateLimitWindow window in observedSnapshot.RateLimits)
        {
            RateLimitNotificationSetting applied = RateLimitNotificationSettingsResolver.Resolve(window, settings);
            RateLimits.Add(new RateLimitSettingItemViewModel
            {
                LimitId = window.LimitId ?? "不明",
                Position = window.Position,
                WindowDurationMinutes = window.WindowDurationMinutes ?? 0,
                Classification = window.Classification,
                AppliedNotifications = FormatNotifications(applied),
                IsNotificationEnabled = applied.IsAnyEnabled,
                NotificationStatus = window.Classification == RateLimitClassification.Unknown
                    ? "利用期間の意味を識別できないため、通知対象外です"
                    : applied.IsAnyEnabled ? "通知有効" : "通知無効",
            });
        }
    }

    /// <summary>
    /// 利用枠別設定の有効通知を短い表示へ変換します。
    /// </summary>
    /// <param name="setting">表示対象の通知設定です。</param>
    /// <returns>有効な通知種類または「なし」です。</returns>
    private static string FormatNotifications(RateLimitNotificationSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        List<string> values = [];
        if (setting.ShortWindowRecoveryEnabled) values.Add("短期回復");
        if (setting.LongWindowEarlyWarningEnabled) values.Add("Early");
        if (setting.LongWindowStandardWarningEnabled) values.Add("Standard");
        if (setting.LongWindowFinalWarningEnabled) values.Add("Final");
        if (setting.LongWindowResetCompletedEnabled) values.Add("リセット完了");
        return values.Count == 0 ? "なし" : string.Join(" / ", values);
    }

    /// <summary>
    /// 未保存変更の比較に使用する画面値の署名を生成します。
    /// </summary>
    /// <returns>すべての編集項目を順序固定で連結した文字列です。</returns>
    private string CaptureEditSignature()
    {
        return string.Join(
            '\u001f',
            WindowsNotificationEnabled,
            QuietHoursEnabled,
            QuietHoursStart,
            QuietHoursEnd,
            FallbackPollingMinutes,
            AutoStartEnabled,
            ShortWindowRecoveryEnabled,
            ShortWindowRecoveryThresholdPercent,
            LongWindowEarlyWarningEnabled,
            LongWindowEarlyWarningHours,
            LongWindowEarlyWarningThresholdPercent,
            LongWindowStandardWarningEnabled,
            LongWindowStandardWarningHours,
            LongWindowStandardWarningThresholdPercent,
            LongWindowFinalWarningEnabled,
            LongWindowFinalWarningHours,
            LongWindowFinalWarningThresholdPercent,
            LongWindowResetCompletedEnabled,
            GmailNotificationEnabled,
            GmailRecipient,
            resetInferenceUsageDropPoints);
    }

    /// <summary>
    /// 時刻入力を厳密なHH:mm形式で解釈します。
    /// </summary>
    /// <param name="value">解釈する入力です。</param>
    /// <param name="result">解釈できた時刻です。</param>
    /// <returns>解釈に成功した場合はtrueです。</returns>
    private static bool TryParseTime(string value, out TimeOnly result)
    {
        return TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    /// <summary>
    /// 整数入力が指定範囲内か検証します。
    /// </summary>
    /// <param name="value">検証する入力です。</param>
    /// <param name="minimum">許容する最小値です。</param>
    /// <param name="maximum">許容する最大値です。</param>
    /// <param name="result">解釈できた整数です。</param>
    /// <returns>整数かつ範囲内の場合はtrueです。</returns>
    private static bool TryParseRange(string value, int minimum, int maximum, out int result)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result)
            && result >= minimum
            && result <= maximum;
    }

    /// <summary>
    /// 整数入力が正の値か検証します。
    /// </summary>
    /// <param name="value">検証する入力です。</param>
    /// <param name="result">解釈できた整数です。</param>
    /// <returns>正の整数の場合はtrueです。</returns>
    private static bool TryParsePositive(string value, out int result)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result)
            && result > 0;
    }

    /// <summary>
    /// 編集可能な値を更新し、入力検証と変更追跡を実行します。
    /// </summary>
    /// <typeparam name="T">更新する値の型です。</typeparam>
    /// <param name="field">更新対象のフィールドです。</param>
    /// <param name="value">新しい値です。</param>
    /// <param name="propertyName">変更されたプロパティ名です。</param>
    private void SetEditableProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value, propertyName) && !isApplyingValues)
        {
            ValidateAndTrackChanges();
        }
    }

    /// <summary>
    /// 値を更新し、変更された場合だけ通知します。
    /// </summary>
    /// <typeparam name="T">更新する値の型です。</typeparam>
    /// <param name="field">更新対象のフィールドです。</param>
    /// <param name="value">新しい値です。</param>
    /// <param name="propertyName">変更されたプロパティ名です。</param>
    /// <returns>値が変更された場合はtrueです。</returns>
    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
