using CodexUsageNotifier.Application.Ntfy;
using CodexUsageNotifier.Application.State;
using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Domain.Services;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier.Application.Notifications;

/// <summary>ntfyスマホ通知チャネルの境界、再試行、および配送を実装します。</summary>
public sealed partial class RateLimitNotificationProcessor
{
    private static readonly TimeSpan NtfyRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NtfyInProgressTimeout = TimeSpan.FromMinutes(5);

    [LoggerMessage(EventId = 3101, Level = LogLevel.Warning, Message = "スマホ通知Topicを読み込めませんでした。Topic値は記録していません。")]
    private static partial void LogNtfyTopicLoadFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3102, Level = LogLevel.Information, Message = "スマホ通知の新しい配送有効期間を開始しました。Topic値は記録していません。")]
    private static partial void LogNtfyBoundaryStarted(ILogger logger);

    [LoggerMessage(EventId = 3103, Level = LogLevel.Information, Message = "中断されたスマホ通知試行を再評価可能な状態へ回復しました。")]
    private static partial void LogNtfyInterruptedRecovered(ILogger logger);

    [LoggerMessage(EventId = 3104, Level = LogLevel.Information, Message = "意味を失ったスマホ通知の再試行を期限切れにしました。Count={Count}")]
    private static partial void LogNtfyExpired(ILogger logger, int count);

    [LoggerMessage(EventId = 3105, Level = LogLevel.Information, Message = "スマホ通知を送信しました。Count={Count}")]
    private static partial void LogNtfySucceeded(ILogger logger, int count);

    [LoggerMessage(EventId = 3106, Level = LogLevel.Warning, Message = "スマホ通知を送信できませんでした。Topicと本文は記録していません。Retry={Retry}")]
    private static partial void LogNtfyFailed(ILogger logger, bool retry, Exception exception);

    /// <summary>設定と秘密Topic世代の変化を、処理対象の取得時刻を基準にntfy配送境界へ同期します。</summary>
    private async Task<(ApplicationState State, NtfyTopic? Topic)> SynchronizeNtfyDeliveryBoundaryAsync(
        ApplicationState state,
        AppSettings settings,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(settings);
        NtfyTopic? topic = null;
        if (settings.NtfyNotificationEnabled && ntfyTopicStore is not null)
        {
            try
            {
                topic = await ntfyTopicStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogNtfyTopicLoadFailed(logger, exception);
            }
        }

        bool usable = settings.NtfyNotificationEnabled && topic is not null;
        bool boundaryChanged = usable
            && (!state.NtfyDeliveryEnabledLastObserved
                || !string.Equals(state.NtfyTopicGenerationId, topic!.GenerationId, StringComparison.Ordinal));
        if (!boundaryChanged && state.NtfyDeliveryEnabledLastObserved == usable)
        {
            return (state, topic);
        }

        ApplicationState updated = await stateStore.UpdateAsync(current => current with
        {
            NtfyDeliveryEnabledLastObserved = usable,
            NtfyDeliveryEnabledSinceUtc = boundaryChanged ? capturedAtUtc : current.NtfyDeliveryEnabledSinceUtc,
            NtfyTopicGenerationId = usable ? topic!.GenerationId : current.NtfyTopicGenerationId,
        }, cancellationToken);
        if (boundaryChanged)
        {
            LogNtfyBoundaryStarted(logger);
        }

        return (updated, topic);
    }

    /// <summary>古い送信中状態を試行回数を維持した再試行可能状態へ戻します。</summary>
    private async Task<ApplicationState> RecoverInterruptedNtfyAttemptsAsync(
        ApplicationState state,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        DateTimeOffset staleBefore = nowUtc.Subtract(NtfyInProgressTimeout);
        if (!state.RateLimitNotificationStates.Any(item => item.NtfyDeliveryStatus == DeliveryStatus.InProgress
            && (item.NtfyLastAttemptedAtUtc ?? item.ConditionMetAtUtc) <= staleBefore))
        {
            return state;
        }

        ApplicationState updated = await stateStore.UpdateAsync(current => current with
        {
            RateLimitNotificationStates = current.RateLimitNotificationStates.Select(item =>
            {
                DateTimeOffset attempted = item.NtfyLastAttemptedAtUtc ?? item.ConditionMetAtUtc;
                if (item.NtfyDeliveryStatus != DeliveryStatus.InProgress || attempted > staleBefore)
                {
                    return item;
                }

                bool canRetry = item.NtfyAttemptCount < RateLimitNotificationPolicy.MaxNtfyAttemptCount;
                return item with
                {
                    NtfyDeliveryStatus = DeliveryStatus.Failed,
                    NtfyFailureKind = NtfyDeliveryFailureKind.Interrupted,
                    NtfyNextRetryAtUtc = canRetry ? attempted.Add(NtfyRetryDelay) : null,
                };
            }).ToArray(),
        }, cancellationToken);
        LogNtfyInterruptedRecovered(logger);
        return updated;
    }

    /// <summary>現在のTopic、期間、残量に対して意味を失ったntfy再試行を期限切れにします。</summary>
    private async Task<ApplicationState> ExpireInvalidNtfyRetriesAsync(
        ApplicationState state,
        UsageSnapshot snapshot,
        AppSettings settings,
        NtfyTopic? topic,
        CancellationToken cancellationToken)
    {
        RateLimitNotificationState[] expired = state.RateLimitNotificationStates.Where(item =>
        {
            if (item.NtfyDeliveryStatus != DeliveryStatus.Failed)
            {
                return false;
            }

            bool tooOld = item.ConditionMetAtUtc < snapshot.CapturedAtUtc.Subtract(TimeSpan.FromHours(24));
            bool wrongTopic = topic is null || !string.Equals(item.NtfyTopicGenerationId, topic.GenerationId, StringComparison.Ordinal);
            GmailDeliveryFailureKind mapped = item.NtfyFailureKind switch
            {
                NtfyDeliveryFailureKind.Transient => GmailDeliveryFailureKind.Transient,
                NtfyDeliveryFailureKind.Interrupted => GmailDeliveryFailureKind.Interrupted,
                _ => GmailDeliveryFailureKind.Permanent,
            };
            return tooOld || wrongTopic || IsInvalidGmailRetry(item with
            {
                GmailDeliveryStatus = DeliveryStatus.Failed,
                GmailFailureKind = mapped,
            }, state, snapshot, settings);
        }).ToArray();
        if (expired.Length == 0)
        {
            return state;
        }

        ApplicationState updated = await stateStore.UpdateAsync(current => current with
        {
            RateLimitNotificationStates = current.RateLimitNotificationStates.Select(item =>
                expired.Any(target => HasSameIdentity(target, item))
                    ? item with { NtfyDeliveryStatus = DeliveryStatus.Expired, NtfyNextRetryAtUtc = null }
                    : item).ToArray(),
        }, cancellationToken);
        LogNtfyExpired(logger, expired.Length);
        return updated;
    }

    /// <summary>共通候補のntfy未送信分を1件へ集約し、Windows/Gmailとは独立に配送します。</summary>
    private async Task<ApplicationState> DeliverNtfyAsync(
        IReadOnlyList<RateLimitNotificationCandidate> candidates,
        ApplicationState currentState,
        UsageSnapshot snapshot,
        AppSettings settings,
        NtfyTopic? topic,
        CancellationToken cancellationToken)
    {
        if (!settings.NtfyNotificationEnabled || topic is null || ntfyNotificationSender is null
            || currentState.NtfyDeliveryEnabledSinceUtc is null)
        {
            return currentState;
        }

        List<RateLimitNotificationCandidate> selected = candidates.Where(candidate =>
        {
            RateLimitNotificationState? existing = FindNotificationState(currentState.RateLimitNotificationStates, candidate);
            return existing is not null
                && RateLimitNotificationPolicy.CanAttemptNtfy(existing, snapshot.CapturedAtUtc)
                && existing.ConditionMetAtUtc >= currentState.NtfyDeliveryEnabledSinceUtc
                && (existing.NtfyTopicGenerationId is null
                    || string.Equals(existing.NtfyTopicGenerationId, topic.GenerationId, StringComparison.Ordinal));
        }).ToList();
        if (selected.Count == 0)
        {
            return currentState;
        }

        DateTimeOffset attemptedAtUtc = timeProvider.GetUtcNow();
        List<RateLimitNotificationState> inProgress = [];
        foreach (RateLimitNotificationCandidate candidate in selected)
        {
            RateLimitNotificationState existing = FindNotificationState(currentState.RateLimitNotificationStates, candidate)
                ?? throw new InvalidOperationException("スマホ通知状態が保存されていません。");
            RateLimitNotificationState updated = existing with
            {
                NtfyDeliveryStatus = DeliveryStatus.InProgress,
                NtfyAttemptCount = existing.NtfyAttemptCount + 1,
                NtfyLastAttemptedAtUtc = attemptedAtUtc,
                NtfyNextRetryAtUtc = null,
                NtfyFailureKind = NtfyDeliveryFailureKind.None,
                NtfyTopicGenerationId = topic.GenerationId,
                DeferredUntilUtc = null,
            };
            currentState = await SaveNotificationStateAsync(updated, cancellationToken);
            inProgress.Add(updated);
        }

        NtfyNotificationMessage message = NtfyNotificationMessageFactory.CreateAggregate(
            selected, snapshot.CapturedAtUtc, timeProvider.LocalTimeZone, RateLimitNotificationDisplayContext.FromSnapshot(snapshot));
        try
        {
            await ntfyNotificationSender.SendAsync(topic, message, cancellationToken).ConfigureAwait(false);
            DateTimeOffset delivered = timeProvider.GetUtcNow();
            foreach (RateLimitNotificationState item in inProgress)
            {
                currentState = await SaveNotificationStateAsync(item with
                {
                    NtfyDeliveryStatus = DeliveryStatus.Succeeded,
                    NtfyFailureKind = NtfyDeliveryFailureKind.None,
                    DeliveredAtUtc = item.DeliveredAtUtc ?? delivered,
                }, cancellationToken);
            }

            LogNtfySucceeded(logger, selected.Count);
            return await stateStore.UpdateAsync(state => state with
            {
                NtfyDeliveryResult = new DeliveryResultState
                {
                    Status = DeliveryStatus.Succeeded,
                    AttemptedAtUtc = delivered,
                    Summary = CreateDeliverySummary(selected),
                },
            }, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            NtfyDeliveryFailureKind kind = exception is NtfyDeliveryException delivery
                ? delivery.FailureKind : NtfyDeliveryFailureKind.Transient;
            foreach (RateLimitNotificationState item in inProgress)
            {
                bool retry = kind == NtfyDeliveryFailureKind.Transient
                    && item.NtfyAttemptCount < RateLimitNotificationPolicy.MaxNtfyAttemptCount;
                currentState = await SaveNotificationStateAsync(item with
                {
                    NtfyDeliveryStatus = DeliveryStatus.Failed,
                    NtfyFailureKind = kind,
                    NtfyNextRetryAtUtc = retry ? attemptedAtUtc.Add(NtfyRetryDelay) : null,
                }, cancellationToken);
            }

            LogNtfyFailed(logger, kind == NtfyDeliveryFailureKind.Transient, exception);
            return await stateStore.UpdateAsync(state => state with
            {
                NtfyDeliveryResult = new DeliveryResultState
                {
                    Status = DeliveryStatus.Failed,
                    AttemptedAtUtc = attemptedAtUtc,
                    Summary = "スマホ通知を送信できませんでした。",
                },
            }, cancellationToken);
        }
    }
}
