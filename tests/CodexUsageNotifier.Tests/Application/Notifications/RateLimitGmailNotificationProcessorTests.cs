using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Application.Gmail;
using CodexUsageNotifier.Application.Notifications;
using CodexUsageNotifier.Application.State;
using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Domain.Services;
using CodexUsageNotifier.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageNotifier.Tests.Application.Notifications;

/// <summary>
/// Phase 4C-1のGmail本番配送境界、チャネル独立性、集約、および初回試行状態を検証します。
/// </summary>
[TestClass]
public sealed class RateLimitGmailNotificationProcessorTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 8, 9, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Gmailが有効かつ認証済みなら本番通知を1通送ることを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_GmailEnabledAndAuthenticated_SendsProductionMail()
    {
        TestContext context = CreateContext();

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot([CreateFiveHourWindow("codex")]),
            CreateGmailSettings(windowsEnabled: false),
            CancellationToken.None);

        Assert.AreEqual(1, context.GmailSender.SendCallCount);
        Assert.AreEqual(DeliveryStatus.Succeeded, result.State.RateLimitNotificationStates.Single().GmailDeliveryStatus);
        Assert.AreEqual(1, result.State.RateLimitNotificationStates.Single().GmailAttemptCount);
    }

    /// <summary>Gmailが無効なら認証済みでも本番メールを送らないことを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_GmailDisabled_DoesNotSendProductionMail()
    {
        TestContext context = CreateContext();

        await context.Processor.ProcessAsync(
            CreateSnapshot([CreateFiveHourWindow("codex")]),
            CreateGmailSettings(windowsEnabled: false) with { GmailNotificationEnabled = false },
            CancellationToken.None);

        Assert.AreEqual(0, context.GmailSender.SendCallCount);
    }

    /// <summary>未認証ではGmail設定が有効でも本番メールを送らないことを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_Unauthenticated_DoesNotSendProductionMail()
    {
        StubGmailAuthenticationService authentication = new();
        TestContext context = CreateContext(authentication: authentication);

        await context.Processor.ProcessAsync(
            CreateSnapshot([CreateFiveHourWindow("codex")]),
            CreateGmailSettings(windowsEnabled: false),
            CancellationToken.None);

        Assert.AreEqual(0, context.GmailSender.SendCallCount);
    }

    /// <summary>Windowsを無効にしてもGmailだけを配送できることを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_WindowsDisabledGmailEnabled_SendsOnlyGmail()
    {
        TestContext context = CreateContext();

        await context.Processor.ProcessAsync(
            CreateSnapshot([CreateFiveHourWindow("codex")]),
            CreateGmailSettings(windowsEnabled: false),
            CancellationToken.None);

        Assert.AreEqual(0, context.WindowsSender.SendCount);
        Assert.AreEqual(1, context.GmailSender.SendCallCount);
    }

    /// <summary>Windows成功済みならGmail未送信チャネルだけを配送することを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_WindowsSucceededGmailNotAttempted_SendsOnlyGmail()
    {
        RateLimitWindow window = CreateFiveHourWindow("codex");
        ApplicationState initial = CreateStateWithNotification(
            window,
            DeliveryStatus.Succeeded,
            DeliveryStatus.NotAttempted,
            NowUtc);
        TestContext context = CreateContext(initial);

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot([window]),
            CreateGmailSettings(windowsEnabled: true),
            CancellationToken.None);

        Assert.AreEqual(0, context.WindowsSender.SendCount);
        Assert.AreEqual(1, context.GmailSender.SendCallCount);
        Assert.AreEqual(DeliveryStatus.Succeeded, result.State.RateLimitNotificationStates.Single().WindowsDeliveryStatus);
    }

    /// <summary>Gmail成功済みならWindows未送信チャネルだけを配送することを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_GmailSucceededWindowsNotAttempted_SendsOnlyWindows()
    {
        RateLimitWindow window = CreateFiveHourWindow("codex");
        ApplicationState initial = CreateStateWithNotification(
            window,
            DeliveryStatus.NotAttempted,
            DeliveryStatus.Succeeded,
            NowUtc);
        TestContext context = CreateContext(initial);

        await context.Processor.ProcessAsync(
            CreateSnapshot([window]),
            CreateGmailSettings(windowsEnabled: true),
            CancellationToken.None);

        Assert.AreEqual(1, context.WindowsSender.SendCount);
        Assert.AreEqual(0, context.GmailSender.SendCallCount);
    }

    /// <summary>同じ取得の複数limitId候補を1通へ集約し、各状態を成功にすることを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_MultipleLimitIds_AggregatesAndMarksEachSucceeded()
    {
        TestContext context = CreateContext();
        UsageSnapshot snapshot = CreateSnapshot(
        [
            CreateFiveHourWindow("codex"),
            CreateWeeklyWindow("codex-team", NowUtc.AddHours(23)),
        ]);

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            snapshot,
            CreateGmailSettings(windowsEnabled: false),
            CancellationToken.None);

        Assert.AreEqual(1, context.GmailSender.SendCallCount);
        Assert.AreEqual("Codex Usage Notifier: 2件のお知らせ", context.GmailSender.Messages.Single().Subject);
        StringAssert.Contains(context.GmailSender.Messages.Single().Body, "LimitId: codex-team");
        Assert.IsTrue(result.State.RateLimitNotificationStates.All(
            state => state.GmailDeliveryStatus == DeliveryStatus.Succeeded));
    }

    /// <summary>集約送信失敗時は全候補を初回失敗にし、Windows状態を変更しないことを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_AggregateFailure_MarksEachFailedWithoutChangingWindows()
    {
        StubGmailNotificationSender gmailSender = new()
        {
            Exception = new GmailApiOperationException(
                GmailApiErrorKind.Transient,
                "一時的な通信エラーです。",
                new HttpRequestException()),
        };
        TestContext context = CreateContext(gmailSender: gmailSender);

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot([CreateFiveHourWindow("codex"), CreateWeeklyWindow("team", NowUtc.AddHours(23))]),
            CreateGmailSettings(windowsEnabled: false),
            CancellationToken.None);

        Assert.IsTrue(result.State.RateLimitNotificationStates.All(
            state => state.GmailDeliveryStatus == DeliveryStatus.Failed));
        Assert.IsTrue(result.State.RateLimitNotificationStates.All(state => state.GmailAttemptCount == 1));
        Assert.IsTrue(result.State.RateLimitNotificationStates.All(
            state => state.WindowsDeliveryStatus == DeliveryStatus.NotAttempted));
        Assert.IsTrue(result.State.RateLimitNotificationStates.All(state => state.GmailNextRetryAtUtc is null));
    }

    /// <summary>Phase 4C開始以前に成立したNotAttempted状態を遡って送らないことを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_ConditionBeforeProductionStart_DoesNotSendHistoricalNotification()
    {
        RateLimitWindow window = CreateFiveHourWindow("codex");
        ApplicationState initial = CreateStateWithNotification(
            window,
            DeliveryStatus.Succeeded,
            DeliveryStatus.NotAttempted,
            NowUtc.AddMinutes(-1)) with
        {
            GmailProductionDeliveryStartedAtUtc = NowUtc,
        };
        TestContext context = CreateContext(initial);

        await context.Processor.ProcessAsync(
            CreateSnapshot([window]),
            CreateGmailSettings(windowsEnabled: true),
            CancellationToken.None);

        Assert.AreEqual(0, context.GmailSender.SendCallCount);
    }

    /// <summary>Phase 4C開始時刻以降に成立した候補だけを送信することを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_ConditionAtProductionStart_SendsNotification()
    {
        RateLimitWindow window = CreateFiveHourWindow("codex");
        ApplicationState initial = CreateStateWithNotification(
            window,
            DeliveryStatus.Succeeded,
            DeliveryStatus.NotAttempted,
            NowUtc) with
        {
            GmailProductionDeliveryStartedAtUtc = NowUtc,
        };
        TestContext context = CreateContext(initial);

        await context.Processor.ProcessAsync(
            CreateSnapshot([window]),
            CreateGmailSettings(windowsEnabled: true),
            CancellationToken.None);

        Assert.AreEqual(1, context.GmailSender.SendCallCount);
    }

    /// <summary>再起動相当のストア再生成後も本番配送開始時刻を維持することを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_AfterStoreReload_PreservesProductionStartBoundary()
    {
        InMemoryStateRepository repository = new(new ApplicationState { InitialSetupCompleted = true });
        DateTimeOffset persistedStart;
        using (ApplicationStateStore firstStore = new(repository))
        {
            TestContext first = CreateContext(stateStore: firstStore);
            await first.Processor.ProcessAsync(
                CreateSnapshot([CreateFiveHourWindow("first")]),
                CreateGmailSettings(windowsEnabled: false),
                CancellationToken.None);
            persistedStart = (await firstStore.LoadAsync(CancellationToken.None))
                .GmailProductionDeliveryStartedAtUtc!.Value;
        }

        using ApplicationStateStore secondStore = new(repository);
        TestContext second = CreateContext(stateStore: secondStore);
        ApplicationState reloaded = await secondStore.LoadAsync(CancellationToken.None);

        Assert.AreEqual(persistedStart, reloaded.GmailProductionDeliveryStartedAtUtc);
    }

    /// <summary>通知禁止時間中はWindowsと同様にGmailも送らないことを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_DuringQuietHours_DoesNotSendGmail()
    {
        DateTimeOffset quietUtc = new(2026, 8, 9, 1, 0, 0, TimeSpan.Zero);
        TestContext context = CreateContext(nowUtc: quietUtc);

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            new UsageSnapshot { CapturedAtUtc = quietUtc, RateLimits = [CreateFiveHourWindow("codex")] },
            CreateGmailSettings(windowsEnabled: false) with { QuietHoursEnabled = true },
            CancellationToken.None);

        Assert.AreEqual(0, context.GmailSender.SendCallCount);
        Assert.IsNotNull(result.DeferredUntilUtc);
    }

    /// <summary>通知禁止時間終了後の再評価で有効な保留候補をGmailへ送ることを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_AfterQuietHours_SendsStillValidDeferredGmail()
    {
        RateLimitWindow window = CreateFiveHourWindow("codex");
        ApplicationState initial = CreateStateWithNotification(
            window,
            DeliveryStatus.Succeeded,
            DeliveryStatus.NotAttempted,
            NowUtc.AddHours(-8)) with
        {
            GmailProductionDeliveryStartedAtUtc = NowUtc.AddHours(-9),
            RateLimitNotificationStates =
            [
                CreateStateWithNotification(
                    window,
                    DeliveryStatus.Succeeded,
                    DeliveryStatus.NotAttempted,
                    NowUtc.AddHours(-8)).RateLimitNotificationStates.Single() with
                {
                    DeferredUntilUtc = NowUtc.AddHours(-2),
                },
            ],
        };
        TestContext context = CreateContext(initial);

        await context.Processor.ProcessAsync(
            CreateSnapshot([window]),
            CreateGmailSettings(windowsEnabled: true),
            CancellationToken.None);

        Assert.AreEqual(1, context.GmailSender.SendCallCount);
    }

    /// <summary>時間帯を過ぎたEarlyを送らずGmailだけ期限切れにし、現在のFinalだけを送ることを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_ExpiredEarly_DoesNotSendOldStage()
    {
        RateLimitWindow window = CreateWeeklyWindow("codex", NowUtc.AddHours(5));
        RateLimitNotificationState oldEarly = new()
        {
            LimitId = "codex",
            Position = RateLimitPosition.Secondary,
            WindowDurationMinutes = 10080,
            RecoveryWindowId = RateLimitNotificationPolicy.CreateRecoveryWindowId(window, NowUtc.AddHours(-30)),
            NotificationType = RateLimitNotificationType.LongWindowEarlyWarning,
            NotificationStage = RateLimitNotificationStage.Early,
            ConditionMetAtUtc = NowUtc.AddHours(-30),
            WindowsDeliveryStatus = DeliveryStatus.Succeeded,
            GmailDeliveryStatus = DeliveryStatus.NotAttempted,
            DeferredUntilUtc = NowUtc,
        };
        ApplicationState initial = new()
        {
            InitialSetupCompleted = true,
            GmailProductionDeliveryStartedAtUtc = NowUtc.AddHours(-40),
            RateLimitNotificationStates = [oldEarly],
        };
        TestContext context = CreateContext(initial);

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot([window]),
            CreateGmailSettings(windowsEnabled: true),
            CancellationToken.None);

        RateLimitNotificationState expired = result.State.RateLimitNotificationStates.Single(
            state => state.NotificationStage == RateLimitNotificationStage.Early);
        Assert.AreEqual(DeliveryStatus.Succeeded, expired.WindowsDeliveryStatus);
        Assert.AreEqual(DeliveryStatus.Expired, expired.GmailDeliveryStatus);
        Assert.AreEqual(1, context.GmailSender.SendCallCount);
        StringAssert.Contains(context.GmailSender.Messages.Single().Body, "通知段階: Final");
        Assert.IsFalse(context.GmailSender.Messages.Single().Body.Contains("通知段階: Early", StringComparison.Ordinal));
    }

    /// <summary>Gmail初回失敗後の次回取得ではPhase 4C-1として自動再試行しないことを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_GmailFailed_DoesNotRetryInPhase4C1()
    {
        StubGmailNotificationSender gmailSender = new() { Exception = new InvalidOperationException("失敗") };
        TestContext context = CreateContext(gmailSender: gmailSender);
        RateLimitWindow window = CreateFiveHourWindow("codex");
        AppSettings settings = CreateGmailSettings(windowsEnabled: false);
        await context.Processor.ProcessAsync(CreateSnapshot([window]), settings, CancellationToken.None);
        gmailSender.Exception = null;

        await context.Processor.ProcessAsync(
            new UsageSnapshot { CapturedAtUtc = NowUtc.AddMinutes(1), RateLimits = [window] },
            settings,
            CancellationToken.None);

        Assert.AreEqual(1, gmailSender.SendCallCount);
    }

    /// <summary>テスト用コンテキストを生成します。</summary>
    private static TestContext CreateContext(
        ApplicationState? initialState = null,
        StubGmailAuthenticationService? authentication = null,
        StubGmailNotificationSender? gmailSender = null,
        DateTimeOffset? nowUtc = null,
        ApplicationStateStore? stateStore = null)
    {
        ApplicationStateStore actualStore = stateStore
            ?? new ApplicationStateStore(new InMemoryStateRepository(
                initialState ?? new ApplicationState { InitialSetupCompleted = true }));
        RecordingWindowsNotificationSender windowsSender = new();
        StubGmailAuthenticationService actualAuthentication = authentication ?? CreateAuthenticatedService();
        StubGmailNotificationSender actualGmailSender = gmailSender ?? new StubGmailNotificationSender();
        MutableTimeProvider timeProvider = new(nowUtc ?? NowUtc);
        RateLimitNotificationProcessor processor = new(
            actualStore,
            windowsSender,
            actualAuthentication,
            actualGmailSender,
            timeProvider,
            NullLogger<RateLimitNotificationProcessor>.Instance);
        return new TestContext(processor, windowsSender, actualGmailSender);
    }

    /// <summary>認証済み状態を返すサービスを生成します。</summary>
    private static StubGmailAuthenticationService CreateAuthenticatedService()
    {
        return new StubGmailAuthenticationService
        {
            Status = new GmailAuthenticationStatus
            {
                State = GmailAuthenticationState.Authenticated,
                HasClientConfiguration = true,
                AuthenticatedEmailAddress = "sender@example.com",
            },
        };
    }

    /// <summary>本番Gmail配送を有効にした設定を生成します。</summary>
    private static AppSettings CreateGmailSettings(bool windowsEnabled)
    {
        return AppSettings.CreateDefault() with
        {
            WindowsNotificationEnabled = windowsEnabled,
            GmailNotificationEnabled = true,
            GmailRecipient = "recipient@example.com",
            QuietHoursEnabled = false,
        };
    }

    /// <summary>回復済みの短期枠を生成します。</summary>
    private static RateLimitWindow CreateFiveHourWindow(string limitId)
    {
        return new RateLimitWindow
        {
            LimitId = limitId,
            Position = RateLimitPosition.Primary,
            Classification = RateLimitClassification.FiveHour,
            WindowDurationMinutes = 300,
            UsedPercent = 1,
            RemainingPercent = 99,
            ResetsAtUtc = NowUtc.AddHours(5),
        };
    }

    /// <summary>Standard条件を満たす週間枠を生成します。</summary>
    private static RateLimitWindow CreateWeeklyWindow(string limitId, DateTimeOffset resetsAtUtc)
    {
        return new RateLimitWindow
        {
            LimitId = limitId,
            Position = RateLimitPosition.Secondary,
            Classification = RateLimitClassification.Weekly,
            WindowDurationMinutes = 10080,
            UsedPercent = 58,
            RemainingPercent = 42,
            ResetsAtUtc = resetsAtUtc,
        };
    }

    /// <summary>指定利用枠を含む現在スナップショットを生成します。</summary>
    private static UsageSnapshot CreateSnapshot(IReadOnlyList<RateLimitWindow> windows)
    {
        return new UsageSnapshot { CapturedAtUtc = NowUtc, RateLimits = windows };
    }

    /// <summary>チャネル別状態を持つ既存通知を含む状態を生成します。</summary>
    private static ApplicationState CreateStateWithNotification(
        RateLimitWindow window,
        DeliveryStatus windowsStatus,
        DeliveryStatus gmailStatus,
        DateTimeOffset conditionMetAtUtc)
    {
        return new ApplicationState
        {
            InitialSetupCompleted = true,
            GmailProductionDeliveryStartedAtUtc = NowUtc.AddHours(-1),
            RateLimitNotificationStates =
            [
                new RateLimitNotificationState
                {
                    LimitId = window.LimitId ?? string.Empty,
                    Position = window.Position,
                    WindowDurationMinutes = window.WindowDurationMinutes ?? 0,
                    RecoveryWindowId = RateLimitNotificationPolicy.CreateRecoveryWindowId(window, conditionMetAtUtc),
                    NotificationType = RateLimitNotificationType.ShortWindowRecovered,
                    NotificationStage = RateLimitNotificationStage.Recovered,
                    ConditionMetAtUtc = conditionMetAtUtc,
                    WindowsDeliveryStatus = windowsStatus,
                    GmailDeliveryStatus = gmailStatus,
                },
            ],
        };
    }

    /// <summary>テストで共有するプロセッサーと記録用送信先を保持します。</summary>
    private sealed record TestContext(
        RateLimitNotificationProcessor Processor,
        RecordingWindowsNotificationSender WindowsSender,
        StubGmailNotificationSender GmailSender);

    /// <summary>状態をメモリ上へ永続化します。</summary>
    private sealed class InMemoryStateRepository : IApplicationStateRepository
    {
        private ApplicationState state;

        /// <summary>初期状態を受け取ります。</summary>
        public InMemoryStateRepository(ApplicationState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            this.state = state;
        }

        /// <inheritdoc />
        public Task<ApplicationState> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(state);
        }

        /// <inheritdoc />
        public Task SaveAsync(ApplicationState state, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(state);
            cancellationToken.ThrowIfCancellationRequested();
            this.state = state;
            return Task.CompletedTask;
        }
    }

    /// <summary>Windows通知の送信回数を記録します。</summary>
    private sealed class RecordingWindowsNotificationSender : IWindowsNotificationSender
    {
        /// <summary>送信回数を取得します。</summary>
        public int SendCount { get; private set; }

        /// <inheritdoc />
        public Task SendAsync(WindowsNotificationMessage message, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(message);
            cancellationToken.ThrowIfCancellationRequested();
            SendCount++;
            return Task.CompletedTask;
        }
    }

    /// <summary>テストから現在時刻を指定できる時刻提供元です。</summary>
    private sealed class MutableTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset utcNow;

        /// <summary>固定UTC時刻を受け取ります。</summary>
        public MutableTimeProvider(DateTimeOffset utcNow) => this.utcNow = utcNow;

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => utcNow;

        /// <inheritdoc />
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
