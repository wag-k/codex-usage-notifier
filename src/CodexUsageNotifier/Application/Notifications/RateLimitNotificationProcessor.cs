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
                RateLimitNotificationState deferred = CreateState(
                    candidate,
                    DeliveryStatus.NotAttempted,
                    deliveredAtUtc: null,
                    deferredUntilUtc: quietHoursEnd);
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

        foreach (RateLimitNotificationCandidate candidate in evaluation.Candidates)
        {
            LogResetCompletionReason(candidate);
            RateLimitNotificationState inProgress = CreateState(
                candidate,
                DeliveryStatus.InProgress,
                deliveredAtUtc: null,
                deferredUntilUtc: null);
            await SaveNotificationStateAsync(inProgress, cancellationToken);
            WindowsNotificationMessage message = WindowsNotificationMessageFactory.Create(
                candidate,
                snapshot.CapturedAtUtc);
            try
            {
                await windowsNotificationSender.SendAsync(message, cancellationToken);
                RateLimitNotificationState succeeded = inProgress with
                {
                    WindowsDeliveryStatus = DeliveryStatus.Succeeded,
                    DeliveredAtUtc = timeProvider.GetUtcNow(),
                };
                currentState = await SaveNotificationStateAsync(succeeded, cancellationToken);
                currentState = await stateStore.UpdateAsync(
                    state => state with
                    {
                        LastNotifiedRecoveryWindowId = candidate.RecoveryWindowId,
                        WindowsDeliveryResult = new DeliveryResultState
                        {
                            Status = DeliveryStatus.Succeeded,
                            AttemptedAtUtc = succeeded.DeliveredAtUtc,
                            Summary = $"{candidate.NotificationType}/{candidate.NotificationStage}",
                        },
                    },
                    cancellationToken);
                LogNotificationSucceeded(logger, candidate.NotificationType, candidate.NotificationStage);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                DateTimeOffset attemptedAtUtc = timeProvider.GetUtcNow();
                RateLimitNotificationState failed = inProgress with
                {
                    WindowsDeliveryStatus = DeliveryStatus.Failed,
                };
                currentState = await SaveNotificationStateAsync(failed, cancellationToken);
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
                LogNotificationFailed(logger, candidate.NotificationType, candidate.NotificationStage, exception);
            }
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
}
