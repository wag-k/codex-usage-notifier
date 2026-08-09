using System.Globalization;
using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Domain.Services;

/// <summary>
/// 全利用枠の設定、残量遷移、リセット時刻、および保存済み状態から通知候補を判定します。
/// </summary>
public static class RateLimitNotificationPolicy
{
    private static readonly TimeSpan DeferredNotificationMaxAge = TimeSpan.FromHours(24);

    /// <summary>
    /// 1つのWindows通知に許可する最大表示試行回数です。
    /// </summary>
    internal const int MaxWindowsAttemptCount = 3;

    /// <summary>
    /// 1つのGmail通知に許可する最大送信試行回数です。
    /// </summary>
    internal const int MaxGmailAttemptCount = 2;

    /// <summary>
    /// 取得できた全利用枠を独立に評価し、複数の通知候補と回復状態を返します。
    /// </summary>
    /// <param name="currentSnapshot">現在取得した全利用枠です。</param>
    /// <param name="previousSnapshot">直前に保存した全利用枠です。</param>
    /// <param name="settings">閾値と利用枠別通知設定です。</param>
    /// <param name="notificationStates">保存済みの通知状態です。</param>
    /// <param name="recoveryStates">保存済みの利用枠別回復状態です。</param>
    /// <returns>送信候補と更新後の回復状態です。</returns>
    public static RateLimitNotificationEvaluation Evaluate(
        UsageSnapshot currentSnapshot,
        UsageSnapshot? previousSnapshot,
        AppSettings settings,
        IReadOnlyList<RateLimitNotificationState> notificationStates,
        IReadOnlyList<RateLimitRecoveryState> recoveryStates)
    {
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(notificationStates);
        ArgumentNullException.ThrowIfNull(recoveryStates);
        List<RateLimitNotificationCandidate> candidates = [];
        List<RateLimitRecoveryState> updatedRecoveryStates = recoveryStates.ToList();

        foreach (RateLimitWindow window in currentSnapshot.RateLimits.Where(HasIdentity))
        {
            RateLimitNotificationSetting windowSetting = RateLimitNotificationSettingsResolver.Resolve(
                window,
                settings);
            RateLimitRecoveryState? recoveryState = FindRecoveryState(updatedRecoveryStates, window);
            bool recoveryStarted = false;
            if (windowSetting.ShortWindowRecoveryEnabled
                || window.Classification == RateLimitClassification.FiveHour)
            {
                (RateLimitRecoveryState updatedState, bool started) = UpdateRecoveryState(
                    window,
                    recoveryState,
                    settings.ShortWindowRecoveryThresholdPercent);
                ReplaceRecoveryState(updatedRecoveryStates, updatedState);
                recoveryState = updatedState;
                recoveryStarted = started;
            }

            RateLimitNotificationCandidate? deferredShort = RestoreDeferredShortWindow(
                currentSnapshot,
                window,
                windowSetting,
                settings,
                recoveryState,
                notificationStates);
            if (deferredShort is not null)
            {
                candidates.Add(deferredShort);
            }
            else
            {
                AddIfPending(
                    candidates,
                    EvaluateShortWindow(
                        currentSnapshot,
                        window,
                        windowSetting,
                        recoveryState,
                        recoveryStarted,
                        settings),
                    notificationStates,
                    currentSnapshot.CapturedAtUtc,
                    settings);
            }

            RateLimitNotificationCandidate? deferredReset = RestoreDeferredResetCompleted(
                currentSnapshot,
                window,
                windowSetting,
                settings,
                notificationStates);
            AddIfPending(
                candidates,
                deferredReset ?? EvaluateLongWindow(
                    currentSnapshot,
                    previousSnapshot,
                    window,
                    FindMatchingWindow(previousSnapshot, window),
                    windowSetting,
                    settings),
                notificationStates,
                currentSnapshot.CapturedAtUtc,
                settings);
        }

        return new RateLimitNotificationEvaluation
        {
            Candidates = candidates,
            RecoveryStates = updatedRecoveryStates,
        };
    }

    /// <summary>
    /// 利用枠と取得時刻から、リセット時刻を持つ期間の識別子を生成します。
    /// </summary>
    /// <param name="window">識別する利用枠です。</param>
    /// <param name="capturedAtUtc">利用枠を取得したUTC時刻です。</param>
    /// <returns>リセット時刻を優先した期間識別子です。</returns>
    public static string CreateRecoveryWindowId(RateLimitWindow window, DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.ResetsAtUtc is not null)
        {
            return $"reset:{window.ResetsAtUtc.Value.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";
        }

        return $"observed:{capturedAtUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// リセット時刻がない短期枠の永続回復連番から期間識別子を生成します。
    /// </summary>
    /// <param name="window">識別する短期枠です。</param>
    /// <param name="recoverySequence">永続化された回復連番です。</param>
    /// <returns>利用枠識別値と回復連番を含む期間識別子です。</returns>
    public static string CreateNoResetRecoveryWindowId(RateLimitWindow window, int recoverySequence)
    {
        ArgumentNullException.ThrowIfNull(window);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"no-reset-time:{window.LimitId}:{window.Position.ToString().ToLowerInvariant()}:{window.WindowDurationMinutes}:recovery-sequence-{recoverySequence}");
    }

    /// <summary>
    /// 短期枠設定と回復遷移から回復通知候補を生成します。
    /// </summary>
    private static RateLimitNotificationCandidate? EvaluateShortWindow(
        UsageSnapshot snapshot,
        RateLimitWindow window,
        RateLimitNotificationSetting windowSetting,
        RateLimitRecoveryState? recoveryState,
        bool recoveryStarted,
        AppSettings settings)
    {
        if (!windowSetting.ShortWindowRecoveryEnabled
            || window.RemainingPercent < settings.ShortWindowRecoveryThresholdPercent)
        {
            return null;
        }

        string recoveryWindowId;
        if (window.ResetsAtUtc is not null)
        {
            recoveryWindowId = CreateRecoveryWindowId(window, snapshot.CapturedAtUtc);
        }
        else
        {
            if (!recoveryStarted || recoveryState is null)
            {
                return null;
            }

            recoveryWindowId = CreateNoResetRecoveryWindowId(window, recoveryState.RecoverySequence);
        }

        return CreateCandidate(
            snapshot,
            window,
            recoveryWindowId,
            RateLimitNotificationType.ShortWindowRecovered,
            RateLimitNotificationStage.Recovered);
    }

    /// <summary>
    /// 長期枠のリセット完了を優先し、該当しなければ有効なリセット前段階を判定します。
    /// </summary>
    private static RateLimitNotificationCandidate? EvaluateLongWindow(
        UsageSnapshot currentSnapshot,
        UsageSnapshot? previousSnapshot,
        RateLimitWindow window,
        RateLimitWindow? previousWindow,
        RateLimitNotificationSetting windowSetting,
        AppSettings settings)
    {
        RateLimitResetCompletionReason? reason = windowSetting.LongWindowResetCompletedEnabled
            ? GetResetCompletionReason(
                currentSnapshot,
                previousSnapshot,
                window,
                previousWindow,
                settings.ResetInferenceUsageDropPoints)
            : null;
        if (reason is not null)
        {
            string recoveryWindowId = window.ResetsAtUtc is not null
                ? CreateRecoveryWindowId(window, currentSnapshot.CapturedAtUtc)
                : CreateUsageDropEventId(window, currentSnapshot.CapturedAtUtc);
            return CreateCandidate(
                currentSnapshot,
                window,
                recoveryWindowId,
                RateLimitNotificationType.LongWindowResetCompleted,
                RateLimitNotificationStage.Completed,
                reason);
        }

        if (window.ResetsAtUtc is null)
        {
            return null;
        }

        TimeSpan remaining = window.ResetsAtUtc.Value - currentSnapshot.CapturedAtUtc;
        if (remaining <= TimeSpan.Zero)
        {
            return null;
        }

        if (remaining <= TimeSpan.FromHours(settings.LongWindowFinalWarningHours))
        {
            return windowSetting.LongWindowFinalWarningEnabled
                && window.RemainingPercent >= settings.LongWindowFinalWarningThresholdPercent
                    ? CreateCandidate(
                        currentSnapshot,
                        window,
                        CreateRecoveryWindowId(window, currentSnapshot.CapturedAtUtc),
                        RateLimitNotificationType.LongWindowFinalWarning,
                        RateLimitNotificationStage.Final)
                    : null;
        }

        if (remaining <= TimeSpan.FromHours(settings.LongWindowStandardWarningHours))
        {
            return windowSetting.LongWindowStandardWarningEnabled
                && window.RemainingPercent >= settings.LongWindowStandardWarningThresholdPercent
                    ? CreateCandidate(
                        currentSnapshot,
                        window,
                        CreateRecoveryWindowId(window, currentSnapshot.CapturedAtUtc),
                        RateLimitNotificationType.LongWindowStandardWarning,
                        RateLimitNotificationStage.Standard)
                    : null;
        }

        if (remaining <= TimeSpan.FromHours(settings.LongWindowEarlyWarningHours))
        {
            return windowSetting.LongWindowEarlyWarningEnabled
                && window.RemainingPercent >= settings.LongWindowEarlyWarningThresholdPercent
                    ? CreateCandidate(
                        currentSnapshot,
                        window,
                        CreateRecoveryWindowId(window, currentSnapshot.CapturedAtUtc),
                        RateLimitNotificationType.LongWindowEarlyWarning,
                        RateLimitNotificationStage.Early)
                    : null;
        }

        return null;
    }

    /// <summary>
    /// 前回と今回の正常取得値からリセット完了理由を判定します。
    /// </summary>
    /// <param name="currentSnapshot">現在の正常取得結果です。</param>
    /// <param name="previousSnapshot">直前の正常取得結果です。</param>
    /// <param name="currentWindow">現在の判定対象利用枠です。</param>
    /// <param name="previousWindow">直前の同一利用枠です。</param>
    /// <param name="usageDropPoints">使用率低下による推定に必要なポイント数です。</param>
    /// <returns>確認できたリセット完了理由、または判定不能時のnullです。</returns>
    private static RateLimitResetCompletionReason? GetResetCompletionReason(
        UsageSnapshot currentSnapshot,
        UsageSnapshot? previousSnapshot,
        RateLimitWindow currentWindow,
        RateLimitWindow? previousWindow,
        int usageDropPoints)
    {
        if (previousSnapshot is null || previousWindow is null)
        {
            return null;
        }

        if (previousWindow.ResetsAtUtc is not null
            && currentSnapshot.CapturedAtUtc < previousWindow.ResetsAtUtc.Value)
        {
            return null;
        }

        if (currentWindow.ResetsAtUtc is not null
            && previousWindow.ResetsAtUtc is not null
            && currentWindow.ResetsAtUtc.Value > previousWindow.ResetsAtUtc.Value)
        {
            return RateLimitResetCompletionReason.ResetTimeAdvanced;
        }

        return previousWindow.UsedPercent - currentWindow.UsedPercent >= usageDropPoints
            ? RateLimitResetCompletionReason.UsageDropInference
            : null;
    }

    /// <summary>
    /// 現在値から利用枠別回復状態と新しい回復の有無を計算します。
    /// </summary>
    private static (RateLimitRecoveryState State, bool RecoveryStarted) UpdateRecoveryState(
        RateLimitWindow window,
        RateLimitRecoveryState? previous,
        int thresholdPercent)
    {
        bool isBelowThreshold = window.RemainingPercent < thresholdPercent;
        bool recoveryStarted = !isBelowThreshold
            && (previous is null || !previous.HasObservation || previous.WasBelowThreshold);
        return (new RateLimitRecoveryState
        {
            LimitId = window.LimitId ?? string.Empty,
            Position = window.Position,
            WindowDurationMinutes = window.WindowDurationMinutes ?? 0,
            HasObservation = true,
            WasBelowThreshold = isBelowThreshold,
            RecoverySequence = (previous?.RecoverySequence ?? 0) + (recoveryStarted ? 1 : 0),
            LastRemainingPercent = window.RemainingPercent,
        }, recoveryStarted);
    }

    /// <summary>
    /// 禁止時間中に保留した短期回復通知を条件が継続している場合に復元します。
    /// </summary>
    private static RateLimitNotificationCandidate? RestoreDeferredShortWindow(
        UsageSnapshot snapshot,
        RateLimitWindow window,
        RateLimitNotificationSetting windowSetting,
        AppSettings settings,
        RateLimitRecoveryState? recoveryState,
        IReadOnlyList<RateLimitNotificationState> notificationStates)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!windowSetting.ShortWindowRecoveryEnabled
            || window.RemainingPercent < settings.ShortWindowRecoveryThresholdPercent)
        {
            return null;
        }

        string? expectedRecoveryWindowId = window.ResetsAtUtc is not null
            ? CreateRecoveryWindowId(window, snapshot.CapturedAtUtc)
            : recoveryState is null
                ? null
                : CreateNoResetRecoveryWindowId(window, recoveryState.RecoverySequence);
        return RestoreDeferred(
            window,
            notificationStates,
            RateLimitNotificationType.ShortWindowRecovered,
            snapshot.CapturedAtUtc,
            expectedRecoveryWindowId,
            settings);
    }

    /// <summary>
    /// 禁止時間中に保留したリセット完了通知を復元します。
    /// </summary>
    private static RateLimitNotificationCandidate? RestoreDeferredResetCompleted(
        UsageSnapshot snapshot,
        RateLimitWindow window,
        RateLimitNotificationSetting windowSetting,
        AppSettings settings,
        IReadOnlyList<RateLimitNotificationState> notificationStates)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string? expectedRecoveryWindowId = window.ResetsAtUtc is null
            ? null
            : CreateRecoveryWindowId(window, snapshot.CapturedAtUtc);
        return windowSetting.LongWindowResetCompletedEnabled
            ? RestoreDeferred(
                window,
                notificationStates,
                RateLimitNotificationType.LongWindowResetCompleted,
                snapshot.CapturedAtUtc,
                expectedRecoveryWindowId,
                settings)
            : null;
    }

    /// <summary>
    /// 指定種類の未送信状態を現在の利用枠候補へ復元します。
    /// </summary>
    private static RateLimitNotificationCandidate? RestoreDeferred(
        RateLimitWindow window,
        IReadOnlyList<RateLimitNotificationState> notificationStates,
        RateLimitNotificationType notificationType,
        DateTimeOffset nowUtc,
        string? expectedRecoveryWindowId,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        RateLimitNotificationState? pending = notificationStates
            .Where(state =>
                IsAnyEnabledChannelPending(state, settings, nowUtc)
                && state.NotificationType == notificationType
                && HasSameIdentity(state, window)
                && ((state.DeferredUntilUtc is not null
                        && state.DeferredUntilUtc <= nowUtc)
                    || (settings.GmailNotificationEnabled
                        && state.GmailDeliveryStatus == DeliveryStatus.Failed
                        && CanAttemptGmail(state, nowUtc)))
                && state.ConditionMetAtUtc >= nowUtc.Subtract(DeferredNotificationMaxAge)
                && (expectedRecoveryWindowId is null
                    || string.Equals(
                        state.RecoveryWindowId,
                        expectedRecoveryWindowId,
                        StringComparison.Ordinal)))
            .OrderByDescending(state => state.ConditionMetAtUtc)
            .FirstOrDefault();
        return pending is null
            ? null
            : new RateLimitNotificationCandidate
            {
                Window = window,
                RecoveryWindowId = pending.RecoveryWindowId,
                NotificationType = pending.NotificationType,
                NotificationStage = pending.NotificationStage,
                ConditionMetAtUtc = pending.ConditionMetAtUtc,
                ResetCompletionReason = pending.ResetCompletionReason,
            };
    }

    /// <summary>
    /// 候補が同じ複合キーで送信済みでなければ一覧へ追加します。
    /// </summary>
    private static void AddIfPending(
        List<RateLimitNotificationCandidate> candidates,
        RateLimitNotificationCandidate? candidate,
        IReadOnlyList<RateLimitNotificationState> notificationStates,
        DateTimeOffset nowUtc,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(notificationStates);
        ArgumentNullException.ThrowIfNull(settings);
        if (candidate is null)
        {
            return;
        }

        RateLimitNotificationState? existing = notificationStates.FirstOrDefault(state =>
            HasSameIdentity(state, candidate.Window)
            && string.Equals(state.RecoveryWindowId, candidate.RecoveryWindowId, StringComparison.Ordinal)
            && state.NotificationType == candidate.NotificationType
            && state.NotificationStage == candidate.NotificationStage);
        if ((settings.WindowsNotificationEnabled && CanAttemptWindows(existing, nowUtc))
            || (settings.GmailNotificationEnabled && CanAttemptGmail(existing, nowUtc)))
        {
            candidates.Add(candidate);
        }
    }

    /// <summary>
    /// 保存済みWindows配送状態から、新規送信または失敗後の再試行が可能か判定します。
    /// </summary>
    /// <param name="existing">同じ通知を表す保存済み状態です。</param>
    /// <param name="nowUtc">今回の正常取得UTC時刻です。</param>
    /// <returns>Windows通知を試行できる場合はtrueです。</returns>
    internal static bool CanAttemptWindows(
        RateLimitNotificationState? existing,
        DateTimeOffset nowUtc)
    {
        if (existing is null)
        {
            return true;
        }

        if (existing.WindowsDeliveryStatus == DeliveryStatus.NotAttempted)
        {
            return existing.DeferredUntilUtc is null || existing.DeferredUntilUtc <= nowUtc;
        }

        return existing.WindowsDeliveryStatus == DeliveryStatus.Failed
            && existing.WindowsAttemptCount < MaxWindowsAttemptCount
            && (existing.WindowsNextRetryAtUtc is null || existing.WindowsNextRetryAtUtc <= nowUtc);
    }

    /// <summary>
    /// 保存済みGmail配送状態から、初回送信または60分後の1回再試行が可能か判定します。
    /// </summary>
    /// <param name="existing">同じ通知を表す保存済み状態です。</param>
    /// <param name="nowUtc">今回の正常取得UTC時刻です。</param>
    /// <returns>Gmail通知を試行できる場合はtrueです。</returns>
    internal static bool CanAttemptGmail(
        RateLimitNotificationState? existing,
        DateTimeOffset nowUtc)
    {
        if (existing is null)
        {
            return true;
        }

        if (existing.GmailDeliveryStatus == DeliveryStatus.NotAttempted)
        {
            return existing.GmailAttemptCount < MaxGmailAttemptCount
                && (existing.DeferredUntilUtc is null || existing.DeferredUntilUtc <= nowUtc);
        }

        return existing.GmailDeliveryStatus == DeliveryStatus.Failed
            && existing.GmailAttemptCount == 1
            && existing.GmailFailureKind is GmailDeliveryFailureKind.Transient
                or GmailDeliveryFailureKind.Interrupted
            && existing.GmailNextRetryAtUtc is not null
            && existing.GmailNextRetryAtUtc <= nowUtc
            && (existing.DeferredUntilUtc is null || existing.DeferredUntilUtc <= nowUtc);
    }

    /// <summary>
    /// 有効な配送チャネルのいずれかに未送信または再試行可能な状態があるか判定します。
    /// </summary>
    /// <param name="state">保存済み通知状態です。</param>
    /// <param name="settings">有効な配送チャネル設定です。</param>
    /// <param name="nowUtc">今回の正常取得UTC時刻です。</param>
    /// <returns>いずれかの有効チャネルが処理可能ならtrueです。</returns>
    private static bool IsAnyEnabledChannelPending(
        RateLimitNotificationState state,
        AppSettings settings,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(settings);
        return (settings.WindowsNotificationEnabled && CanAttemptWindows(state, nowUtc))
            || (settings.GmailNotificationEnabled && CanAttemptGmail(state, nowUtc));
    }

    /// <summary>
    /// 指定利用枠に有効な識別値がそろっているか判定します。
    /// </summary>
    private static bool HasIdentity(RateLimitWindow window)
    {
        return !string.IsNullOrWhiteSpace(window.LimitId)
            && window.WindowDurationMinutes is > 0;
    }

    /// <summary>
    /// 直前スナップショットから同一利用枠を検索します。
    /// </summary>
    private static RateLimitWindow? FindMatchingWindow(UsageSnapshot? snapshot, RateLimitWindow window)
    {
        return snapshot?.RateLimits.FirstOrDefault(candidate =>
            string.Equals(candidate.LimitId, window.LimitId, StringComparison.Ordinal)
            && candidate.Position == window.Position
            && candidate.WindowDurationMinutes == window.WindowDurationMinutes);
    }

    /// <summary>
    /// 保存済み回復状態から同一利用枠を検索します。
    /// </summary>
    private static RateLimitRecoveryState? FindRecoveryState(
        IReadOnlyList<RateLimitRecoveryState> states,
        RateLimitWindow window)
    {
        return states.FirstOrDefault(state =>
            string.Equals(state.LimitId, window.LimitId, StringComparison.Ordinal)
            && state.Position == window.Position
            && state.WindowDurationMinutes == window.WindowDurationMinutes);
    }

    /// <summary>
    /// 同一利用枠の回復状態を置換し、未登録なら追加します。
    /// </summary>
    private static void ReplaceRecoveryState(
        List<RateLimitRecoveryState> states,
        RateLimitRecoveryState updated)
    {
        for (int index = 0; index < states.Count; index++)
        {
            RateLimitRecoveryState current = states[index];
            if (string.Equals(current.LimitId, updated.LimitId, StringComparison.Ordinal)
                && current.Position == updated.Position
                && current.WindowDurationMinutes == updated.WindowDurationMinutes)
            {
                states[index] = updated;
                return;
            }
        }

        states.Add(updated);
    }

    /// <summary>
    /// 通知状態と利用枠が同じ識別値を持つか判定します。
    /// </summary>
    private static bool HasSameIdentity(RateLimitNotificationState state, RateLimitWindow window)
    {
        return string.Equals(state.LimitId, window.LimitId, StringComparison.Ordinal)
            && state.Position == window.Position
            && state.WindowDurationMinutes == window.WindowDurationMinutes;
    }

    /// <summary>
    /// 使用率低下による推定イベントの識別子を生成します。
    /// </summary>
    private static string CreateUsageDropEventId(RateLimitWindow window, DateTimeOffset capturedAtUtc)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"no-reset-time:{window.LimitId}:{window.Position.ToString().ToLowerInvariant()}:{window.WindowDurationMinutes}:usage-drop-{capturedAtUtc.ToUnixTimeSeconds()}");
    }

    /// <summary>
    /// 現在の条件から通知候補を生成します。
    /// </summary>
    private static RateLimitNotificationCandidate CreateCandidate(
        UsageSnapshot snapshot,
        RateLimitWindow window,
        string recoveryWindowId,
        RateLimitNotificationType notificationType,
        RateLimitNotificationStage notificationStage,
        RateLimitResetCompletionReason? resetCompletionReason = null)
    {
        return new RateLimitNotificationCandidate
        {
            Window = window,
            RecoveryWindowId = recoveryWindowId,
            NotificationType = notificationType,
            NotificationStage = notificationStage,
            ConditionMetAtUtc = snapshot.CapturedAtUtc,
            ResetCompletionReason = resetCompletionReason,
        };
    }
}
