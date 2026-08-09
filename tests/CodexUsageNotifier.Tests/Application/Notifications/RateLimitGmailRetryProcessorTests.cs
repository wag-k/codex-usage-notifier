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
/// Phase 4C-2のGmail再試行、期限切れ、配送境界、およびWindowsとの独立性を検証します。
/// </summary>
[TestClass]
public sealed class RateLimitGmailRetryProcessorTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 8, 9, 9, 0, 0, TimeSpan.Zero);

    /// <summary>初回の一時失敗を60分後の再試行として保存することを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_InitialTransientFailure_SchedulesRetryAfterSixtyMinutes()
    {
        TestContext context = CreateContext();
        context.GmailSender.Exception = CreateTransientException();

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [CreateFiveHourWindow("codex", NowUtc.AddHours(5), 99)]),
            CreateSettings(windowsEnabled: false),
            CancellationToken.None);

        RateLimitNotificationState state = result.State.RateLimitNotificationStates.Single();
        Assert.AreEqual(1, state.GmailAttemptCount);
        Assert.AreEqual(NowUtc.AddMinutes(60), state.GmailNextRetryAtUtc);
        Assert.AreEqual(GmailDeliveryFailureKind.Transient, state.GmailFailureKind);
    }

    /// <summary>初回失敗から60分未満では再試行しないことを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_BeforeRetryDeadline_DoesNotRetry()
    {
        RateLimitWindow window = CreateFiveHourWindow("codex", NowUtc.AddHours(5), 99);
        TestContext context = CreateContext(CreateRetryState(window, NowUtc.AddMinutes(-30)));

        await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [window]),
            CreateSettings(windowsEnabled: false),
            CancellationToken.None);

        Assert.AreEqual(0, context.GmailSender.SendCallCount);
    }

    /// <summary>初回失敗から60分後の正常取得で1回だけ再試行することを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_AfterRetryDeadline_RetriesOnce()
    {
        RateLimitWindow window = CreateFiveHourWindow("codex", NowUtc.AddHours(5), 99);
        TestContext context = CreateContext(CreateRetryState(window, NowUtc.AddMinutes(-60)));

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [window]),
            CreateSettings(windowsEnabled: false),
            CancellationToken.None);

        Assert.AreEqual(1, context.GmailSender.SendCallCount);
        Assert.AreEqual(2, result.State.RateLimitNotificationStates.Single().GmailAttemptCount);
        Assert.AreEqual(DeliveryStatus.Succeeded, result.State.RateLimitNotificationStates.Single().GmailDeliveryStatus);
    }

    /// <summary>2回目も失敗した候補をそれ以上再試行しないことを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_SecondFailure_DoesNotScheduleAnotherRetry()
    {
        RateLimitWindow window = CreateFiveHourWindow("codex", NowUtc.AddHours(5), 99);
        TestContext context = CreateContext(CreateRetryState(window, NowUtc.AddMinutes(-60)));
        context.GmailSender.Exception = CreateTransientException();

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [window]),
            CreateSettings(windowsEnabled: false),
            CancellationToken.None);

        RateLimitNotificationState failed = result.State.RateLimitNotificationStates.Single();
        Assert.AreEqual(2, failed.GmailAttemptCount);
        Assert.AreEqual(DeliveryStatus.Failed, failed.GmailDeliveryStatus);
        Assert.IsNull(failed.GmailNextRetryAtUtc);
    }

    /// <summary>タイムアウトを60分後に再試行可能な一時障害として保存することを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_Timeout_SchedulesRetry()
    {
        TestContext context = CreateContext();
        context.GmailSender.Exception = new TimeoutException("timeout");

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [CreateFiveHourWindow("codex", NowUtc.AddHours(5), 99)]),
            CreateSettings(windowsEnabled: false),
            CancellationToken.None);

        Assert.AreEqual(GmailDeliveryFailureKind.Transient, result.State.RateLimitNotificationStates.Single().GmailFailureKind);
        Assert.AreEqual(NowUtc.AddHours(1), result.State.RateLimitNotificationStates.Single().GmailNextRetryAtUtc);
    }

    /// <summary>401相当の失敗を再試行せず再認証必要として保存することを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_Unauthorized_DoesNotRetryAndRequiresReauthentication()
    {
        TestContext context = CreateContext();
        context.GmailSender.Exception = new GmailApiOperationException(
            GmailApiErrorKind.Unauthorized,
            "再認証してください。",
            new InvalidOperationException());

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [CreateFiveHourWindow("codex", NowUtc.AddHours(5), 99)]),
            CreateSettings(windowsEnabled: false),
            CancellationToken.None);

        RateLimitNotificationState state = result.State.RateLimitNotificationStates.Single();
        Assert.AreEqual(GmailDeliveryFailureKind.Authentication, state.GmailFailureKind);
        Assert.IsNull(state.GmailNextRetryAtUtc);
        Assert.IsFalse(result.State.GmailAuthenticationWasUsable);
    }

    /// <summary>invalid_grant相当の認証例外を自動再試行しないことを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_InvalidGrant_DoesNotRetry()
    {
        TestContext context = CreateContext();
        context.GmailSender.Exception = new InvalidOperationException("invalid_grant");

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [CreateFiveHourWindow("codex", NowUtc.AddHours(5), 99)]),
            CreateSettings(windowsEnabled: false),
            CancellationToken.None);

        RateLimitNotificationState state = result.State.RateLimitNotificationStates.Single();
        Assert.AreEqual(GmailDeliveryFailureKind.Authentication, state.GmailFailureKind);
        Assert.IsNull(state.GmailNextRetryAtUtc);
    }

    /// <summary>恒久的な403を自動再試行しないことを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_PermanentForbidden_DoesNotRetry()
    {
        TestContext context = CreateContext();
        context.GmailSender.Exception = new GmailApiOperationException(
            GmailApiErrorKind.Forbidden,
            "Gmail APIが利用できません。",
            new InvalidOperationException());

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [CreateFiveHourWindow("codex", NowUtc.AddHours(5), 99)]),
            CreateSettings(windowsEnabled: false),
            CancellationToken.None);

        RateLimitNotificationState state = result.State.RateLimitNotificationStates.Single();
        Assert.AreEqual(GmailDeliveryFailureKind.Permanent, state.GmailFailureKind);
        Assert.IsNull(state.GmailNextRetryAtUtc);
    }

    /// <summary>同時に期限へ達した複数の再試行候補を1通へ集約することを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_MultipleRetries_AggregatesIntoOneMail()
    {
        RateLimitWindow first = CreateFiveHourWindow("codex", NowUtc.AddHours(5), 99);
        RateLimitWindow second = CreateWeeklyWindow("team", NowUtc.AddHours(23), 42);
        ApplicationState initial = CreateState(
            [CreateRetryNotification(first, NowUtc.AddMinutes(-60)), CreateRetryNotification(
                second,
                NowUtc.AddMinutes(-60),
                RateLimitNotificationType.LongWindowStandardWarning,
                RateLimitNotificationStage.Standard)]);
        TestContext context = CreateContext(initial);

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [first, second]),
            CreateSettings(windowsEnabled: false),
            CancellationToken.None);

        Assert.AreEqual(1, context.GmailSender.SendCallCount);
        Assert.AreEqual(2, result.State.RateLimitNotificationStates.Count);
        Assert.IsTrue(result.State.RateLimitNotificationStates.All(state => state.GmailAttemptCount == 2));
    }

    /// <summary>再試行候補と今回の新規候補を1通へ集約できることを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_RetryAndNewCandidate_AggregatesIntoOneMail()
    {
        RateLimitWindow retry = CreateFiveHourWindow("codex", NowUtc.AddHours(5), 99);
        RateLimitWindow fresh = CreateWeeklyWindow("team", NowUtc.AddHours(23), 42);
        TestContext context = CreateContext(CreateState([CreateRetryNotification(retry, NowUtc.AddMinutes(-60))]));

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [retry, fresh]),
            CreateSettings(windowsEnabled: false),
            CancellationToken.None);

        Assert.AreEqual(1, context.GmailSender.SendCallCount);
        Assert.AreEqual(2, result.State.RateLimitNotificationStates.Single(state => state.LimitId == "codex").GmailAttemptCount);
        Assert.AreEqual(1, result.State.RateLimitNotificationStates.Single(state => state.LimitId == "team").GmailAttemptCount);
    }

    /// <summary>Early失敗後にStandard時間帯へ進んだ場合はEarlyを期限切れにすることを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_EarlyMovedToStandard_ExpiresEarly()
    {
        RateLimitWindow window = CreateWeeklyWindow("codex", NowUtc.AddHours(20), 60);
        RateLimitNotificationState early = CreateRetryNotification(
            window,
            NowUtc.AddMinutes(-60),
            RateLimitNotificationType.LongWindowEarlyWarning,
            RateLimitNotificationStage.Early);
        TestContext context = CreateContext(CreateState([early]));

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [window]),
            CreateSettings(windowsEnabled: false),
            CancellationToken.None);

        Assert.AreEqual(DeliveryStatus.Expired, result.State.RateLimitNotificationStates.Single(
            state => state.NotificationStage == RateLimitNotificationStage.Early).GmailDeliveryStatus);
        Assert.IsTrue(context.GmailSender.Messages.Single().Body.Contains("通知段階: Standard", StringComparison.Ordinal));
    }

    /// <summary>Standard失敗後にFinal時間帯へ進んだ場合はStandardを期限切れにすることを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_StandardMovedToFinal_ExpiresStandard()
    {
        RateLimitWindow window = CreateWeeklyWindow("codex", NowUtc.AddHours(5), 60);
        RateLimitNotificationState standard = CreateRetryNotification(
            window,
            NowUtc.AddMinutes(-60),
            RateLimitNotificationType.LongWindowStandardWarning,
            RateLimitNotificationStage.Standard);
        TestContext context = CreateContext(CreateState([standard]));

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [window]),
            CreateSettings(windowsEnabled: false),
            CancellationToken.None);

        Assert.AreEqual(DeliveryStatus.Expired, result.State.RateLimitNotificationStates.Single(
            state => state.NotificationStage == RateLimitNotificationStage.Standard).GmailDeliveryStatus);
        Assert.IsTrue(context.GmailSender.Messages.Single().Body.Contains("通知段階: Final", StringComparison.Ordinal));
    }

    /// <summary>短期枠の残量が回復閾値未満なら再試行を期限切れにすることを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_ShortWindowBelowThreshold_ExpiresRetry()
    {
        RateLimitWindow window = CreateFiveHourWindow("codex", NowUtc.AddHours(5), 98);
        TestContext context = CreateContext(CreateRetryState(window, NowUtc.AddMinutes(-60)));

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [window]),
            CreateSettings(windowsEnabled: false),
            CancellationToken.None);

        Assert.AreEqual(0, context.GmailSender.SendCallCount);
        Assert.AreEqual(DeliveryStatus.Expired, result.State.RateLimitNotificationStates.Single().GmailDeliveryStatus);
    }

    /// <summary>リセット完了が同じ新期間を表す場合は1回再試行できることを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_ResetCompletedSamePeriod_Retries()
    {
        RateLimitWindow window = CreateWeeklyWindow("codex", NowUtc.AddDays(7), 95);
        RateLimitNotificationState retry = CreateRetryNotification(
            window,
            NowUtc.AddMinutes(-60),
            RateLimitNotificationType.LongWindowResetCompleted,
            RateLimitNotificationStage.Completed) with
        {
            ResetCompletionReason = RateLimitResetCompletionReason.ResetTimeAdvanced,
        };
        TestContext context = CreateContext(CreateState([retry]));

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [window]),
            CreateSettings(windowsEnabled: false),
            CancellationToken.None);

        Assert.AreEqual(1, context.GmailSender.SendCallCount);
        Assert.AreEqual(DeliveryStatus.Succeeded, result.State.RateLimitNotificationStates.Single().GmailDeliveryStatus);
    }

    /// <summary>通知禁止時間中はGmail再試行回数を増加させないことを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_RetryDuringQuietHours_DoesNotIncrementAttemptCount()
    {
        DateTimeOffset quietUtc = new(2026, 8, 9, 1, 0, 0, TimeSpan.Zero);
        RateLimitWindow window = CreateFiveHourWindow("codex", quietUtc.AddHours(5), 99);
        TestContext context = CreateContext(CreateRetryState(window, quietUtc.AddMinutes(-60)), quietUtc);

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot(quietUtc, [window]),
            CreateSettings(windowsEnabled: false) with { QuietHoursEnabled = true },
            CancellationToken.None);

        Assert.AreEqual(0, context.GmailSender.SendCallCount);
        Assert.AreEqual(1, result.State.RateLimitNotificationStates.Single().GmailAttemptCount);
    }

    /// <summary>通知禁止時間終了後も有効な再試行を送ることを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_RetryAfterQuietHours_SendsWhenStillValid()
    {
        RateLimitWindow window = CreateFiveHourWindow("codex", NowUtc.AddHours(5), 99);
        RateLimitNotificationState retry = CreateRetryNotification(window, NowUtc.AddHours(-2)) with
        {
            DeferredUntilUtc = NowUtc.AddHours(-1),
        };
        TestContext context = CreateContext(CreateState([retry]));

        await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [window]),
            CreateSettings(windowsEnabled: false) with { QuietHoursEnabled = true },
            CancellationToken.None);

        Assert.AreEqual(1, context.GmailSender.SendCallCount);
    }

    /// <summary>60分以上古いInProgressを2回目として回復し再試行することを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_StaleInProgress_RecoversAndRetries()
    {
        RateLimitWindow window = CreateFiveHourWindow("codex", NowUtc.AddHours(5), 99);
        RateLimitNotificationState interrupted = CreateRetryNotification(window, NowUtc.AddMinutes(-61)) with
        {
            GmailDeliveryStatus = DeliveryStatus.InProgress,
            GmailFailureKind = GmailDeliveryFailureKind.None,
            GmailNextRetryAtUtc = null,
        };
        TestContext context = CreateContext(CreateState([interrupted]));

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [window]),
            CreateSettings(windowsEnabled: false),
            CancellationToken.None);

        Assert.AreEqual(1, context.GmailSender.SendCallCount);
        Assert.AreEqual(2, result.State.RateLimitNotificationStates.Single().GmailAttemptCount);
    }

    /// <summary>60分未満のInProgressは送信結果待ちとして再送しないことを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_RecentInProgress_DoesNotRetry()
    {
        RateLimitWindow window = CreateFiveHourWindow("codex", NowUtc.AddHours(5), 99);
        RateLimitNotificationState interrupted = CreateRetryNotification(window, NowUtc.AddMinutes(-59)) with
        {
            GmailDeliveryStatus = DeliveryStatus.InProgress,
            GmailFailureKind = GmailDeliveryFailureKind.None,
            GmailNextRetryAtUtc = null,
        };
        TestContext context = CreateContext(CreateState([interrupted]));

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [window]),
            CreateSettings(windowsEnabled: false),
            CancellationToken.None);

        Assert.AreEqual(0, context.GmailSender.SendCallCount);
        Assert.AreEqual(DeliveryStatus.InProgress, result.State.RateLimitNotificationStates.Single().GmailDeliveryStatus);
        Assert.AreEqual(1, result.State.RateLimitNotificationStates.Single().GmailAttemptCount);
    }

    /// <summary>2回目のInProgress復旧では最大回数を超えて再送しないことを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_StaleSecondInProgress_DoesNotExceedMaximum()
    {
        RateLimitWindow window = CreateFiveHourWindow("codex", NowUtc.AddHours(5), 99);
        RateLimitNotificationState interrupted = CreateRetryNotification(window, NowUtc.AddMinutes(-61)) with
        {
            GmailDeliveryStatus = DeliveryStatus.InProgress,
            GmailAttemptCount = 2,
            GmailFailureKind = GmailDeliveryFailureKind.None,
            GmailNextRetryAtUtc = null,
        };
        TestContext context = CreateContext(CreateState([interrupted]));

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [window]),
            CreateSettings(windowsEnabled: false),
            CancellationToken.None);

        Assert.AreEqual(0, context.GmailSender.SendCallCount);
        Assert.AreEqual(2, result.State.RateLimitNotificationStates.Single().GmailAttemptCount);
        Assert.IsNull(result.State.RateLimitNotificationStates.Single().GmailNextRetryAtUtc);
    }

    /// <summary>Gmail無効中に成立した通知を再有効化後に送らないことを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_NotificationCreatedWhileDisabled_IsNotSentAfterEnable()
    {
        MutableTimeProvider timeProvider = new(NowUtc);
        TestContext context = CreateContext(nowUtc: NowUtc, timeProvider: timeProvider);
        RateLimitWindow window = CreateFiveHourWindow("codex", NowUtc.AddHours(5), 99);
        await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [window]),
            CreateSettings(windowsEnabled: true) with { GmailNotificationEnabled = false },
            CancellationToken.None);
        timeProvider.UtcNow = NowUtc.AddHours(1);

        await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc.AddHours(1), [window]),
            CreateSettings(windowsEnabled: true),
            CancellationToken.None);

        Assert.AreEqual(0, context.GmailSender.SendCallCount);
    }

    /// <summary>Gmail再有効化後に新しく成立した通知だけを送ることを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_NewNotificationAfterEnable_IsSent()
    {
        RateLimitWindow oldWindow = CreateFiveHourWindow("old", NowUtc.AddHours(5), 99);
        RateLimitNotificationState oldNotification = CreateNotAttemptedNotification(oldWindow, NowUtc.AddHours(-1));
        ApplicationState initial = CreateState([oldNotification]) with
        {
            GmailDeliveryEnabledLastObserved = false,
            GmailAuthenticationWasUsable = false,
        };
        RateLimitWindow newWindow = CreateFiveHourWindow("new", NowUtc.AddHours(5), 99);
        TestContext context = CreateContext(initial);

        await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [oldWindow, newWindow]),
            CreateSettings(windowsEnabled: false),
            CancellationToken.None);

        Assert.AreEqual(1, context.GmailSender.SendCallCount);
        StringAssert.Contains(context.GmailSender.Messages.Single().Body, "LimitId: new");
        Assert.IsFalse(context.GmailSender.Messages.Single().Body.Contains("LimitId: old", StringComparison.Ordinal));
    }

    /// <summary>Gmail再試行が成功済みWindows状態を変更しないことを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_GmailRetry_DoesNotChangeWindowsState()
    {
        RateLimitWindow window = CreateFiveHourWindow("codex", NowUtc.AddHours(5), 99);
        RateLimitNotificationState retry = CreateRetryNotification(window, NowUtc.AddMinutes(-60)) with
        {
            WindowsDeliveryStatus = DeliveryStatus.Succeeded,
            WindowsAttemptCount = 1,
        };
        TestContext context = CreateContext(CreateState([retry]));

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [window]),
            CreateSettings(windowsEnabled: true),
            CancellationToken.None);

        Assert.AreEqual(0, context.WindowsSender.SendCount);
        Assert.AreEqual(DeliveryStatus.Succeeded, result.State.RateLimitNotificationStates.Single().WindowsDeliveryStatus);
        Assert.AreEqual(1, result.State.RateLimitNotificationStates.Single().WindowsAttemptCount);
    }

    /// <summary>Windows再試行が成功済みGmail状態を変更または再送しないことを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_WindowsRetry_DoesNotChangeGmailState()
    {
        RateLimitWindow window = CreateFiveHourWindow("codex", NowUtc.AddHours(5), 99);
        RateLimitNotificationState notification = CreateNotAttemptedNotification(window, NowUtc.AddHours(-1)) with
        {
            WindowsDeliveryStatus = DeliveryStatus.Failed,
            WindowsAttemptCount = 1,
            WindowsLastAttemptedAtUtc = NowUtc.AddMinutes(-10),
            WindowsNextRetryAtUtc = NowUtc,
            GmailDeliveryStatus = DeliveryStatus.Succeeded,
            GmailAttemptCount = 1,
        };
        TestContext context = CreateContext(CreateState([notification]));

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [window]),
            CreateSettings(windowsEnabled: true),
            CancellationToken.None);

        Assert.AreEqual(1, context.WindowsSender.SendCount);
        Assert.AreEqual(0, context.GmailSender.SendCallCount);
        Assert.AreEqual(DeliveryStatus.Succeeded, result.State.RateLimitNotificationStates.Single().GmailDeliveryStatus);
    }

    /// <summary>Windows失敗時でもGmail成功を独立して保存することを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_WindowsFailsGmailSucceeds_StoresIndependentResults()
    {
        TestContext context = CreateContext();
        context.WindowsSender.Exception = new InvalidOperationException("Windows failure");

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [CreateFiveHourWindow("codex", NowUtc.AddHours(5), 99)]),
            CreateSettings(windowsEnabled: true),
            CancellationToken.None);

        RateLimitNotificationState state = result.State.RateLimitNotificationStates.Single();
        Assert.AreEqual(DeliveryStatus.Failed, state.WindowsDeliveryStatus);
        Assert.AreEqual(DeliveryStatus.Succeeded, state.GmailDeliveryStatus);
    }

    /// <summary>Gmail失敗時でもWindows成功を独立して保存することを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_WindowsSucceedsGmailFails_StoresIndependentResults()
    {
        TestContext context = CreateContext();
        context.GmailSender.Exception = CreateTransientException();

        NotificationProcessingResult result = await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [CreateFiveHourWindow("codex", NowUtc.AddHours(5), 99)]),
            CreateSettings(windowsEnabled: true),
            CancellationToken.None);

        RateLimitNotificationState state = result.State.RateLimitNotificationStates.Single();
        Assert.AreEqual(DeliveryStatus.Succeeded, state.WindowsDeliveryStatus);
        Assert.AreEqual(DeliveryStatus.Failed, state.GmailDeliveryStatus);
    }

    /// <summary>成功済みGmail候補を次の正常取得で再送しないことを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_SucceededCandidate_IsNotSentAgain()
    {
        RateLimitWindow window = CreateFiveHourWindow("codex", NowUtc.AddHours(5), 99);
        RateLimitNotificationState succeeded = CreateNotAttemptedNotification(window, NowUtc.AddHours(-1)) with
        {
            WindowsDeliveryStatus = DeliveryStatus.Succeeded,
            GmailDeliveryStatus = DeliveryStatus.Succeeded,
            GmailAttemptCount = 1,
        };
        TestContext context = CreateContext(CreateState([succeeded]));

        await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [window]),
            CreateSettings(windowsEnabled: true),
            CancellationToken.None);

        Assert.AreEqual(0, context.GmailSender.SendCallCount);
        Assert.AreEqual(0, context.WindowsSender.SendCount);
    }

    /// <summary>再認証完了時の新境界より前の認証失効通知を送らないことを検証します。</summary>
    [TestMethod]
    public async Task ProcessAsync_AfterReauthentication_DoesNotSendOldAuthenticationFailure()
    {
        RateLimitWindow oldWindow = CreateFiveHourWindow("old", NowUtc.AddHours(5), 99);
        RateLimitNotificationState oldFailure = CreateRetryNotification(oldWindow, NowUtc.AddHours(-2)) with
        {
            GmailFailureKind = GmailDeliveryFailureKind.Authentication,
            GmailNextRetryAtUtc = null,
        };
        ApplicationState initial = CreateState([oldFailure]) with { GmailAuthenticationWasUsable = false };
        TestContext context = CreateContext(initial);

        await context.Processor.ProcessAsync(
            CreateSnapshot(NowUtc, [oldWindow]),
            CreateSettings(windowsEnabled: false),
            CancellationToken.None);

        Assert.AreEqual(0, context.GmailSender.SendCallCount);
        Assert.AreEqual(NowUtc, (await context.StateStore.LoadAsync(CancellationToken.None)).GmailDeliveryEnabledSinceUtc);
    }

    /// <summary>共通するテスト用プロセッサーと依存先を生成します。</summary>
    private static TestContext CreateContext(
        ApplicationState? initialState = null,
        DateTimeOffset? nowUtc = null,
        MutableTimeProvider? timeProvider = null)
    {
        ApplicationStateStore stateStore = new(new InMemoryStateRepository(
            initialState ?? new ApplicationState { InitialSetupCompleted = true }));
        RecordingWindowsNotificationSender windowsSender = new();
        StubGmailNotificationSender gmailSender = new();
        MutableTimeProvider actualTimeProvider = timeProvider ?? new MutableTimeProvider(nowUtc ?? NowUtc);
        StubGmailAuthenticationService authentication = new()
        {
            Status = new GmailAuthenticationStatus
            {
                State = GmailAuthenticationState.Authenticated,
                HasClientConfiguration = true,
                AuthenticatedEmailAddress = "sender@example.com",
            },
        };
        RateLimitNotificationProcessor processor = new(
            stateStore,
            windowsSender,
            authentication,
            gmailSender,
            actualTimeProvider,
            NullLogger<RateLimitNotificationProcessor>.Instance);
        return new TestContext(processor, stateStore, windowsSender, gmailSender);
    }

    /// <summary>Gmailと任意のWindows設定を有効にした設定を生成します。</summary>
    private static AppSettings CreateSettings(bool windowsEnabled)
    {
        return AppSettings.CreateDefault() with
        {
            WindowsNotificationEnabled = windowsEnabled,
            GmailNotificationEnabled = true,
            GmailRecipient = "recipient@example.com",
            QuietHoursEnabled = false,
        };
    }

    /// <summary>指定取得時刻と利用枠を持つスナップショットを生成します。</summary>
    private static UsageSnapshot CreateSnapshot(
        DateTimeOffset capturedAtUtc,
        IReadOnlyList<RateLimitWindow> windows)
    {
        return new UsageSnapshot { CapturedAtUtc = capturedAtUtc, RateLimits = windows };
    }

    /// <summary>指定残量を持つ5時間枠を生成します。</summary>
    private static RateLimitWindow CreateFiveHourWindow(
        string limitId,
        DateTimeOffset resetsAtUtc,
        double remainingPercent)
    {
        return new RateLimitWindow
        {
            LimitId = limitId,
            Position = RateLimitPosition.Primary,
            Classification = RateLimitClassification.FiveHour,
            WindowDurationMinutes = 300,
            UsedPercent = 100 - remainingPercent,
            RemainingPercent = remainingPercent,
            ResetsAtUtc = resetsAtUtc,
        };
    }

    /// <summary>指定残量とリセット時刻を持つ週間枠を生成します。</summary>
    private static RateLimitWindow CreateWeeklyWindow(
        string limitId,
        DateTimeOffset resetsAtUtc,
        double remainingPercent)
    {
        return new RateLimitWindow
        {
            LimitId = limitId,
            Position = RateLimitPosition.Secondary,
            Classification = RateLimitClassification.Weekly,
            WindowDurationMinutes = 10080,
            UsedPercent = 100 - remainingPercent,
            RemainingPercent = remainingPercent,
            ResetsAtUtc = resetsAtUtc,
        };
    }

    /// <summary>1件の一時失敗通知を持つアプリケーション状態を生成します。</summary>
    private static ApplicationState CreateRetryState(RateLimitWindow window, DateTimeOffset lastAttemptedAtUtc)
    {
        return CreateState([CreateRetryNotification(window, lastAttemptedAtUtc)]);
    }

    /// <summary>指定通知一覧と有効なGmail配送境界を持つ状態を生成します。</summary>
    private static ApplicationState CreateState(IReadOnlyList<RateLimitNotificationState> notifications)
    {
        return new ApplicationState
        {
            InitialSetupCompleted = true,
            GmailProductionDeliveryStartedAtUtc = NowUtc.AddDays(-1),
            GmailDeliveryEnabledSinceUtc = NowUtc.AddDays(-1),
            GmailDeliveryEnabledLastObserved = true,
            GmailAuthenticationWasUsable = true,
            RateLimitNotificationStates = notifications,
        };
    }

    /// <summary>60分後に再試行できる一時失敗通知を生成します。</summary>
    private static RateLimitNotificationState CreateRetryNotification(
        RateLimitWindow window,
        DateTimeOffset lastAttemptedAtUtc,
        RateLimitNotificationType notificationType = RateLimitNotificationType.ShortWindowRecovered,
        RateLimitNotificationStage notificationStage = RateLimitNotificationStage.Recovered)
    {
        return CreateNotAttemptedNotification(window, lastAttemptedAtUtc) with
        {
            NotificationType = notificationType,
            NotificationStage = notificationStage,
            GmailDeliveryStatus = DeliveryStatus.Failed,
            GmailAttemptCount = 1,
            GmailLastAttemptedAtUtc = lastAttemptedAtUtc,
            GmailNextRetryAtUtc = lastAttemptedAtUtc.AddMinutes(60),
            GmailFailureKind = GmailDeliveryFailureKind.Transient,
        };
    }

    /// <summary>指定利用枠の未送信通知状態を生成します。</summary>
    private static RateLimitNotificationState CreateNotAttemptedNotification(
        RateLimitWindow window,
        DateTimeOffset conditionMetAtUtc)
    {
        return new RateLimitNotificationState
        {
            LimitId = window.LimitId ?? string.Empty,
            Position = window.Position,
            WindowDurationMinutes = window.WindowDurationMinutes ?? 0,
            RecoveryWindowId = RateLimitNotificationPolicy.CreateRecoveryWindowId(window, conditionMetAtUtc),
            NotificationType = RateLimitNotificationType.ShortWindowRecovered,
            NotificationStage = RateLimitNotificationStage.Recovered,
            ConditionMetAtUtc = conditionMetAtUtc,
            WindowsDeliveryStatus = DeliveryStatus.Succeeded,
            GmailDeliveryStatus = DeliveryStatus.NotAttempted,
        };
    }

    /// <summary>再試行可能なGmail API一時例外を生成します。</summary>
    private static GmailApiOperationException CreateTransientException()
    {
        return new GmailApiOperationException(
            GmailApiErrorKind.Transient,
            "一時的な通信障害です。",
            new HttpRequestException());
    }

    /// <summary>テストで共有する処理対象と記録先を保持します。</summary>
    private sealed record TestContext(
        RateLimitNotificationProcessor Processor,
        ApplicationStateStore StateStore,
        RecordingWindowsNotificationSender WindowsSender,
        StubGmailNotificationSender GmailSender);

    /// <summary>メモリ上へ状態を永続化します。</summary>
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

    /// <summary>Windows通知の送信と失敗を記録します。</summary>
    private sealed class RecordingWindowsNotificationSender : IWindowsNotificationSender
    {
        /// <summary>送信回数を取得します。</summary>
        public int SendCount { get; private set; }

        /// <summary>送信時に発生させる例外を取得または設定します。</summary>
        public Exception? Exception { get; set; }

        /// <inheritdoc />
        public Task SendAsync(WindowsNotificationMessage message, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(message);
            cancellationToken.ThrowIfCancellationRequested();
            SendCount++;
            return Exception is null ? Task.CompletedTask : Task.FromException(Exception);
        }
    }

    /// <summary>テストからUTC時刻を変更できる時刻提供元です。</summary>
    private sealed class MutableTimeProvider : TimeProvider
    {
        /// <summary>初期UTC時刻を受け取ります。</summary>
        public MutableTimeProvider(DateTimeOffset utcNow) => UtcNow = utcNow;

        /// <summary>現在として返すUTC時刻を取得または設定します。</summary>
        public DateTimeOffset UtcNow { get; set; }

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => UtcNow;

        /// <inheritdoc />
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
