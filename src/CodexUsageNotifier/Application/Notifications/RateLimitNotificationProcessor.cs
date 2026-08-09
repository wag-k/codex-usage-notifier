using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Application.Gmail;
using CodexUsageNotifier.Application.State;
using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Domain.Services;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier.Application.Notifications;

/// <summary>
/// 通知候補の判定、禁止時間中の保留、Windows通知、および通知状態保存を調整します。
/// </summary>
public sealed partial class RateLimitNotificationProcessor
{
    private static readonly TimeSpan WindowsRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WindowsInProgressTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan GmailRetryDelay = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan GmailInProgressTimeout = TimeSpan.FromMinutes(60);
    private readonly ApplicationStateStore stateStore;
    private readonly IWindowsNotificationSender windowsNotificationSender;
    private readonly IGmailAuthenticationStatusProvider gmailAuthenticationStatusProvider;
    private readonly IGmailNotificationSender gmailNotificationSender;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<RateLimitNotificationProcessor> logger;

    /// <summary>
    /// 状態保存、チャネル別通知、Gmail認証状態、時刻、およびロガーを受け取ります。
    /// </summary>
    /// <param name="stateStore">通知状態を保存するストアです。</param>
    /// <param name="windowsNotificationSender">Windows通知の送信先です。</param>
    /// <param name="gmailAuthenticationStatusProvider">Gmailの安全な認証状態の取得元です。</param>
    /// <param name="gmailNotificationSender">Gmail本番通知の送信先です。</param>
    /// <param name="timeProvider">通知禁止時間のタイムゾーンを提供します。</param>
    /// <param name="logger">通知判定と送信結果の記録先です。</param>
    public RateLimitNotificationProcessor(
        ApplicationStateStore stateStore,
        IWindowsNotificationSender windowsNotificationSender,
        IGmailAuthenticationStatusProvider gmailAuthenticationStatusProvider,
        IGmailNotificationSender gmailNotificationSender,
        TimeProvider timeProvider,
        ILogger<RateLimitNotificationProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(windowsNotificationSender);
        ArgumentNullException.ThrowIfNull(gmailAuthenticationStatusProvider);
        ArgumentNullException.ThrowIfNull(gmailNotificationSender);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        this.stateStore = stateStore;
        this.windowsNotificationSender = windowsNotificationSender;
        this.gmailAuthenticationStatusProvider = gmailAuthenticationStatusProvider;
        this.gmailNotificationSender = gmailNotificationSender;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>
    /// 最新スナップショットを保存し、全利用枠の通知候補を保留またはWindowsへ送信します。
    /// </summary>
    /// <param name="snapshot">正常取得した最新スナップショットです。</param>
    /// <param name="settings">通知と禁止時間の設定です。</param>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    /// <returns>保存済み状態と保留終了時刻です。</returns>
    public async Task<NotificationProcessingResult> ProcessAsync(
        UsageSnapshot snapshot,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        ApplicationState previousState = await stateStore.LoadAsync(cancellationToken);
        previousState = await EnsureGmailProductionDeliveryStartAsync(previousState, cancellationToken);
        (previousState, GmailAuthenticationStatus? gmailAuthenticationStatus) =
            await SynchronizeGmailDeliveryBoundaryAsync(
                previousState,
                settings,
                cancellationToken);
        previousState = await RecoverInterruptedWindowsAttemptsAsync(
            previousState,
            snapshot.CapturedAtUtc,
            cancellationToken);
        previousState = await RecoverInterruptedGmailAttemptsAsync(
            previousState,
            snapshot.CapturedAtUtc,
            cancellationToken);
        previousState = await ExpireInvalidDeferredNotificationsAsync(
            previousState,
            snapshot,
            settings,
            cancellationToken);
        previousState = await ExpireInvalidGmailRetriesAsync(
            previousState,
            snapshot,
            settings,
            cancellationToken);
        RateLimitNotificationEvaluation evaluation = RateLimitNotificationPolicy.Evaluate(
            snapshot,
            previousState.LastUsageSnapshot,
            settings,
            previousState.RateLimitNotificationStates,
            previousState.RateLimitRecoveryStates);
        ApplicationState currentState = await stateStore.UpdateAsync(
            state => state with
            {
                LastSuccessfulFetchAtUtc = snapshot.CapturedAtUtc,
                LastUsageSnapshot = snapshot,
                RateLimitRecoveryStates = evaluation.RecoveryStates,
                ConsecutiveFailures = 0,
                FailureNotificationSent = false,
            },
            cancellationToken);

        if (!previousState.InitialSetupCompleted
            || evaluation.Candidates.Count == 0)
        {
            return new NotificationProcessingResult { State = currentState };
        }

        DateTimeOffset nowUtc = snapshot.CapturedAtUtc;
        DateTimeOffset? quietHoursEnd = QuietHoursSchedule.GetQuietHoursEndUtc(
            nowUtc,
            timeProvider.LocalTimeZone,
            settings);
        if (quietHoursEnd is not null)
        {
            foreach (RateLimitNotificationCandidate candidate in evaluation.Candidates)
            {
                LogResetCompletionReason(candidate);
                RateLimitNotificationState? existing = FindNotificationState(
                    currentState.RateLimitNotificationStates,
                    candidate);
                RateLimitNotificationState deferred = CreateState(
                    candidate,
                    existing?.WindowsDeliveryStatus ?? DeliveryStatus.NotAttempted,
                    deliveredAtUtc: existing?.DeliveredAtUtc,
                    deferredUntilUtc: quietHoursEnd) with
                {
                    WindowsAttemptCount = existing?.WindowsAttemptCount ?? 0,
                    WindowsLastAttemptedAtUtc = existing?.WindowsLastAttemptedAtUtc,
                    WindowsNextRetryAtUtc = existing?.WindowsNextRetryAtUtc,
                    GmailDeliveryStatus = existing?.GmailDeliveryStatus ?? DeliveryStatus.NotAttempted,
                    GmailAttemptCount = existing?.GmailAttemptCount ?? 0,
                    GmailLastAttemptedAtUtc = existing?.GmailLastAttemptedAtUtc,
                    GmailNextRetryAtUtc = existing?.GmailNextRetryAtUtc,
                    GmailFailureKind = existing?.GmailFailureKind ?? GmailDeliveryFailureKind.None,
                };
                currentState = await SaveNotificationStateAsync(deferred, cancellationToken);
                LogNotificationDeferred(
                    logger,
                    candidate.NotificationType,
                    candidate.NotificationStage,
                    quietHoursEnd.Value);
                if (existing is not null
                    && existing.GmailDeliveryStatus == DeliveryStatus.Failed)
                {
                    LogGmailRetryDeferredByQuietHours(
                        logger,
                        candidate.NotificationType,
                        candidate.NotificationStage,
                        quietHoursEnd.Value);
                }
            }

            return new NotificationProcessingResult
            {
                State = currentState,
                DeferredUntilUtc = quietHoursEnd,
            };
        }

        foreach (RateLimitNotificationCandidate candidate in evaluation.Candidates)
        {
            LogResetCompletionReason(candidate);
            RateLimitNotificationState? existing = FindNotificationState(
                currentState.RateLimitNotificationStates,
                candidate);
            if (existing is null)
            {
                existing = CreateState(
                    candidate,
                    DeliveryStatus.NotAttempted,
                    deliveredAtUtc: null,
                    deferredUntilUtc: null);
                currentState = await SaveNotificationStateAsync(existing, cancellationToken);
            }
        }

        currentState = await DeliverWindowsAsync(
            evaluation.Candidates,
            currentState,
            snapshot,
            settings,
            cancellationToken);
        currentState = await DeliverGmailAsync(
            evaluation.Candidates,
            currentState,
            snapshot,
            settings,
            gmailAuthenticationStatus,
            cancellationToken);

        return new NotificationProcessingResult { State = currentState };
    }

    /// <summary>
    /// Phase 4Cの本番Gmail配送開始時刻を初回だけ永続化します。
    /// </summary>
    /// <param name="state">現在の永続状態です。</param>
    /// <param name="cancellationToken">保存のキャンセル通知です。</param>
    /// <returns>配送開始時刻を保持する状態です。</returns>
    private async Task<ApplicationState> EnsureGmailProductionDeliveryStartAsync(
        ApplicationState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.GmailProductionDeliveryStartedAtUtc is not null
            && state.SchemaVersion == ApplicationState.CurrentSchemaVersion)
        {
            return state;
        }

        DateTimeOffset startedAtUtc = timeProvider.GetUtcNow();
        ApplicationState updated = await stateStore.UpdateAsync(
            current => current with
            {
                SchemaVersion = ApplicationState.CurrentSchemaVersion,
                GmailProductionDeliveryStartedAtUtc =
                    current.GmailProductionDeliveryStartedAtUtc ?? startedAtUtc,
            },
            cancellationToken);
        LogGmailProductionDeliveryStarted(logger, updated.GmailProductionDeliveryStartedAtUtc!.Value);
        return updated;
    }

    /// <summary>
    /// Gmail設定と認証状態の変化を配送有効期間の境界へ反映します。
    /// </summary>
    /// <param name="state">現在の永続状態です。</param>
    /// <param name="settings">現在適用中の設定です。</param>
    /// <param name="cancellationToken">状態確認と保存のキャンセル通知です。</param>
    /// <returns>境界を同期した状態と、取得できた認証状態です。</returns>
    private async Task<(ApplicationState State, GmailAuthenticationStatus? AuthenticationStatus)>
        SynchronizeGmailDeliveryBoundaryAsync(
            ApplicationState state,
            AppSettings settings,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(settings);
        GmailAuthenticationStatus? authenticationStatus = null;
        if (settings.GmailNotificationEnabled)
        {
            try
            {
                authenticationStatus = await gmailAuthenticationStatusProvider
                    .GetStatusAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                LogGmailDeliveryUnavailable(logger, "Gmail認証状態を確認できませんでした。");
            }
        }

        bool becameEnabled = settings.GmailNotificationEnabled
            && !state.GmailDeliveryEnabledLastObserved;
        bool reauthenticationCompleted = settings.GmailNotificationEnabled
            && authenticationStatus?.CanSendMail == true
            && !state.GmailAuthenticationWasUsable;
        bool authenticationWasUsable = settings.GmailNotificationEnabled
            && authenticationStatus?.CanSendMail == true;
        if (!becameEnabled
            && !reauthenticationCompleted
            && state.GmailDeliveryEnabledLastObserved == settings.GmailNotificationEnabled
            && (authenticationStatus is null
                || state.GmailAuthenticationWasUsable == authenticationWasUsable))
        {
            return (state, authenticationStatus);
        }

        DateTimeOffset nowUtc = timeProvider.GetUtcNow();
        ApplicationState updated = await stateStore.UpdateAsync(
            current => current with
            {
                GmailDeliveryEnabledSinceUtc = becameEnabled || reauthenticationCompleted
                    ? nowUtc
                    : current.GmailDeliveryEnabledSinceUtc,
                GmailDeliveryEnabledLastObserved = settings.GmailNotificationEnabled,
                GmailAuthenticationWasUsable = authenticationStatus is null
                    ? current.GmailAuthenticationWasUsable
                    : authenticationWasUsable,
            },
            cancellationToken);
        if (becameEnabled || reauthenticationCompleted)
        {
            LogGmailDeliveryBoundaryStarted(
                logger,
                updated.GmailDeliveryEnabledSinceUtc!.Value,
                reauthenticationCompleted ? "Reauthenticated" : "Enabled");
        }

        return (updated, authenticationStatus);
    }

    /// <summary>
    /// 共通候補のうちWindowsチャネルで試行可能な候補だけを1件へ集約して配送します。
    /// </summary>
    private async Task<ApplicationState> DeliverWindowsAsync(
        IReadOnlyList<RateLimitNotificationCandidate> candidates,
        ApplicationState currentState,
        UsageSnapshot snapshot,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        List<RateLimitNotificationCandidate> windowsCandidates = candidates
            .Where(candidate =>
            {
                RateLimitNotificationState? existing = FindNotificationState(
                    currentState.RateLimitNotificationStates,
                    candidate);
                return settings.WindowsNotificationEnabled
                    && RateLimitNotificationPolicy.CanAttemptWindows(existing, snapshot.CapturedAtUtc);
            })
            .ToList();
        if (windowsCandidates.Count == 0)
        {
            return currentState;
        }

        List<RateLimitNotificationState> inProgressStates = [];
        foreach (RateLimitNotificationCandidate candidate in windowsCandidates)
        {
            RateLimitNotificationState existing = FindNotificationState(
                currentState.RateLimitNotificationStates,
                candidate) ?? throw new InvalidOperationException("Windows通知状態が保存されていません。");
            RateLimitNotificationState inProgress = CreateInProgressState(
                candidate,
                existing,
                timeProvider.GetUtcNow());
            currentState = await SaveNotificationStateAsync(inProgress, cancellationToken);
            inProgressStates.Add(inProgress);
        }

        WindowsNotificationMessage message = WindowsNotificationMessageFactory.CreateAggregate(
            windowsCandidates,
            snapshot.CapturedAtUtc);
        try
        {
            await windowsNotificationSender.SendAsync(message, cancellationToken);
            DateTimeOffset deliveredAtUtc = timeProvider.GetUtcNow();
            foreach ((RateLimitNotificationCandidate candidate, RateLimitNotificationState inProgress) in
                     windowsCandidates.Zip(inProgressStates))
            {
                RateLimitNotificationState succeeded = inProgress with
                {
                    WindowsDeliveryStatus = DeliveryStatus.Succeeded,
                    DeliveredAtUtc = inProgress.DeliveredAtUtc ?? deliveredAtUtc,
                };
                currentState = await SaveNotificationStateAsync(succeeded, cancellationToken);
                LogNotificationSucceeded(logger, candidate.NotificationType, candidate.NotificationStage);
            }

            return await stateStore.UpdateAsync(
                state => state with
                {
                    LastNotifiedRecoveryWindowId = windowsCandidates[^1].RecoveryWindowId,
                    WindowsDeliveryResult = new DeliveryResultState
                    {
                        Status = DeliveryStatus.Succeeded,
                        AttemptedAtUtc = deliveredAtUtc,
                        Summary = CreateDeliverySummary(windowsCandidates),
                    },
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DateTimeOffset attemptedAtUtc = timeProvider.GetUtcNow();
            foreach ((RateLimitNotificationCandidate candidate, RateLimitNotificationState inProgress) in
                     windowsCandidates.Zip(inProgressStates))
            {
                RateLimitNotificationState failed = inProgress with
                {
                    WindowsDeliveryStatus = DeliveryStatus.Failed,
                    WindowsNextRetryAtUtc = attemptedAtUtc.Add(WindowsRetryDelay),
                };
                currentState = await SaveNotificationStateAsync(failed, cancellationToken);
                LogNotificationFailed(logger, candidate.NotificationType, candidate.NotificationStage, exception);
            }

            return await stateStore.UpdateAsync(
                state => state with
                {
                    WindowsDeliveryResult = new DeliveryResultState
                    {
                        Status = DeliveryStatus.Failed,
                        AttemptedAtUtc = attemptedAtUtc,
                        Summary = "Windows通知を表示できませんでした。",
                    },
                },
                cancellationToken);
        }
    }

    /// <summary>
    /// 共通候補のうちPhase 4C開始後に成立したGmail未試行候補だけを1通へ集約して配送します。
    /// </summary>
    private async Task<ApplicationState> DeliverGmailAsync(
        IReadOnlyList<RateLimitNotificationCandidate> candidates,
        ApplicationState currentState,
        UsageSnapshot snapshot,
        AppSettings settings,
        GmailAuthenticationStatus? authenticationStatus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.GmailNotificationEnabled
            || string.IsNullOrWhiteSpace(settings.GmailRecipient)
            || !AppSettings.IsValidOptionalEmailAddress(settings.GmailRecipient))
        {
            return currentState;
        }

        if (authenticationStatus?.CanSendMail != true)
        {
            LogGmailDeliveryUnavailable(logger, "Gmailが未認証または再認証待ちです。");
            return currentState;
        }

        DateTimeOffset startedAtUtc = currentState.GmailProductionDeliveryStartedAtUtc
            ?? throw new InvalidOperationException("Gmail本番配送開始時刻が保存されていません。");
        DateTimeOffset enabledSinceUtc = currentState.GmailDeliveryEnabledSinceUtc
            ?? throw new InvalidOperationException("Gmail配送有効期間の開始時刻が保存されていません。");
        List<RateLimitNotificationCandidate> gmailCandidates = candidates
            .Where(candidate =>
            {
                RateLimitNotificationState? existing = FindNotificationState(
                    currentState.RateLimitNotificationStates,
                    candidate);
                return existing is not null
                    && RateLimitNotificationPolicy.CanAttemptGmail(existing, snapshot.CapturedAtUtc)
                    && existing.ConditionMetAtUtc >= startedAtUtc
                    && existing.ConditionMetAtUtc >= enabledSinceUtc;
            })
            .ToList();
        if (gmailCandidates.Count == 0)
        {
            return currentState;
        }

        DateTimeOffset attemptedAtUtc = timeProvider.GetUtcNow();
        List<RateLimitNotificationState> inProgressStates = [];
        foreach (RateLimitNotificationCandidate candidate in gmailCandidates)
        {
            RateLimitNotificationState existing = FindNotificationState(
                currentState.RateLimitNotificationStates,
                candidate) ?? throw new InvalidOperationException("Gmail通知状態が保存されていません。");
            RateLimitNotificationState inProgress = existing with
            {
                GmailDeliveryStatus = DeliveryStatus.InProgress,
                GmailAttemptCount = existing.GmailAttemptCount + 1,
                GmailLastAttemptedAtUtc = attemptedAtUtc,
                GmailNextRetryAtUtc = null,
                GmailFailureKind = GmailDeliveryFailureKind.None,
                DeferredUntilUtc = null,
            };
            currentState = await SaveNotificationStateAsync(inProgress, cancellationToken);
            inProgressStates.Add(inProgress);
            LogGmailDeliveryAttemptStarted(
                logger,
                candidate.NotificationType,
                candidate.NotificationStage,
                inProgress.GmailAttemptCount,
                inProgress.GmailAttemptCount == 1 ? "Initial" : "Retry");
        }

        GmailNotificationMessage message = GmailNotificationMessageFactory.CreateAggregate(
            gmailCandidates,
            snapshot.CapturedAtUtc,
            timeProvider.LocalTimeZone);
        try
        {
            await gmailNotificationSender.SendAsync(
                settings.GmailRecipient,
                message,
                cancellationToken).ConfigureAwait(false);
            DateTimeOffset deliveredAtUtc = timeProvider.GetUtcNow();
            foreach ((RateLimitNotificationCandidate candidate, RateLimitNotificationState inProgress) in
                     gmailCandidates.Zip(inProgressStates))
            {
                RateLimitNotificationState succeeded = inProgress with
                {
                    GmailDeliveryStatus = DeliveryStatus.Succeeded,
                    GmailNextRetryAtUtc = null,
                    GmailFailureKind = GmailDeliveryFailureKind.None,
                    DeliveredAtUtc = inProgress.DeliveredAtUtc ?? deliveredAtUtc,
                };
                currentState = await SaveNotificationStateAsync(succeeded, cancellationToken);
                LogGmailNotificationSucceeded(logger, candidate.NotificationType, candidate.NotificationStage);
                if (inProgress.GmailAttemptCount == RateLimitNotificationPolicy.MaxGmailAttemptCount)
                {
                    LogGmailRetrySucceeded(
                        logger,
                        candidate.NotificationType,
                        candidate.NotificationStage);
                }
            }

            return await stateStore.UpdateAsync(
                state => state with
                {
                    GmailDeliveryResult = new DeliveryResultState
                    {
                        Status = DeliveryStatus.Succeeded,
                        AttemptedAtUtc = deliveredAtUtc,
                        Summary = CreateDeliverySummary(gmailCandidates),
                    },
                },
                cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return await HandleGmailDeliveryFailureAsync(
                gmailCandidates,
                inProgressStates,
                currentState,
                attemptedAtUtc,
                exception,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await HandleGmailDeliveryFailureAsync(
                gmailCandidates,
                inProgressStates,
                currentState,
                attemptedAtUtc,
                exception,
                cancellationToken);
        }
    }

    /// <summary>例外からトークンやメール本文を含まないGmail配送失敗概要を生成します。</summary>
    private static string CreateSafeGmailFailureSummary(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            GmailApiOperationException apiException => apiException.Message,
            InvalidOperationException => "Gmailの認証を確認できないため通知メールを送信できませんでした。",
            _ => "Gmail通知メールを送信できませんでした。",
        };
    }

    /// <summary>
    /// Gmail集約送信の失敗を候補ごとに保存し、一時障害だけを60分後へ予約します。
    /// </summary>
    /// <param name="candidates">今回集約した通知候補です。</param>
    /// <param name="inProgressStates">送信前に保存した候補別状態です。</param>
    /// <param name="currentState">送信直前のアプリケーション状態です。</param>
    /// <param name="attemptedAtUtc">今回の試行開始UTC時刻です。</param>
    /// <param name="exception">送信中に発生した例外です。</param>
    /// <param name="cancellationToken">状態保存のキャンセル通知です。</param>
    /// <returns>失敗結果と再試行時刻を保存した状態です。</returns>
    private async Task<ApplicationState> HandleGmailDeliveryFailureAsync(
        IReadOnlyList<RateLimitNotificationCandidate> candidates,
        IReadOnlyList<RateLimitNotificationState> inProgressStates,
        ApplicationState currentState,
        DateTimeOffset attemptedAtUtc,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(inProgressStates);
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(exception);
        GmailDeliveryFailureKind failureKind = GmailDeliveryFailureClassifier.Classify(exception);
        string safeSummary = CreateSafeGmailFailureSummary(exception);
        foreach ((RateLimitNotificationCandidate candidate, RateLimitNotificationState inProgress) in
                 candidates.Zip(inProgressStates))
        {
            bool canRetry = failureKind == GmailDeliveryFailureKind.Transient
                && inProgress.GmailAttemptCount < RateLimitNotificationPolicy.MaxGmailAttemptCount;
            DateTimeOffset? nextRetryAtUtc = canRetry
                ? inProgress.GmailLastAttemptedAtUtc!.Value.Add(GmailRetryDelay)
                : null;
            RateLimitNotificationState failed = inProgress with
            {
                GmailDeliveryStatus = DeliveryStatus.Failed,
                GmailNextRetryAtUtc = nextRetryAtUtc,
                GmailFailureKind = failureKind,
            };
            currentState = await SaveNotificationStateAsync(failed, cancellationToken);
            LogGmailNotificationFailed(
                logger,
                candidate.NotificationType,
                candidate.NotificationStage,
                safeSummary);
            if (nextRetryAtUtc is not null)
            {
                LogGmailRetryScheduled(
                    logger,
                    candidate.NotificationType,
                    candidate.NotificationStage,
                    nextRetryAtUtc.Value);
            }
            else if (inProgress.GmailAttemptCount >= RateLimitNotificationPolicy.MaxGmailAttemptCount)
            {
                LogGmailMaximumAttemptsReached(
                    logger,
                    candidate.NotificationType,
                    candidate.NotificationStage);
            }
            else if (failureKind == GmailDeliveryFailureKind.Authentication)
            {
                LogGmailReauthenticationRequired(
                    logger,
                    candidate.NotificationType,
                    candidate.NotificationStage);
            }
        }

        return await stateStore.UpdateAsync(
            state => state with
            {
                GmailAuthenticationWasUsable = failureKind == GmailDeliveryFailureKind.Authentication
                    ? false
                    : state.GmailAuthenticationWasUsable,
                GmailDeliveryResult = new DeliveryResultState
                {
                    Status = DeliveryStatus.Failed,
                    AttemptedAtUtc = attemptedAtUtc,
                    Summary = safeSummary,
                },
            },
            cancellationToken);
    }

    /// <summary>
    /// 3回連続失敗時の監視異常をWindowsへ1回だけ通知します。
    /// </summary>
    /// <param name="state">連続失敗回数を保存済みの状態です。</param>
    /// <param name="settings">Windows通知設定です。</param>
    /// <param name="cancellationToken">送信のキャンセル通知です。</param>
    /// <returns>通知済みフラグを反映した最新状態です。</returns>
    public async Task<ApplicationState> NotifyMonitoringFailureAsync(
        ApplicationState state,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(settings);
        if (state.ConsecutiveFailures < 3
            || state.FailureNotificationSent
            || !settings.WindowsNotificationEnabled)
        {
            return state;
        }

        try
        {
            await windowsNotificationSender.SendAsync(
                new WindowsNotificationMessage
                {
                    Title = "Codex利用枠の監視に失敗しています",
                    Body = $"Codex App Serverとの通信に{state.ConsecutiveFailures}回連続で失敗しました。状態画面とログを確認してください。",
                },
                cancellationToken);
            DateTimeOffset attemptedAtUtc = timeProvider.GetUtcNow();
            return await stateStore.UpdateAsync(
                current => current with
                {
                    FailureNotificationSent = true,
                    WindowsDeliveryResult = new DeliveryResultState
                    {
                        Status = DeliveryStatus.Succeeded,
                        AttemptedAtUtc = attemptedAtUtc,
                        Summary = RateLimitNotificationType.MonitoringFailure.ToString(),
                    },
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogMonitoringFailureNotificationFailed(logger, exception);
            return state;
        }
    }

    /// <summary>
    /// 通知候補から永続化用の通知状態を生成します。
    /// </summary>
    /// <param name="candidate">通知候補です。</param>
    /// <param name="windowsStatus">Windows通知状態です。</param>
    /// <param name="deliveredAtUtc">送信成功時刻です。</param>
    /// <param name="deferredUntilUtc">保留終了時刻です。</param>
    /// <returns>永続化可能な通知状態です。</returns>
    private static RateLimitNotificationState CreateState(
        RateLimitNotificationCandidate candidate,
        DeliveryStatus windowsStatus,
        DateTimeOffset? deliveredAtUtc,
        DateTimeOffset? deferredUntilUtc)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return new RateLimitNotificationState
        {
            LimitId = candidate.Window.LimitId ?? string.Empty,
            Position = candidate.Window.Position,
            WindowDurationMinutes = candidate.Window.WindowDurationMinutes ?? 0,
            RecoveryWindowId = candidate.RecoveryWindowId,
            NotificationType = candidate.NotificationType,
            NotificationStage = candidate.NotificationStage,
            ConditionMetAtUtc = candidate.ConditionMetAtUtc,
            DeliveredAtUtc = deliveredAtUtc,
            WindowsDeliveryStatus = windowsStatus,
            GmailDeliveryStatus = DeliveryStatus.NotAttempted,
            DeferredUntilUtc = deferredUntilUtc,
            ResetCompletionReason = candidate.ResetCompletionReason,
        };
    }

    /// <summary>
    /// 新規または再試行対象の候補からWindows送信中状態を生成します。
    /// </summary>
    /// <param name="candidate">今回送信する通知候補です。</param>
    /// <param name="existing">同じ通知の保存済み状態です。</param>
    /// <param name="attemptedAtUtc">表示要求を開始するUTC時刻です。</param>
    /// <returns>試行回数を増加させた送信中状態です。</returns>
    private static RateLimitNotificationState CreateInProgressState(
        RateLimitNotificationCandidate candidate,
        RateLimitNotificationState? existing,
        DateTimeOffset attemptedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        RateLimitNotificationState state = CreateState(
            candidate,
            DeliveryStatus.InProgress,
            deliveredAtUtc: existing?.DeliveredAtUtc,
            deferredUntilUtc: null);
        return state with
        {
            WindowsAttemptCount = (existing?.WindowsAttemptCount ?? 0) + 1,
            WindowsLastAttemptedAtUtc = attemptedAtUtc,
            WindowsNextRetryAtUtc = null,
            GmailDeliveryStatus = existing?.GmailDeliveryStatus ?? DeliveryStatus.NotAttempted,
            GmailAttemptCount = existing?.GmailAttemptCount ?? 0,
            GmailLastAttemptedAtUtc = existing?.GmailLastAttemptedAtUtc,
            GmailNextRetryAtUtc = existing?.GmailNextRetryAtUtc,
            GmailFailureKind = existing?.GmailFailureKind ?? GmailDeliveryFailureKind.None,
        };
    }

    /// <summary>
    /// 強制終了などで残った古いWindows送信中状態を再試行可能な失敗状態へ戻します。
    /// </summary>
    /// <param name="state">読み込んだアプリケーション状態です。</param>
    /// <param name="nowUtc">今回の正常取得UTC時刻です。</param>
    /// <param name="cancellationToken">状態保存のキャンセル通知です。</param>
    /// <returns>中断試行を回復済みの状態です。</returns>
    private async Task<ApplicationState> RecoverInterruptedWindowsAttemptsAsync(
        ApplicationState state,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        DateTimeOffset staleBeforeUtc = nowUtc.Subtract(WindowsInProgressTimeout);
        bool hasInterrupted = state.RateLimitNotificationStates.Any(notification =>
            notification.WindowsDeliveryStatus == DeliveryStatus.InProgress
            && (notification.WindowsLastAttemptedAtUtc ?? notification.ConditionMetAtUtc) <= staleBeforeUtc);
        if (!hasInterrupted)
        {
            return state;
        }

        ApplicationState recovered = await stateStore.UpdateAsync(
            current => current with
            {
                RateLimitNotificationStates = current.RateLimitNotificationStates
                    .Select(notification =>
                        notification.WindowsDeliveryStatus == DeliveryStatus.InProgress
                        && (notification.WindowsLastAttemptedAtUtc ?? notification.ConditionMetAtUtc) <= staleBeforeUtc
                            ? notification with
                            {
                                WindowsDeliveryStatus = DeliveryStatus.Failed,
                                WindowsNextRetryAtUtc = nowUtc,
                            }
                            : notification)
                    .ToArray(),
            },
            cancellationToken);
        LogInterruptedWindowsAttemptsRecovered(logger);
        return recovered;
    }

    /// <summary>
    /// 60分以上残ったGmail送信中状態を、試行回数を維持したまま再試行可能状態へ戻します。
    /// </summary>
    /// <param name="state">読み込んだアプリケーション状態です。</param>
    /// <param name="nowUtc">今回の正常取得UTC時刻です。</param>
    /// <param name="cancellationToken">状態保存のキャンセル通知です。</param>
    /// <returns>中断試行を回復済みの状態です。</returns>
    private async Task<ApplicationState> RecoverInterruptedGmailAttemptsAsync(
        ApplicationState state,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        DateTimeOffset staleBeforeUtc = nowUtc.Subtract(GmailInProgressTimeout);
        bool hasInterrupted = state.RateLimitNotificationStates.Any(notification =>
            notification.GmailDeliveryStatus == DeliveryStatus.InProgress
            && (notification.GmailLastAttemptedAtUtc ?? notification.ConditionMetAtUtc) <= staleBeforeUtc);
        if (!hasInterrupted)
        {
            return state;
        }

        ApplicationState recovered = await stateStore.UpdateAsync(
            current => current with
            {
                RateLimitNotificationStates = current.RateLimitNotificationStates
                    .Select(notification =>
                    {
                        DateTimeOffset lastAttemptedAtUtc = notification.GmailLastAttemptedAtUtc
                            ?? notification.ConditionMetAtUtc;
                        if (notification.GmailDeliveryStatus != DeliveryStatus.InProgress
                            || lastAttemptedAtUtc > staleBeforeUtc)
                        {
                            return notification;
                        }

                        bool canRetry = notification.GmailAttemptCount
                            < RateLimitNotificationPolicy.MaxGmailAttemptCount;
                        return notification with
                        {
                            GmailDeliveryStatus = DeliveryStatus.Failed,
                            GmailFailureKind = GmailDeliveryFailureKind.Interrupted,
                            GmailNextRetryAtUtc = canRetry
                                ? lastAttemptedAtUtc.Add(GmailRetryDelay)
                                : null,
                        };
                    })
                    .ToArray(),
            },
            cancellationToken);
        LogInterruptedGmailAttemptsRecovered(logger);
        return recovered;
    }

    /// <summary>
    /// 現在は意味を持たないGmail再試行を期限切れへ変更します。
    /// </summary>
    /// <param name="state">読み込んだアプリケーション状態です。</param>
    /// <param name="snapshot">今回の正常取得結果です。</param>
    /// <param name="settings">現在適用中の通知設定です。</param>
    /// <param name="cancellationToken">状態保存のキャンセル通知です。</param>
    /// <returns>無効な再試行を期限切れへ変更した状態です。</returns>
    private async Task<ApplicationState> ExpireInvalidGmailRetriesAsync(
        ApplicationState state,
        UsageSnapshot snapshot,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        RateLimitNotificationState[] expired = state.RateLimitNotificationStates
            .Where(notification => IsInvalidGmailRetry(notification, state, snapshot, settings))
            .ToArray();
        if (expired.Length == 0)
        {
            return state;
        }

        ApplicationState updated = await stateStore.UpdateAsync(
            current => current with
            {
                RateLimitNotificationStates = current.RateLimitNotificationStates
                    .Select(notification => expired.Any(item => HasSameIdentity(item, notification))
                        ? notification with
                        {
                            GmailDeliveryStatus = DeliveryStatus.Expired,
                            GmailNextRetryAtUtc = null,
                        }
                        : notification)
                    .ToArray(),
            },
            cancellationToken);
        foreach (RateLimitNotificationState notification in expired)
        {
            LogGmailRetryExpired(
                logger,
                notification.NotificationType,
                notification.NotificationStage);
        }

        return updated;
    }

    /// <summary>
    /// Gmailの失敗候補が現在の残量、警告段階、または利用期間と一致しないか判定します。
    /// </summary>
    private static bool IsInvalidGmailRetry(
        RateLimitNotificationState notification,
        ApplicationState state,
        UsageSnapshot snapshot,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        if (notification.GmailDeliveryStatus != DeliveryStatus.Failed
            || notification.GmailFailureKind is not (GmailDeliveryFailureKind.Transient
                or GmailDeliveryFailureKind.Interrupted))
        {
            return false;
        }

        RateLimitWindow? window = snapshot.RateLimits.FirstOrDefault(candidate =>
            string.Equals(candidate.LimitId, notification.LimitId, StringComparison.Ordinal)
            && candidate.Position == notification.Position
            && candidate.WindowDurationMinutes == notification.WindowDurationMinutes);
        if (window is null)
        {
            return true;
        }

        RateLimitNotificationSetting windowSetting = RateLimitNotificationSettingsResolver.Resolve(
            window,
            settings);
        if (notification.NotificationType == RateLimitNotificationType.ShortWindowRecovered)
        {
            string? currentRecoveryWindowId = window.ResetsAtUtc is not null
                ? RateLimitNotificationPolicy.CreateRecoveryWindowId(window, snapshot.CapturedAtUtc)
                : CreateNoResetCurrentRecoveryWindowId(window, state.RateLimitRecoveryStates);
            return !windowSetting.ShortWindowRecoveryEnabled
                || window.RemainingPercent < settings.ShortWindowRecoveryThresholdPercent
                || !string.Equals(
                    notification.RecoveryWindowId,
                    currentRecoveryWindowId,
                    StringComparison.Ordinal);
        }

        if (IsLongWindowWarning(notification.NotificationType))
        {
            return !IsCurrentWarningCondition(notification, window, snapshot.CapturedAtUtc, settings)
                || !IsWarningEnabled(notification.NotificationStage, windowSetting);
        }

        if (notification.NotificationType == RateLimitNotificationType.LongWindowResetCompleted)
        {
            if (!windowSetting.LongWindowResetCompletedEnabled)
            {
                return true;
            }

            if (window.ResetsAtUtc is not null)
            {
                string currentRecoveryWindowId = RateLimitNotificationPolicy.CreateRecoveryWindowId(
                    window,
                    snapshot.CapturedAtUtc);
                return !string.Equals(
                    notification.RecoveryWindowId,
                    currentRecoveryWindowId,
                    StringComparison.Ordinal);
            }

            return notification.ResetCompletionReason != RateLimitResetCompletionReason.UsageDropInference
                || notification.ConditionMetAtUtc < snapshot.CapturedAtUtc.Subtract(TimeSpan.FromHours(24))
                || state.RateLimitNotificationStates.Any(candidate =>
                    candidate.NotificationType == RateLimitNotificationType.LongWindowResetCompleted
                    && string.Equals(candidate.LimitId, notification.LimitId, StringComparison.Ordinal)
                    && candidate.Position == notification.Position
                    && candidate.WindowDurationMinutes == notification.WindowDurationMinutes
                    && candidate.ConditionMetAtUtc > notification.ConditionMetAtUtc);
        }

        return true;
    }

    /// <summary>保存済み段階に対応する長期枠通知設定が有効か判定します。</summary>
    private static bool IsWarningEnabled(
        RateLimitNotificationStage stage,
        RateLimitNotificationSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return stage switch
        {
            RateLimitNotificationStage.Early => setting.LongWindowEarlyWarningEnabled,
            RateLimitNotificationStage.Standard => setting.LongWindowStandardWarningEnabled,
            RateLimitNotificationStage.Final => setting.LongWindowFinalWarningEnabled,
            _ => false,
        };
    }

    /// <summary>
    /// 古すぎる保留または現在の利用期間と一致しない保留を期限切れへ変更します。
    /// </summary>
    /// <param name="state">読み込んだアプリケーション状態です。</param>
    /// <param name="snapshot">今回の正常取得結果です。</param>
    /// <param name="cancellationToken">状態保存のキャンセル通知です。</param>
    /// <returns>無効な保留を期限切れへ変更した状態です。</returns>
    private async Task<ApplicationState> ExpireInvalidDeferredNotificationsAsync(
        ApplicationState state,
        UsageSnapshot snapshot,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        bool hasExpired = state.RateLimitNotificationStates.Any(notification =>
            IsInvalidDeferredNotification(notification, state, snapshot, settings));
        if (!hasExpired)
        {
            return state;
        }

        ApplicationState updated = await stateStore.UpdateAsync(
            current => current with
            {
                RateLimitNotificationStates = current.RateLimitNotificationStates
                    .Select(notification => IsInvalidDeferredNotification(notification, current, snapshot, settings)
                        ? notification with
                        {
                            WindowsDeliveryStatus = notification.WindowsDeliveryStatus == DeliveryStatus.NotAttempted
                                ? DeliveryStatus.Expired
                                : notification.WindowsDeliveryStatus,
                            GmailDeliveryStatus = notification.GmailDeliveryStatus == DeliveryStatus.NotAttempted
                                ? DeliveryStatus.Expired
                                : notification.GmailDeliveryStatus,
                            DeferredUntilUtc = null,
                            WindowsNextRetryAtUtc = null,
                            GmailNextRetryAtUtc = null,
                        }
                        : notification)
                    .ToArray(),
            },
            cancellationToken);
        LogDeferredNotificationsExpired(logger);
        return updated;
    }

    /// <summary>
    /// 1件のWindows保留通知が古すぎるか現在の利用期間と不一致か判定します。
    /// </summary>
    /// <param name="notification">判定対象の通知状態です。</param>
    /// <param name="state">回復連番を含む保存状態です。</param>
    /// <param name="snapshot">現在取得した利用枠です。</param>
    /// <returns>保留を期限切れにする場合はtrueです。</returns>
    private static bool IsInvalidDeferredNotification(
        RateLimitNotificationState notification,
        ApplicationState state,
        UsageSnapshot snapshot,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        if (notification.DeferredUntilUtc is null
            || (notification.WindowsDeliveryStatus != DeliveryStatus.NotAttempted
                && notification.GmailDeliveryStatus != DeliveryStatus.NotAttempted))
        {
            return false;
        }

        if (notification.ConditionMetAtUtc < snapshot.CapturedAtUtc.Subtract(TimeSpan.FromHours(24)))
        {
            return true;
        }

        RateLimitWindow? window = snapshot.RateLimits.FirstOrDefault(candidate =>
            string.Equals(candidate.LimitId, notification.LimitId, StringComparison.Ordinal)
            && candidate.Position == notification.Position
            && candidate.WindowDurationMinutes == notification.WindowDurationMinutes);
        if (window is null)
        {
            return true;
        }

        if (IsLongWindowWarning(notification.NotificationType)
            && notification.DeferredUntilUtc <= snapshot.CapturedAtUtc
            && !IsCurrentWarningCondition(notification, window, snapshot.CapturedAtUtc, settings))
        {
            return true;
        }

        string? currentRecoveryWindowId = window.ResetsAtUtc is not null
            ? RateLimitNotificationPolicy.CreateRecoveryWindowId(window, snapshot.CapturedAtUtc)
            : CreateNoResetCurrentRecoveryWindowId(window, state.RateLimitRecoveryStates);
        return currentRecoveryWindowId is not null
            && !string.Equals(
                notification.RecoveryWindowId,
                currentRecoveryWindowId,
                StringComparison.Ordinal);
    }

    /// <summary>通知種別が長期枠のリセット前警告か判定します。</summary>
    private static bool IsLongWindowWarning(RateLimitNotificationType notificationType)
    {
        return notificationType is RateLimitNotificationType.LongWindowEarlyWarning
            or RateLimitNotificationType.LongWindowStandardWarning
            or RateLimitNotificationType.LongWindowFinalWarning;
    }

    /// <summary>保留した長期枠警告が現在も同じ時間帯と残量条件を満たすか判定します。</summary>
    private static bool IsCurrentWarningCondition(
        RateLimitNotificationState notification,
        RateLimitWindow window,
        DateTimeOffset nowUtc,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(settings);
        if (window.ResetsAtUtc is null)
        {
            return false;
        }

        double remainingHours = (window.ResetsAtUtc.Value - nowUtc).TotalHours;
        RateLimitNotificationStage? currentStage = remainingHours <= 0
            ? null
            : remainingHours <= settings.LongWindowFinalWarningHours
                ? RateLimitNotificationStage.Final
                : remainingHours <= settings.LongWindowStandardWarningHours
                    ? RateLimitNotificationStage.Standard
                    : remainingHours <= settings.LongWindowEarlyWarningHours
                        ? RateLimitNotificationStage.Early
                        : null;
        double threshold = notification.NotificationStage switch
        {
            RateLimitNotificationStage.Early => settings.LongWindowEarlyWarningThresholdPercent,
            RateLimitNotificationStage.Standard => settings.LongWindowStandardWarningThresholdPercent,
            RateLimitNotificationStage.Final => settings.LongWindowFinalWarningThresholdPercent,
            _ => double.PositiveInfinity,
        };
        return notification.NotificationStage == currentStage
            && window.RemainingPercent >= threshold;
    }

    /// <summary>
    /// リセット時刻がない短期枠について、保存済み回復連番から現在期間IDを生成します。
    /// </summary>
    /// <param name="window">現在取得した利用枠です。</param>
    /// <param name="recoveryStates">保存済み回復状態です。</param>
    /// <returns>現在期間ID、または回復状態がない場合のnullです。</returns>
    private static string? CreateNoResetCurrentRecoveryWindowId(
        RateLimitWindow window,
        IReadOnlyList<RateLimitRecoveryState> recoveryStates)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(recoveryStates);
        if (window.Classification != RateLimitClassification.FiveHour)
        {
            return null;
        }

        RateLimitRecoveryState? recovery = recoveryStates.FirstOrDefault(candidate =>
            string.Equals(candidate.LimitId, window.LimitId, StringComparison.Ordinal)
            && candidate.Position == window.Position
            && candidate.WindowDurationMinutes == window.WindowDurationMinutes);
        return recovery is null
            ? null
            : RateLimitNotificationPolicy.CreateNoResetRecoveryWindowId(
                window,
                recovery.RecoverySequence);
    }

    /// <summary>
    /// 保存済み状態から通知候補と同じ複合キーの状態を検索します。
    /// </summary>
    /// <param name="states">検索対象の通知状態です。</param>
    /// <param name="candidate">検索する通知候補です。</param>
    /// <returns>同じ通知を表す状態、または未登録時のnullです。</returns>
    private static RateLimitNotificationState? FindNotificationState(
        IReadOnlyList<RateLimitNotificationState> states,
        RateLimitNotificationCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(candidate);
        return states.FirstOrDefault(state =>
            string.Equals(state.LimitId, candidate.Window.LimitId, StringComparison.Ordinal)
            && state.Position == candidate.Window.Position
            && state.WindowDurationMinutes == candidate.Window.WindowDurationMinutes
            && string.Equals(state.RecoveryWindowId, candidate.RecoveryWindowId, StringComparison.Ordinal)
            && state.NotificationType == candidate.NotificationType
            && state.NotificationStage == candidate.NotificationStage);
    }

    /// <summary>
    /// リセット完了候補に判定理由がある場合は診断ログへ記録します。
    /// </summary>
    /// <param name="candidate">判定理由を確認する通知候補です。</param>
    private void LogResetCompletionReason(RateLimitNotificationCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.ResetCompletionReason is not null)
        {
            LogResetCompletionDetected(
                logger,
                candidate.Window.LimitId ?? "(null)",
                candidate.Window.Position,
                candidate.Window.WindowDurationMinutes,
                candidate.ResetCompletionReason.Value);
        }
    }

    /// <summary>
    /// 同じ複合キーの通知状態を置換し、存在しない場合は末尾へ追加します。
    /// </summary>
    /// <param name="notificationState">保存する通知状態です。</param>
    /// <param name="cancellationToken">保存のキャンセル通知です。</param>
    /// <returns>保存後のアプリケーション状態です。</returns>
    private Task<ApplicationState> SaveNotificationStateAsync(
        RateLimitNotificationState notificationState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notificationState);
        return stateStore.UpdateAsync(
            state =>
            {
                List<RateLimitNotificationState> states = state.RateLimitNotificationStates
                    .Where(existing => !HasSameIdentity(existing, notificationState))
                    .ToList();
                states.Add(notificationState);
                return state with { RateLimitNotificationStates = states };
            },
            cancellationToken);
    }

    /// <summary>
    /// 2つの通知状態が同じ複合キーを持つか判定します。
    /// </summary>
    /// <param name="left">比較する既存状態です。</param>
    /// <param name="right">比較する新しい状態です。</param>
    /// <returns>同じ通知を表す場合はtrueです。</returns>
    private static bool HasSameIdentity(
        RateLimitNotificationState left,
        RateLimitNotificationState right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return string.Equals(left.LimitId, right.LimitId, StringComparison.Ordinal)
            && left.Position == right.Position
            && left.WindowDurationMinutes == right.WindowDurationMinutes
            && string.Equals(left.RecoveryWindowId, right.RecoveryWindowId, StringComparison.Ordinal)
            && left.NotificationType == right.NotificationType
            && left.NotificationStage == right.NotificationStage;
    }

    /// <summary>
    /// 集約して送信した通知候補を診断表示向けの短い文字列へ変換します。
    /// </summary>
    /// <param name="candidates">送信に含めた通知候補です。</param>
    /// <returns>件数と通知種別を含む概要です。</returns>
    private static string CreateDeliverySummary(List<RateLimitNotificationCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return $"{candidates.Count}件: {string.Join(", ", candidates.Select(candidate => $"{candidate.NotificationType}/{candidate.NotificationStage}"))}";
    }

    [LoggerMessage(2300, LogLevel.Information, "Windows通知を送信しました。NotificationType={NotificationType}, NotificationStage={NotificationStage}")]
    private static partial void LogNotificationSucceeded(
        ILogger logger,
        RateLimitNotificationType notificationType,
        RateLimitNotificationStage notificationStage);

    [LoggerMessage(2301, LogLevel.Error, "Windows通知を送信できませんでした。NotificationType={NotificationType}, NotificationStage={NotificationStage}")]
    private static partial void LogNotificationFailed(
        ILogger logger,
        RateLimitNotificationType notificationType,
        RateLimitNotificationStage notificationStage,
        Exception exception);

    [LoggerMessage(2302, LogLevel.Information, "通知禁止時間のため通知を保留しました。NotificationType={NotificationType}, NotificationStage={NotificationStage}, DeferredUntilUtc={DeferredUntilUtc}")]
    private static partial void LogNotificationDeferred(
        ILogger logger,
        RateLimitNotificationType notificationType,
        RateLimitNotificationStage notificationStage,
        DateTimeOffset deferredUntilUtc);

    [LoggerMessage(2303, LogLevel.Error, "監視失敗のWindows通知を表示できませんでした。")]
    private static partial void LogMonitoringFailureNotificationFailed(ILogger logger, Exception exception);

    [LoggerMessage(2304, LogLevel.Information, "長期枠のリセット完了を判定しました。LimitId={LimitId}, Position={Position}, WindowDurationMinutes={WindowDurationMinutes}, Reason={Reason}")]
    private static partial void LogResetCompletionDetected(
        ILogger logger,
        string limitId,
        RateLimitPosition position,
        int? windowDurationMinutes,
        RateLimitResetCompletionReason reason);

    [LoggerMessage(2305, LogLevel.Warning, "古いWindows通知送信中状態を再試行可能な状態へ戻しました。")]
    private static partial void LogInterruptedWindowsAttemptsRecovered(ILogger logger);

    [LoggerMessage(2306, LogLevel.Information, "無効になった保留通知の未送信チャネルを期限切れへ変更しました。")]
    private static partial void LogDeferredNotificationsExpired(ILogger logger);

    [LoggerMessage(2310, LogLevel.Information, "本番Gmail配送の開始境界を保存しました。StartedAtUtc={StartedAtUtc}")]
    private static partial void LogGmailProductionDeliveryStarted(ILogger logger, DateTimeOffset startedAtUtc);

    [LoggerMessage(2311, LogLevel.Debug, "本番Gmail配送を実行しません。Reason={Reason}")]
    private static partial void LogGmailDeliveryUnavailable(ILogger logger, string reason);

    [LoggerMessage(2312, LogLevel.Information, "Gmail通知を送信しました。NotificationType={NotificationType}, NotificationStage={NotificationStage}")]
    private static partial void LogGmailNotificationSucceeded(
        ILogger logger,
        RateLimitNotificationType notificationType,
        RateLimitNotificationStage notificationStage);

    [LoggerMessage(2313, LogLevel.Warning, "Gmail通知を送信できませんでした。NotificationType={NotificationType}, NotificationStage={NotificationStage}, Reason={Reason}")]
    private static partial void LogGmailNotificationFailed(
        ILogger logger,
        RateLimitNotificationType notificationType,
        RateLimitNotificationStage notificationStage,
        string reason);

    [LoggerMessage(2314, LogLevel.Information, "Gmail配送有効期間の開始境界を保存しました。EnabledSinceUtc={EnabledSinceUtc}, Reason={Reason}")]
    private static partial void LogGmailDeliveryBoundaryStarted(
        ILogger logger,
        DateTimeOffset enabledSinceUtc,
        string reason);

    [LoggerMessage(2315, LogLevel.Information, "Gmail通知の送信を開始しました。NotificationType={NotificationType}, NotificationStage={NotificationStage}, AttemptCount={AttemptCount}, AttemptKind={AttemptKind}")]
    private static partial void LogGmailDeliveryAttemptStarted(
        ILogger logger,
        RateLimitNotificationType notificationType,
        RateLimitNotificationStage notificationStage,
        int attemptCount,
        string attemptKind);

    [LoggerMessage(2316, LogLevel.Information, "Gmail通知の再試行を予約しました。NotificationType={NotificationType}, NotificationStage={NotificationStage}, NextRetryAtUtc={NextRetryAtUtc}")]
    private static partial void LogGmailRetryScheduled(
        ILogger logger,
        RateLimitNotificationType notificationType,
        RateLimitNotificationStage notificationStage,
        DateTimeOffset nextRetryAtUtc);

    [LoggerMessage(2317, LogLevel.Information, "Gmail通知の再試行に成功しました。NotificationType={NotificationType}, NotificationStage={NotificationStage}")]
    private static partial void LogGmailRetrySucceeded(
        ILogger logger,
        RateLimitNotificationType notificationType,
        RateLimitNotificationStage notificationStage);

    [LoggerMessage(2318, LogLevel.Warning, "Gmail通知が最大試行回数へ到達しました。NotificationType={NotificationType}, NotificationStage={NotificationStage}")]
    private static partial void LogGmailMaximumAttemptsReached(
        ILogger logger,
        RateLimitNotificationType notificationType,
        RateLimitNotificationStage notificationStage);

    [LoggerMessage(2319, LogLevel.Information, "Gmail通知の再試行を期限切れにしました。NotificationType={NotificationType}, NotificationStage={NotificationStage}")]
    private static partial void LogGmailRetryExpired(
        ILogger logger,
        RateLimitNotificationType notificationType,
        RateLimitNotificationStage notificationStage);

    [LoggerMessage(2320, LogLevel.Warning, "古いGmail通知送信中状態を再試行可能な状態へ戻しました。")]
    private static partial void LogInterruptedGmailAttemptsRecovered(ILogger logger);

    [LoggerMessage(2321, LogLevel.Warning, "Gmail通知には再認証が必要です。NotificationType={NotificationType}, NotificationStage={NotificationStage}")]
    private static partial void LogGmailReauthenticationRequired(
        ILogger logger,
        RateLimitNotificationType notificationType,
        RateLimitNotificationStage notificationStage);

    [LoggerMessage(2322, LogLevel.Information, "通知禁止時間のためGmail再試行を保留しました。NotificationType={NotificationType}, NotificationStage={NotificationStage}, DeferredUntilUtc={DeferredUntilUtc}")]
    private static partial void LogGmailRetryDeferredByQuietHours(
        ILogger logger,
        RateLimitNotificationType notificationType,
        RateLimitNotificationStage notificationStage,
        DateTimeOffset deferredUntilUtc);
}
