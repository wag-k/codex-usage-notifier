using CodexUsageNotifier.Application.Abstractions;
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
    private readonly ApplicationStateStore stateStore;
    private readonly IWindowsNotificationSender windowsNotificationSender;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<RateLimitNotificationProcessor> logger;

    /// <summary>
    /// 状態保存、Windows通知、時刻、およびロガーを受け取ります。
    /// </summary>
    /// <param name="stateStore">通知状態を保存するストアです。</param>
    /// <param name="windowsNotificationSender">Windows通知の送信先です。</param>
    /// <param name="timeProvider">通知禁止時間のタイムゾーンを提供します。</param>
    /// <param name="logger">通知判定と送信結果の記録先です。</param>
    public RateLimitNotificationProcessor(
        ApplicationStateStore stateStore,
        IWindowsNotificationSender windowsNotificationSender,
        TimeProvider timeProvider,
        ILogger<RateLimitNotificationProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(windowsNotificationSender);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        this.stateStore = stateStore;
        this.windowsNotificationSender = windowsNotificationSender;
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
        previousState = await RecoverInterruptedWindowsAttemptsAsync(
            previousState,
            snapshot.CapturedAtUtc,
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
            || evaluation.Candidates.Count == 0
            || !settings.WindowsNotificationEnabled)
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
                    previousState.RateLimitNotificationStates,
                    candidate);
                RateLimitNotificationState deferred = CreateState(
                    candidate,
                    DeliveryStatus.NotAttempted,
                    deliveredAtUtc: null,
                    deferredUntilUtc: quietHoursEnd) with
                {
                    WindowsAttemptCount = existing?.WindowsAttemptCount ?? 0,
                    WindowsLastAttemptedAtUtc = existing?.WindowsLastAttemptedAtUtc,
                    WindowsNextRetryAtUtc = existing?.WindowsNextRetryAtUtc,
                };
                currentState = await SaveNotificationStateAsync(deferred, cancellationToken);
                LogNotificationDeferred(
                    logger,
                    candidate.NotificationType,
                    candidate.NotificationStage,
                    quietHoursEnd.Value);
            }

            return new NotificationProcessingResult
            {
                State = currentState,
                DeferredUntilUtc = quietHoursEnd,
            };
        }

        List<RateLimitNotificationState> inProgressStates = [];
        foreach (RateLimitNotificationCandidate candidate in evaluation.Candidates)
        {
            LogResetCompletionReason(candidate);
            RateLimitNotificationState? existing = FindNotificationState(
                previousState.RateLimitNotificationStates,
                candidate);
            RateLimitNotificationState inProgress = CreateInProgressState(
                candidate,
                existing,
                timeProvider.GetUtcNow());
            await SaveNotificationStateAsync(inProgress, cancellationToken);
            inProgressStates.Add(inProgress);
        }

        WindowsNotificationMessage message = WindowsNotificationMessageFactory.CreateAggregate(
            evaluation.Candidates,
            snapshot.CapturedAtUtc);
        try
        {
            await windowsNotificationSender.SendAsync(message, cancellationToken);
            DateTimeOffset deliveredAtUtc = timeProvider.GetUtcNow();
            foreach ((RateLimitNotificationCandidate candidate, RateLimitNotificationState inProgress) in
                     evaluation.Candidates.Zip(inProgressStates))
            {
                RateLimitNotificationState succeeded = inProgress with
                {
                    WindowsDeliveryStatus = DeliveryStatus.Succeeded,
                    DeliveredAtUtc = deliveredAtUtc,
                };
                currentState = await SaveNotificationStateAsync(succeeded, cancellationToken);
                LogNotificationSucceeded(logger, candidate.NotificationType, candidate.NotificationStage);
            }

            currentState = await stateStore.UpdateAsync(
                state => state with
                {
                    LastNotifiedRecoveryWindowId = evaluation.Candidates[^1].RecoveryWindowId,
                    WindowsDeliveryResult = new DeliveryResultState
                    {
                        Status = DeliveryStatus.Succeeded,
                        AttemptedAtUtc = deliveredAtUtc,
                        Summary = CreateDeliverySummary(evaluation.Candidates),
                    },
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DateTimeOffset attemptedAtUtc = timeProvider.GetUtcNow();
            foreach ((RateLimitNotificationCandidate candidate, RateLimitNotificationState inProgress) in
                     evaluation.Candidates.Zip(inProgressStates))
            {
                RateLimitNotificationState failed = inProgress with
                {
                    WindowsDeliveryStatus = DeliveryStatus.Failed,
                    WindowsNextRetryAtUtc = attemptedAtUtc.Add(WindowsRetryDelay),
                };
                currentState = await SaveNotificationStateAsync(failed, cancellationToken);
                LogNotificationFailed(logger, candidate.NotificationType, candidate.NotificationStage, exception);
            }

            currentState = await stateStore.UpdateAsync(
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

        return new NotificationProcessingResult { State = currentState };
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
    private static string CreateDeliverySummary(IReadOnlyList<RateLimitNotificationCandidate> candidates)
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
}
