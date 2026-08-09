using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Application.State;
using CodexUsageNotifier.Application.Gmail;
using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Presentation.ViewModels;
using CodexUsageNotifier.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageNotifier.Tests.Presentation.ViewModels;

/// <summary>
/// Phase 4A設定画面の読み込み、検証、保存、および変更破棄を検証します。
/// </summary>
[TestClass]
public sealed class SettingsViewModelTests
{
    /// <summary>
    /// 保存済み初期設定を画面へ読み込み、未変更状態になることを検証します。
    /// </summary>
    [TestMethod]
    public async Task LoadAsync_DefaultSettings_LoadsInitialValues()
    {
        TestContext context = CreateContext(AppSettings.CreateDefault());

        await context.ViewModel.LoadAsync(CancellationToken.None);

        Assert.IsTrue(context.ViewModel.WindowsNotificationEnabled);
        Assert.AreEqual("99", context.ViewModel.ShortWindowRecoveryThresholdPercent);
        Assert.AreEqual("48", context.ViewModel.LongWindowEarlyWarningHours);
        Assert.IsFalse(context.ViewModel.GmailNotificationEnabled);
        Assert.IsFalse(context.ViewModel.HasUnsavedChanges);
        Assert.IsFalse(context.ViewModel.CanSave);
    }

    /// <summary>
    /// 編集値を保存するとJSON保存先と監視反映先へ同じ設定が渡ることを検証します。
    /// </summary>
    [TestMethod]
    public async Task SaveAsync_EditedSettings_SavesAndAppliesToMonitor()
    {
        TestContext context = CreateContext(AppSettings.CreateDefault());
        await context.ViewModel.LoadAsync(CancellationToken.None);
        context.ViewModel.FallbackPollingMinutes = "30";
        context.ViewModel.ShortWindowRecoveryThresholdPercent = "95";

        bool result = await context.ViewModel.SaveAsync(CancellationToken.None);

        Assert.IsTrue(result);
        Assert.AreEqual(30, context.SettingsRepository.Settings.FallbackPollingMinutes);
        Assert.AreEqual(95, context.SettingsRepository.Settings.ShortWindowRecoveryThresholdPercent);
        Assert.AreSame(context.SettingsRepository.Settings, context.SettingsSink.AppliedSettings);
        Assert.IsFalse(context.ViewModel.HasUnsavedChanges);
    }

    /// <summary>
    /// キャンセル時は編集値を破棄し、永続設定を変更しないことを検証します。
    /// </summary>
    [TestMethod]
    public async Task DiscardChanges_EditedSettings_RestoresLoadedValues()
    {
        TestContext context = CreateContext(AppSettings.CreateDefault());
        await context.ViewModel.LoadAsync(CancellationToken.None);
        context.ViewModel.ShortWindowRecoveryThresholdPercent = "80";

        context.ViewModel.DiscardChanges();

        Assert.AreEqual("99", context.ViewModel.ShortWindowRecoveryThresholdPercent);
        Assert.AreEqual(0, context.SettingsRepository.SaveCount);
        Assert.IsFalse(context.ViewModel.HasUnsavedChanges);
    }

    /// <summary>
    /// 初期値へ戻す操作が画面対象項目を既定値へ戻し、未保存変更として扱うことを検証します。
    /// </summary>
    [TestMethod]
    public async Task RestoreDefaults_NonDefaultSettings_RestoresEditableDefaults()
    {
        AppSettings customized = AppSettings.CreateDefault() with
        {
            QuietHoursStart = new TimeOnly(22, 0),
            FallbackPollingMinutes = 15,
            ShortWindowRecoveryThresholdPercent = 90,
        };
        TestContext context = CreateContext(customized);
        await context.ViewModel.LoadAsync(CancellationToken.None);

        context.ViewModel.RestoreDefaults();

        Assert.AreEqual("00:00", context.ViewModel.QuietHoursStart);
        Assert.AreEqual("60", context.ViewModel.FallbackPollingMinutes);
        Assert.AreEqual("99", context.ViewModel.ShortWindowRecoveryThresholdPercent);
        Assert.IsTrue(context.ViewModel.HasUnsavedChanges);
        Assert.IsTrue(context.ViewModel.CanSave);
    }

    /// <summary>
    /// 通知閾値の1%と100%を許容し、範囲外では保存を無効化することを検証します。
    /// </summary>
    [TestMethod]
    public async Task Thresholds_BoundaryValues_ValidateOneThroughOneHundred()
    {
        TestContext context = CreateContext(AppSettings.CreateDefault());
        await context.ViewModel.LoadAsync(CancellationToken.None);

        context.ViewModel.ShortWindowRecoveryThresholdPercent = "1";
        context.ViewModel.LongWindowEarlyWarningThresholdPercent = "100";
        Assert.IsTrue(context.ViewModel.CanSave);

        context.ViewModel.ShortWindowRecoveryThresholdPercent = "0";
        Assert.IsFalse(context.ViewModel.CanSave);
        StringAssert.Contains(context.ViewModel.ShortWindowThresholdError, "1～100");

        context.ViewModel.ShortWindowRecoveryThresholdPercent = "101";
        Assert.IsFalse(context.ViewModel.CanSave);
    }

    /// <summary>
    /// Early、Standard、Finalの残り時間が降順でない場合に保存できないことを検証します。
    /// </summary>
    [TestMethod]
    public async Task LongWindowHours_InvalidOrder_DisablesSave()
    {
        TestContext context = CreateContext(AppSettings.CreateDefault());
        await context.ViewModel.LoadAsync(CancellationToken.None);

        context.ViewModel.LongWindowEarlyWarningHours = "12";
        context.ViewModel.LongWindowStandardWarningHours = "24";

        Assert.IsFalse(context.ViewModel.CanSave);
        StringAssert.Contains(context.ViewModel.EarlyHoursError, "Early > Standard > Final");
    }

    /// <summary>
    /// 入力済みGmail送信先がメールアドレス形式でない場合に保存できないことを検証します。
    /// </summary>
    [TestMethod]
    public async Task GmailRecipient_InvalidAddress_DisablesSave()
    {
        TestContext context = CreateContext(AppSettings.CreateDefault());
        await context.ViewModel.LoadAsync(CancellationToken.None);

        context.ViewModel.GmailRecipient = "not-an-email";
        Assert.IsFalse(context.ViewModel.CanSave);
        StringAssert.Contains(context.ViewModel.GmailRecipientError, "メールアドレス形式");

        context.ViewModel.GmailRecipient = "user@example.com";
        Assert.IsTrue(context.ViewModel.CanSave);
    }

    /// <summary>
    /// 開始時刻が終了時刻より後の日付をまたぐ通知禁止時間を許容することを検証します。
    /// </summary>
    [TestMethod]
    public async Task QuietHours_CrossesMidnight_RemainsValid()
    {
        TestContext context = CreateContext(AppSettings.CreateDefault());
        await context.ViewModel.LoadAsync(CancellationToken.None);

        context.ViewModel.QuietHoursStart = "23:30";
        context.ViewModel.QuietHoursEnd = "06:45";

        Assert.AreEqual(string.Empty, context.ViewModel.QuietHoursError);
        Assert.IsTrue(context.ViewModel.CanSave);
    }

    /// <summary>
    /// 設定保存時に既存の通知済み状態と回復連番を更新しないことを検証します。
    /// </summary>
    [TestMethod]
    public async Task SaveAsync_DoesNotResetNotificationOrRecoveryState()
    {
        ApplicationState originalState = CreateStateWithWindows();
        TestContext context = CreateContext(AppSettings.CreateDefault(), originalState);
        await context.ViewModel.LoadAsync(CancellationToken.None);
        context.ViewModel.WindowsNotificationEnabled = false;

        await context.ViewModel.SaveAsync(CancellationToken.None);
        ApplicationState stateAfterSave = await context.StateStore.LoadAsync(CancellationToken.None);

        Assert.AreSame(originalState, stateAfterSave);
        Assert.AreEqual(1, stateAfterSave.RateLimitNotificationStates.Count);
        Assert.AreEqual(3, stateAfterSave.RateLimitRecoveryStates.Single().RecoverySequence);
    }

    /// <summary>
    /// 永続化に失敗した場合は読み込み済み設定と未保存編集を維持し、監視へ反映しないことを検証します。
    /// </summary>
    [TestMethod]
    public async Task SaveAsync_RepositoryFailure_KeepsOriginalSettings()
    {
        TestContext context = CreateContext(AppSettings.CreateDefault());
        await context.ViewModel.LoadAsync(CancellationToken.None);
        context.SettingsRepository.ThrowOnSave = true;
        context.ViewModel.FallbackPollingMinutes = "30";

        bool result = await context.ViewModel.SaveAsync(CancellationToken.None);

        Assert.IsFalse(result);
        Assert.AreEqual(60, context.SettingsRepository.Settings.FallbackPollingMinutes);
        Assert.IsNull(context.SettingsSink.AppliedSettings);
        Assert.IsTrue(context.ViewModel.HasUnsavedChanges);
        StringAssert.Contains(context.ViewModel.OperationMessage, "元の設定");
    }

    /// <summary>
    /// Unknown枠を説明付きで表示し、既定通知対象外として扱うことを検証します。
    /// </summary>
    [TestMethod]
    public async Task LoadAsync_UnknownWindow_ShowsExcludedReadOnlyStatus()
    {
        TestContext context = CreateContext(AppSettings.CreateDefault(), CreateStateWithWindows());

        await context.ViewModel.LoadAsync(CancellationToken.None);
        RateLimitSettingItemViewModel unknown = context.ViewModel.RateLimits.Single(
            item => item.Classification == RateLimitClassification.Unknown);

        Assert.IsFalse(unknown.IsNotificationEnabled);
        Assert.AreEqual("利用期間の意味を識別できないため、通知対象外です", unknown.NotificationStatus);
    }

    /// <summary>
    /// Gmail未認証ではGmail通知を有効化して保存できないことを検証します。
    /// </summary>
    [TestMethod]
    public async Task GmailNotification_Unauthenticated_CannotEnable()
    {
        TestContext context = CreateContext(AppSettings.CreateDefault());
        await context.ViewModel.LoadAsync(CancellationToken.None);

        context.ViewModel.GmailNotificationEnabled = true;

        Assert.IsTrue(context.ViewModel.IsGmailAuthenticationAvailable);
        Assert.IsFalse(context.ViewModel.CanSave);
        StringAssert.Contains(context.ViewModel.GmailNotificationError, "認証済み");
    }

    /// <summary>
    /// 認証成功時に空のGmail送信先へ認証済みアドレスを初期設定することを検証します。
    /// </summary>
    [TestMethod]
    public async Task AuthenticateGmailAsync_EmptyRecipient_UsesAuthenticatedAddress()
    {
        TestContext context = CreateContext(AppSettings.CreateDefault());
        await context.ViewModel.LoadAsync(CancellationToken.None);

        await context.ViewModel.AuthenticateGmailAsync(false, CancellationToken.None);

        Assert.AreEqual("user@example.com", context.ViewModel.GmailRecipient);
        Assert.AreEqual("認証済み", context.ViewModel.GmailAuthenticationStatus);
        Assert.IsFalse(context.ViewModel.IsGmailAuthenticationAvailable);
        Assert.IsTrue(context.ViewModel.IsGmailReauthenticationAvailable);
        Assert.IsTrue(context.ViewModel.IsTestEmailAvailable);
    }

    /// <summary>再認証成功時に現在のGmail配送有効期間を更新することを検証します。</summary>
    [TestMethod]
    public async Task AuthenticateGmailAsync_Reauthentication_UpdatesDeliveryBoundary()
    {
        DateTimeOffset enabledSinceUtc = new(2026, 8, 9, 11, 0, 0, TimeSpan.Zero);
        AppSettings settings = AppSettings.CreateDefault() with
        {
            GmailNotificationEnabled = true,
            GmailRecipient = "target@example.com",
        };
        TestContext context = CreateContext(
            settings,
            timeProvider: new FixedTimeProvider(enabledSinceUtc));
        context.AuthenticationService.Status = new GmailAuthenticationStatus
        {
            State = GmailAuthenticationState.ReauthenticationRequired,
            HasClientConfiguration = true,
        };
        await context.ViewModel.LoadAsync(CancellationToken.None);

        await context.ViewModel.AuthenticateGmailAsync(true, CancellationToken.None);

        ApplicationState state = await context.StateStore.LoadAsync(CancellationToken.None);
        Assert.AreEqual(enabledSinceUtc, state.GmailDeliveryEnabledSinceUtc);
        Assert.IsTrue(state.GmailDeliveryEnabledLastObserved);
        Assert.IsTrue(state.GmailAuthenticationWasUsable);
    }

    /// <summary>
    /// 認証済みかつ送信先が有効な場合にGmail通知をtrueとして保存できることを検証します。
    /// </summary>
    [TestMethod]
    public async Task SaveAsync_AuthenticatedGmail_AllowsGmailNotification()
    {
        DateTimeOffset enabledSinceUtc = new(2026, 8, 9, 10, 0, 0, TimeSpan.Zero);
        TestContext context = CreateContext(
            AppSettings.CreateDefault(),
            timeProvider: new FixedTimeProvider(enabledSinceUtc));
        await context.ViewModel.LoadAsync(CancellationToken.None);
        await context.ViewModel.AuthenticateGmailAsync(false, CancellationToken.None);
        context.ViewModel.GmailNotificationEnabled = true;

        bool result = await context.ViewModel.SaveAsync(CancellationToken.None);

        Assert.IsTrue(result);
        Assert.IsTrue(context.SettingsRepository.Settings.GmailNotificationEnabled);
        Assert.AreEqual("user@example.com", context.SettingsRepository.Settings.GmailRecipient);
        ApplicationState state = await context.StateStore.LoadAsync(CancellationToken.None);
        Assert.AreEqual(enabledSinceUtc, state.GmailDeliveryEnabledSinceUtc);
        Assert.IsTrue(state.GmailDeliveryEnabledLastObserved);
        Assert.IsTrue(state.GmailAuthenticationWasUsable);
    }

    /// <summary>
    /// 認証解除で永続設定のGmail通知をfalseにし、送信先は維持することを検証します。
    /// </summary>
    [TestMethod]
    public async Task DisconnectGmailAsync_EnabledSetting_DisablesNotificationAndKeepsRecipient()
    {
        AppSettings settings = AppSettings.CreateDefault() with
        {
            GmailNotificationEnabled = true,
            GmailRecipient = "target@example.com",
        };
        TestContext context = CreateContext(settings);
        context.AuthenticationService.Status = new GmailAuthenticationStatus
        {
            State = GmailAuthenticationState.Authenticated,
            HasClientConfiguration = true,
            AuthenticatedEmailAddress = "user@example.com",
        };
        await context.ViewModel.LoadAsync(CancellationToken.None);

        await context.ViewModel.DisconnectGmailAsync(CancellationToken.None);

        Assert.IsFalse(context.SettingsRepository.Settings.GmailNotificationEnabled);
        Assert.AreEqual("target@example.com", context.SettingsRepository.Settings.GmailRecipient);
        Assert.IsFalse(context.ViewModel.IsTestEmailAvailable);
    }

    /// <summary>
    /// ViewModelのテスト送信が専用送信サービスだけを呼び、本番状態を変更しないことを検証します。
    /// </summary>
    [TestMethod]
    public async Task SendGmailTestMailAsync_Authenticated_DoesNotChangeApplicationState()
    {
        ApplicationState original = CreateStateWithWindows();
        TestContext context = CreateContext(AppSettings.CreateDefault(), original);
        context.AuthenticationService.Status = new GmailAuthenticationStatus
        {
            State = GmailAuthenticationState.Authenticated,
            HasClientConfiguration = true,
            AuthenticatedEmailAddress = "user@example.com",
        };
        await context.ViewModel.LoadAsync(CancellationToken.None);
        context.ViewModel.GmailRecipient = "target@example.com";

        await context.ViewModel.SendGmailTestMailAsync(CancellationToken.None);
        ApplicationState after = await context.StateStore.LoadAsync(CancellationToken.None);

        Assert.AreEqual(1, context.TestMailSender.SendCallCount);
        Assert.AreSame(original, after);
    }

    /// <summary>
    /// テスト対象のViewModelとインメモリ依存関係を生成します。
    /// </summary>
    /// <param name="settings">初期設定です。</param>
    /// <param name="state">初期状態です。</param>
    /// <returns>テスト操作に必要な依存関係です。</returns>
    private static TestContext CreateContext(
        AppSettings settings,
        ApplicationState? state = null,
        TimeProvider? timeProvider = null)
    {
        InMemorySettingsRepository settingsRepository = new(settings);
        InMemoryStateRepository stateRepository = new(state ?? ApplicationState.CreateDefault());
        ApplicationStateStore stateStore = new(stateRepository);
        RecordingSettingsChangeSink settingsSink = new();
        StubGoogleOAuthClientConfigurationService configurationService = new();
        StubGmailAuthenticationService authenticationService = new();
        StubGmailTestMailSender testMailSender = new();
        SettingsViewModel viewModel = new(
            settingsRepository,
            stateStore,
            settingsSink,
            configurationService,
            authenticationService,
            testMailSender,
            timeProvider ?? TimeProvider.System,
            NullLogger<SettingsViewModel>.Instance);
        return new TestContext(
            viewModel,
            settingsRepository,
            stateStore,
            settingsSink,
            authenticationService,
            testMailSender);
    }

    /// <summary>固定UTC時刻を返すテスト用時刻提供元です。</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset utcNow;

        /// <summary>固定して返すUTC時刻を受け取ります。</summary>
        public FixedTimeProvider(DateTimeOffset utcNow) => this.utcNow = utcNow;

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    /// <summary>
    /// FiveHour、Weekly、Unknownと通知状態を含むテスト用状態を生成します。
    /// </summary>
    /// <returns>既存通知状態と回復連番を含む状態です。</returns>
    private static ApplicationState CreateStateWithWindows()
    {
        return new ApplicationState
        {
            LastUsageSnapshot = new UsageSnapshot
            {
                CapturedAtUtc = new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero),
                RateLimits =
                [
                    CreateWindow(300, RateLimitClassification.FiveHour, RateLimitPosition.Primary),
                    CreateWindow(10080, RateLimitClassification.Weekly, RateLimitPosition.Secondary),
                    CreateWindow(1440, RateLimitClassification.Unknown, RateLimitPosition.Primary, "other"),
                ],
            },
            RateLimitNotificationStates =
            [
                new RateLimitNotificationState
                {
                    LimitId = "codex",
                    Position = RateLimitPosition.Primary,
                    WindowDurationMinutes = 300,
                    RecoveryWindowId = "reset:1",
                    NotificationType = RateLimitNotificationType.ShortWindowRecovered,
                    NotificationStage = RateLimitNotificationStage.Recovered,
                    WindowsDeliveryStatus = DeliveryStatus.Succeeded,
                },
            ],
            RateLimitRecoveryStates =
            [
                new RateLimitRecoveryState
                {
                    LimitId = "codex",
                    Position = RateLimitPosition.Primary,
                    WindowDurationMinutes = 300,
                    RecoverySequence = 3,
                },
            ],
        };
    }

    /// <summary>
    /// 指定分類のテスト用利用枠を生成します。
    /// </summary>
    /// <param name="duration">期間の分数です。</param>
    /// <param name="classification">利用枠分類です。</param>
    /// <param name="position">レスポンス内の位置です。</param>
    /// <param name="limitId">利用枠識別子です。</param>
    /// <returns>設定画面へ表示できる利用枠です。</returns>
    private static RateLimitWindow CreateWindow(
        int duration,
        RateLimitClassification classification,
        RateLimitPosition position,
        string limitId = "codex")
    {
        return new RateLimitWindow
        {
            LimitId = limitId,
            Position = position,
            WindowDurationMinutes = duration,
            Classification = classification,
        };
    }

    /// <summary>
    /// ViewModelテストで共有する依存関係を保持します。
    /// </summary>
    private sealed class TestContext
    {
        /// <summary>
        /// テスト対象と依存関係を受け取ります。
        /// </summary>
        /// <param name="viewModel">テスト対象の設定ViewModelです。</param>
        /// <param name="settingsRepository">設定のインメモリ保存先です。</param>
        /// <param name="stateStore">通知状態の保持先です。</param>
        /// <param name="settingsSink">監視反映の記録先です。</param>
        public TestContext(
            SettingsViewModel viewModel,
            InMemorySettingsRepository settingsRepository,
            ApplicationStateStore stateStore,
            RecordingSettingsChangeSink settingsSink,
            StubGmailAuthenticationService authenticationService,
            StubGmailTestMailSender testMailSender)
        {
            ViewModel = viewModel;
            SettingsRepository = settingsRepository;
            StateStore = stateStore;
            SettingsSink = settingsSink;
            AuthenticationService = authenticationService;
            TestMailSender = testMailSender;
        }

        /// <summary>
        /// テスト対象の設定ViewModelを取得します。
        /// </summary>
        public SettingsViewModel ViewModel { get; }

        /// <summary>
        /// 設定のインメモリ保存先を取得します。
        /// </summary>
        public InMemorySettingsRepository SettingsRepository { get; }

        /// <summary>
        /// 通知状態の保持先を取得します。
        /// </summary>
        public ApplicationStateStore StateStore { get; }

        /// <summary>
        /// 監視反映の記録先を取得します。
        /// </summary>
        public RecordingSettingsChangeSink SettingsSink { get; }

        /// <summary>
        /// Gmail認証状態を制御するテスト用サービスを取得します。
        /// </summary>
        public StubGmailAuthenticationService AuthenticationService { get; }

        /// <summary>
        /// Gmailテスト送信を記録するテスト用サービスを取得します。
        /// </summary>
        public StubGmailTestMailSender TestMailSender { get; }
    }

    /// <summary>
    /// 設定をメモリ上で読み書きするテスト用リポジトリです。
    /// </summary>
    private sealed class InMemorySettingsRepository : ISettingsRepository
    {
        /// <summary>
        /// 初期設定を受け取ります。
        /// </summary>
        /// <param name="settings">読み込み時に返す設定です。</param>
        public InMemorySettingsRepository(AppSettings settings)
        {
            Settings = settings;
        }

        /// <summary>
        /// 現在保存されている設定を取得します。
        /// </summary>
        public AppSettings Settings { get; private set; }

        /// <summary>
        /// 保存回数を取得します。
        /// </summary>
        public int SaveCount { get; private set; }

        /// <summary>
        /// 保存時にテスト用例外を発生させるかどうかを取得または設定します。
        /// </summary>
        public bool ThrowOnSave { get; set; }

        /// <summary>
        /// 現在の設定を返します。
        /// </summary>
        /// <param name="cancellationToken">読み込みのキャンセル通知です。</param>
        /// <returns>現在の設定です。</returns>
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Settings);
        }

        /// <summary>
        /// 指定設定をメモリへ保存します。
        /// </summary>
        /// <param name="settings">保存する設定です。</param>
        /// <param name="cancellationToken">保存のキャンセル通知です。</param>
        /// <returns>完了済み処理です。</returns>
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(settings);
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnSave)
            {
                throw new IOException("テスト用の設定保存失敗です。");
            }

            Settings = settings;
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// アプリケーション状態をメモリ上で保持するテスト用リポジトリです。
    /// </summary>
    private sealed class InMemoryStateRepository : IApplicationStateRepository
    {
        private ApplicationState state;

        /// <summary>
        /// 初期状態を受け取ります。
        /// </summary>
        /// <param name="state">読み込み時に返す状態です。</param>
        public InMemoryStateRepository(ApplicationState state)
        {
            this.state = state;
        }

        /// <summary>
        /// 現在状態を返します。
        /// </summary>
        /// <param name="cancellationToken">読み込みのキャンセル通知です。</param>
        /// <returns>現在状態です。</returns>
        public Task<ApplicationState> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(state);
        }

        /// <summary>
        /// 指定状態をメモリへ保存します。
        /// </summary>
        /// <param name="newState">保存する状態です。</param>
        /// <param name="cancellationToken">保存のキャンセル通知です。</param>
        /// <returns>完了済み処理です。</returns>
        public Task SaveAsync(ApplicationState newState, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(newState);
            cancellationToken.ThrowIfCancellationRequested();
            state = newState;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 監視処理へ反映された設定を記録するテスト用受け口です。
    /// </summary>
    private sealed class RecordingSettingsChangeSink : ISettingsChangeSink
    {
        /// <summary>
        /// 最後に反映された設定を取得します。
        /// </summary>
        public AppSettings? AppliedSettings { get; private set; }

        /// <summary>
        /// 反映対象設定を記録します。
        /// </summary>
        /// <param name="settings">保存済み設定です。</param>
        /// <param name="cancellationToken">反映のキャンセル通知です。</param>
        /// <returns>完了済み処理です。</returns>
        public Task ApplyAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(settings);
            cancellationToken.ThrowIfCancellationRequested();
            AppliedSettings = settings;
            return Task.CompletedTask;
        }
    }
}
