using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Application.Notifications;
using CodexUsageNotifier.Application.State;
using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Domain.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageNotifier.Tests.Application.Notifications;

/// <summary>
/// Windows通知の保留、送信、および永続化による重複防止を検証します。
/// </summary>
[TestClass]
public sealed class RateLimitNotificationProcessorTests
{
    /// <summary>
    /// 禁止時間中は通知を送らず07:00まで保留することを検証します。
    /// </summary>
    [TestMethod]
    public async Task ProcessAsync_DuringQuietHours_DefersNotification()
    {
        DateTimeOffset nowUtc = new(2026, 8, 5, 1, 0, 0, TimeSpan.Zero);
        InMemoryStateRepository repository = new();
        using ApplicationStateStore stateStore = new(repository);
        RecordingWindowsNotificationSender sender = new();
        MutableTimeProvider timeProvider = new(nowUtc);
        RateLimitNotificationProcessor processor = CreateProcessor(stateStore, sender, timeProvider);
        RateLimitWindow window = CreateFiveHourWindow(nowUtc);
        UsageSnapshot snapshot = CreateSnapshot(window, nowUtc);

        NotificationProcessingResult result = await processor.ProcessAsync(
            snapshot,
            AppSettings.CreateDefault(),
            CancellationToken.None);

        Assert.AreEqual(0, sender.SendCount);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 5, 7, 0, 0, TimeSpan.Zero), result.DeferredUntilUtc);
        RateLimitNotificationState state = result.State.RateLimitNotificationStates.Single();
        Assert.AreEqual(DeliveryStatus.NotAttempted, state.WindowsDeliveryStatus);
        Assert.AreEqual(result.DeferredUntilUtc, state.DeferredUntilUtc);
    }

    /// <summary>
    /// 禁止時間終了後に保留通知を1回送信し、後続取得では重複しないことを検証します。
    /// </summary>
    [TestMethod]
    public async Task ProcessAsync_AfterQuietHours_SendsDeferredNotificationOnce()
    {
        DateTimeOffset quietUtc = new(2026, 8, 5, 1, 0, 0, TimeSpan.Zero);
        InMemoryStateRepository repository = new();
        using ApplicationStateStore stateStore = new(repository);
        RecordingWindowsNotificationSender sender = new();
        MutableTimeProvider timeProvider = new(quietUtc);
        RateLimitNotificationProcessor processor = CreateProcessor(stateStore, sender, timeProvider);
        RateLimitWindow window = CreateFiveHourWindow(quietUtc);
        await processor.ProcessAsync(
            CreateSnapshot(window, quietUtc),
            AppSettings.CreateDefault(),
            CancellationToken.None);

        DateTimeOffset afterQuietUtc = new(2026, 8, 5, 8, 0, 0, TimeSpan.Zero);
        timeProvider.SetUtcNow(afterQuietUtc);
        RateLimitWindow currentWindow = CreateFiveHourWindow(afterQuietUtc);
        NotificationProcessingResult sent = await processor.ProcessAsync(
            CreateSnapshot(currentWindow, afterQuietUtc),
            AppSettings.CreateDefault(),
            CancellationToken.None);
        await processor.ProcessAsync(
            CreateSnapshot(currentWindow, afterQuietUtc.AddMinutes(1)),
            AppSettings.CreateDefault(),
            CancellationToken.None);

        Assert.AreEqual(1, sender.SendCount);
        Assert.AreEqual(DeliveryStatus.Succeeded, sent.State.RateLimitNotificationStates.Single().WindowsDeliveryStatus);
    }

    /// <summary>
    /// 同一取得で複数候補が成立してもWindows通知を1件だけ送り、各候補を成功として保存することを検証します。
    /// </summary>
    [TestMethod]
    public async Task ProcessAsync_MultipleCandidates_SendsSingleAggregateNotification()
    {
        DateTimeOffset nowUtc = new(2026, 8, 5, 8, 0, 0, TimeSpan.Zero);
        InMemoryStateRepository repository = new();
        using ApplicationStateStore stateStore = new(repository);
        RecordingWindowsNotificationSender sender = new();
        RateLimitNotificationProcessor processor = CreateProcessor(
            stateStore,
            sender,
            new MutableTimeProvider(nowUtc));
        UsageSnapshot snapshot = new()
        {
            CapturedAtUtc = nowUtc,
            RateLimits =
            [
                CreateFiveHourWindow(nowUtc),
                new RateLimitWindow
                {
                    LimitId = "codex",
                    Position = RateLimitPosition.Secondary,
                    Classification = RateLimitClassification.Weekly,
                    WindowDurationMinutes = 10080,
                    UsedPercent = 35,
                    RemainingPercent = 65,
                    ResetsAtUtc = nowUtc.AddHours(23),
                },
            ],
        };

        NotificationProcessingResult result = await processor.ProcessAsync(
            snapshot,
            AppSettings.CreateDefault(),
            CancellationToken.None);

        Assert.AreEqual(1, sender.SendCount);
        Assert.AreEqual("Codex利用枠のお知らせ（2件）", sender.Messages.Single().Title);
        Assert.AreEqual(2, result.State.RateLimitNotificationStates.Count);
        Assert.IsTrue(result.State.RateLimitNotificationStates.All(
            state => state.WindowsDeliveryStatus == DeliveryStatus.Succeeded));
    }

    /// <summary>
    /// Windows通知が1回失敗した場合に指定時刻以降で再送し、成功後は重複しないことを検証します。
    /// </summary>
    [TestMethod]
    public async Task ProcessAsync_FirstSendFails_RetriesThenStopsAfterSuccess()
    {
        DateTimeOffset nowUtc = new(2026, 8, 5, 8, 0, 0, TimeSpan.Zero);
        InMemoryStateRepository repository = new();
        using ApplicationStateStore stateStore = new(repository);
        RecordingWindowsNotificationSender sender = new() { FailuresRemaining = 1 };
        MutableTimeProvider timeProvider = new(nowUtc);
        RateLimitNotificationProcessor processor = CreateProcessor(stateStore, sender, timeProvider);
        RateLimitWindow window = CreateFiveHourWindow(nowUtc);

        NotificationProcessingResult failed = await processor.ProcessAsync(
            CreateSnapshot(window, nowUtc),
            AppSettings.CreateDefault(),
            CancellationToken.None);
        timeProvider.SetUtcNow(nowUtc.AddMinutes(4));
        await processor.ProcessAsync(
            CreateSnapshot(window, nowUtc.AddMinutes(4)),
            AppSettings.CreateDefault(),
            CancellationToken.None);
        timeProvider.SetUtcNow(nowUtc.AddMinutes(5));
        NotificationProcessingResult succeeded = await processor.ProcessAsync(
            CreateSnapshot(window, nowUtc.AddMinutes(5)),
            AppSettings.CreateDefault(),
            CancellationToken.None);
        timeProvider.SetUtcNow(nowUtc.AddMinutes(6));
        await processor.ProcessAsync(
            CreateSnapshot(window, nowUtc.AddMinutes(6)),
            AppSettings.CreateDefault(),
            CancellationToken.None);

        RateLimitNotificationState failedState = failed.State.RateLimitNotificationStates.Single();
        RateLimitNotificationState succeededState = succeeded.State.RateLimitNotificationStates.Single();
        Assert.AreEqual(2, sender.SendCount);
        Assert.AreEqual(DeliveryStatus.Failed, failedState.WindowsDeliveryStatus);
        Assert.AreEqual(1, failedState.WindowsAttemptCount);
        Assert.AreEqual(nowUtc.AddMinutes(5), failedState.WindowsNextRetryAtUtc);
        Assert.AreEqual(DeliveryStatus.Succeeded, succeededState.WindowsDeliveryStatus);
        Assert.AreEqual(2, succeededState.WindowsAttemptCount);
        Assert.IsNull(succeededState.WindowsNextRetryAtUtc);
    }

    /// <summary>
    /// 強制終了で残った古い送信中状態を次の正常取得で回復し、再送することを検証します。
    /// </summary>
    [TestMethod]
    public async Task ProcessAsync_StaleInProgress_RetriesOnNextFetch()
    {
        DateTimeOffset nowUtc = new(2026, 8, 5, 8, 0, 0, TimeSpan.Zero);
        RateLimitWindow window = CreateFiveHourWindow(nowUtc);
        string recoveryWindowId = RateLimitNotificationPolicy.CreateRecoveryWindowId(window, nowUtc);
        InMemoryStateRepository repository = new();
        await repository.SaveAsync(
            new ApplicationState
            {
                InitialSetupCompleted = true,
                RateLimitNotificationStates =
                [
                    new RateLimitNotificationState
                    {
                        LimitId = "codex",
                        Position = RateLimitPosition.Primary,
                        WindowDurationMinutes = 300,
                        RecoveryWindowId = recoveryWindowId,
                        NotificationType = RateLimitNotificationType.ShortWindowRecovered,
                        NotificationStage = RateLimitNotificationStage.Recovered,
                        ConditionMetAtUtc = nowUtc.AddMinutes(-10),
                        WindowsDeliveryStatus = DeliveryStatus.InProgress,
                        WindowsAttemptCount = 1,
                        WindowsLastAttemptedAtUtc = nowUtc.AddMinutes(-10),
                    },
                ],
            },
            CancellationToken.None);
        using ApplicationStateStore stateStore = new(repository);
        RecordingWindowsNotificationSender sender = new();
        RateLimitNotificationProcessor processor = CreateProcessor(
            stateStore,
            sender,
            new MutableTimeProvider(nowUtc));

        NotificationProcessingResult result = await processor.ProcessAsync(
            CreateSnapshot(window, nowUtc),
            AppSettings.CreateDefault(),
            CancellationToken.None);

        RateLimitNotificationState state = result.State.RateLimitNotificationStates.Single();
        Assert.AreEqual(1, sender.SendCount);
        Assert.AreEqual(DeliveryStatus.Succeeded, state.WindowsDeliveryStatus);
        Assert.AreEqual(2, state.WindowsAttemptCount);
    }

    /// <summary>
    /// Windows通知が失敗し続けても最大3回で再試行を停止することを検証します。
    /// </summary>
    [TestMethod]
    public async Task ProcessAsync_RepeatedFailures_StopsAfterMaximumAttempts()
    {
        DateTimeOffset nowUtc = new(2026, 8, 5, 8, 0, 0, TimeSpan.Zero);
        InMemoryStateRepository repository = new();
        using ApplicationStateStore stateStore = new(repository);
        RecordingWindowsNotificationSender sender = new() { FailuresRemaining = 4 };
        MutableTimeProvider timeProvider = new(nowUtc);
        RateLimitNotificationProcessor processor = CreateProcessor(stateStore, sender, timeProvider);
        RateLimitWindow window = CreateFiveHourWindow(nowUtc);
        NotificationProcessingResult? result = null;

        for (int attempt = 0; attempt < 4; attempt++)
        {
            DateTimeOffset capturedAtUtc = nowUtc.AddMinutes(attempt * 5);
            timeProvider.SetUtcNow(capturedAtUtc);
            result = await processor.ProcessAsync(
                CreateSnapshot(window, capturedAtUtc),
                AppSettings.CreateDefault(),
                CancellationToken.None);
        }

        Assert.IsNotNull(result);
        RateLimitNotificationState state = result.State.RateLimitNotificationStates.Single();
        Assert.AreEqual(3, sender.SendCount);
        Assert.AreEqual(DeliveryStatus.Failed, state.WindowsDeliveryStatus);
        Assert.AreEqual(3, state.WindowsAttemptCount);
    }

    /// <summary>
    /// 監視失敗が3回へ達したときだけ障害通知を1回送ることを検証します。
    /// </summary>
    [TestMethod]
    public async Task NotifyMonitoringFailureAsync_ThirdFailure_SendsOnce()
    {
        DateTimeOffset nowUtc = new(2026, 8, 5, 8, 0, 0, TimeSpan.Zero);
        InMemoryStateRepository repository = new();
        using ApplicationStateStore stateStore = new(repository);
        RecordingWindowsNotificationSender sender = new();
        RateLimitNotificationProcessor processor = CreateProcessor(
            stateStore,
            sender,
            new MutableTimeProvider(nowUtc));

        ApplicationState beforeThreshold = await processor.NotifyMonitoringFailureAsync(
            new ApplicationState { InitialSetupCompleted = true, ConsecutiveFailures = 2 },
            AppSettings.CreateDefault(),
            CancellationToken.None);
        ApplicationState notified = await processor.NotifyMonitoringFailureAsync(
            beforeThreshold with { ConsecutiveFailures = 3 },
            AppSettings.CreateDefault(),
            CancellationToken.None);
        await processor.NotifyMonitoringFailureAsync(
            notified with { ConsecutiveFailures = 3 },
            AppSettings.CreateDefault(),
            CancellationToken.None);

        Assert.AreEqual(1, sender.SendCount);
        Assert.IsTrue(notified.FailureNotificationSent);
        Assert.AreEqual(DeliveryStatus.Succeeded, notified.WindowsDeliveryResult?.Status);
    }

    /// <summary>
    /// テスト対象の通知プロセッサーを生成します。
    /// </summary>
    /// <param name="stateStore">状態ストアです。</param>
    /// <param name="sender">記録用Windows通知送信先です。</param>
    /// <param name="timeProvider">テスト用時刻提供元です。</param>
    /// <returns>テスト対象の通知プロセッサーです。</returns>
    private static RateLimitNotificationProcessor CreateProcessor(
        ApplicationStateStore stateStore,
        RecordingWindowsNotificationSender sender,
        TimeProvider timeProvider)
    {
        return new RateLimitNotificationProcessor(
            stateStore,
            sender,
            timeProvider,
            NullLogger<RateLimitNotificationProcessor>.Instance);
    }

    /// <summary>
    /// 回復済みの5時間枠を生成します。
    /// </summary>
    /// <param name="capturedAtUtc">取得UTC時刻です。</param>
    /// <returns>回復済みの5時間枠です。</returns>
    private static RateLimitWindow CreateFiveHourWindow(DateTimeOffset capturedAtUtc)
    {
        return new RateLimitWindow
        {
            LimitId = "codex",
            Position = RateLimitPosition.Primary,
            Classification = RateLimitClassification.FiveHour,
            WindowDurationMinutes = 300,
            UsedPercent = 1,
            RemainingPercent = 99,
            ResetsAtUtc = new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero),
        };
    }

    /// <summary>
    /// 1つの利用枠を含むスナップショットを生成します。
    /// </summary>
    /// <param name="window">含める利用枠です。</param>
    /// <param name="capturedAtUtc">取得UTC時刻です。</param>
    /// <returns>テスト用スナップショットです。</returns>
    private static UsageSnapshot CreateSnapshot(RateLimitWindow window, DateTimeOffset capturedAtUtc)
    {
        return new UsageSnapshot
        {
            CapturedAtUtc = capturedAtUtc,
            RateLimits = [window],
        };
    }

    /// <summary>
    /// メモリ上だけで状態を保持するテスト用リポジトリです。
    /// </summary>
    private sealed class InMemoryStateRepository : IApplicationStateRepository
    {
        private ApplicationState state = new() { InitialSetupCompleted = true };

        /// <summary>
        /// 現在の状態を返します。
        /// </summary>
        /// <param name="cancellationToken">読み込みのキャンセル通知です。</param>
        /// <returns>現在の状態です。</returns>
        public Task<ApplicationState> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(state);
        }

        /// <summary>
        /// 状態をメモリへ保存します。
        /// </summary>
        /// <param name="state">保存する状態です。</param>
        /// <param name="cancellationToken">保存のキャンセル通知です。</param>
        public Task SaveAsync(ApplicationState state, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(state);
            cancellationToken.ThrowIfCancellationRequested();
            this.state = state;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Windows通知の送信回数を記録します。
    /// </summary>
    private sealed class RecordingWindowsNotificationSender : IWindowsNotificationSender
    {
        /// <summary>
        /// 送信時に発生させる残り失敗回数を取得または設定します。
        /// </summary>
        public int FailuresRemaining { get; set; }

        /// <summary>
        /// 送信されたWindows通知を取得します。
        /// </summary>
        public List<WindowsNotificationMessage> Messages { get; } = [];

        /// <summary>
        /// Windows通知の送信回数を取得します。
        /// </summary>
        public int SendCount { get; private set; }

        /// <summary>
        /// 通知内容を検証して送信回数を増やします。
        /// </summary>
        /// <param name="message">通知内容です。</param>
        /// <param name="cancellationToken">送信のキャンセル通知です。</param>
        /// <returns>完了済みの非同期処理です。</returns>
        public Task SendAsync(
            WindowsNotificationMessage message,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(message);
            cancellationToken.ThrowIfCancellationRequested();
            Messages.Add(message);
            SendCount++;
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("テスト用のWindows通知失敗です。");
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 現在時刻をテストから変更できる時刻提供元です。
    /// </summary>
    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow;

        /// <summary>
        /// 初期UTC時刻を受け取ります。
        /// </summary>
        /// <param name="utcNow">初期UTC時刻です。</param>
        public MutableTimeProvider(DateTimeOffset utcNow)
        {
            this.utcNow = utcNow;
        }

        /// <summary>
        /// 現在のUTC時刻を返します。
        /// </summary>
        /// <returns>設定済みUTC時刻です。</returns>
        public override DateTimeOffset GetUtcNow() => utcNow;

        /// <summary>
        /// テストで使用するUTCタイムゾーンを返します。
        /// </summary>
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        /// <summary>
        /// 現在のUTC時刻を変更します。
        /// </summary>
        /// <param name="value">新しいUTC時刻です。</param>
        public void SetUtcNow(DateTimeOffset value) => utcNow = value;
    }
}
