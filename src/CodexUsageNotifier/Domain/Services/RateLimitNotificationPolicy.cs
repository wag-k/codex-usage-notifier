using System.Globalization;
using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Domain.Services;

/// <summary>
/// 利用枠の分類、残量、リセット時刻、および保存済み状態から通知候補を判定します。
/// </summary>
public static class RateLimitNotificationPolicy
{
    /// <summary>
    /// リセット完了の補助判定に使用する使用率低下幅です。
    /// </summary>
    public const double SignificantUsedPercentDrop = 50D;

    /// <summary>
    /// 現在選択された利用枠について、送信または保留できる通知候補を返します。
    /// </summary>
    /// <param name="currentSnapshot">現在取得した全利用枠です。</param>
    /// <param name="previousSnapshot">直前に保存した全利用枠です。</param>
    /// <param name="target">現在選択された通知対象です。</param>
    /// <param name="settings">通知設定です。</param>
    /// <param name="notificationStates">保存済みの通知状態です。</param>
    /// <returns>現在有効な通知候補です。候補がない場合はnullです。</returns>
    public static RateLimitNotificationCandidate? Evaluate(
        UsageSnapshot currentSnapshot,
        UsageSnapshot? previousSnapshot,
        RateLimitWindow? target,
        AppSettings settings,
        IReadOnlyList<RateLimitNotificationState> notificationStates)
    {
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(notificationStates);
        if (target?.WindowDurationMinutes is not > 0 || string.IsNullOrWhiteSpace(target.LimitId))
        {
            return null;
        }

        if (target.Classification == RateLimitClassification.Unknown
            && !settings.IncludeUnknownRateLimitsInNotifications)
        {
            return null;
        }

        RateLimitNotificationState? deferredResetCompleted = notificationStates
            .Where(state =>
                state.WindowsDeliveryStatus == DeliveryStatus.NotAttempted
                && state.NotificationType == RateLimitNotificationType.LongWindowResetCompleted
                && string.Equals(state.LimitId, target.LimitId, StringComparison.Ordinal)
                && state.Position == target.Position
                && state.WindowDurationMinutes == target.WindowDurationMinutes)
            .OrderByDescending(state => state.ConditionMetAtUtc)
            .FirstOrDefault();
        if (deferredResetCompleted is not null)
        {
            return new RateLimitNotificationCandidate
            {
                Window = target,
                RecoveryWindowId = deferredResetCompleted.RecoveryWindowId,
                NotificationType = deferredResetCompleted.NotificationType,
                NotificationStage = deferredResetCompleted.NotificationStage,
                ConditionMetAtUtc = deferredResetCompleted.ConditionMetAtUtc,
            };
        }

        RateLimitWindow? previousWindow = FindMatchingWindow(previousSnapshot, target);
        RateLimitNotificationCandidate? candidate = target.Classification switch
        {
            RateLimitClassification.Weekly => EvaluateLongWindow(
                currentSnapshot,
                previousSnapshot,
                target,
                previousWindow,
                settings),
            RateLimitClassification.FiveHour => EvaluateShortWindow(currentSnapshot, target, settings),
            _ => null,
        };

        if (candidate is null)
        {
            return null;
        }

        RateLimitNotificationState? existing = FindMatchingState(notificationStates, candidate);
        if (existing is null)
        {
            return candidate;
        }

        return existing.WindowsDeliveryStatus == DeliveryStatus.NotAttempted
            ? new RateLimitNotificationCandidate
            {
                Window = candidate.Window,
                RecoveryWindowId = candidate.RecoveryWindowId,
                NotificationType = candidate.NotificationType,
                NotificationStage = candidate.NotificationStage,
                ConditionMetAtUtc = existing.ConditionMetAtUtc,
            }
            : null;
    }

    /// <summary>
    /// 利用枠と取得時刻から、重複通知防止に使用するリセット期間IDを生成します。
    /// </summary>
    /// <param name="window">識別する利用枠です。</param>
    /// <param name="capturedAtUtc">利用枠を取得したUTC時刻です。</param>
    /// <returns>同じリセット期間で安定する識別子です。</returns>
    public static string CreateRecoveryWindowId(RateLimitWindow window, DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.ResetsAtUtc is not null)
        {
            return $"reset:{window.ResetsAtUtc.Value.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";
        }

        if (window.WindowDurationMinutes is > 0)
        {
            long durationSeconds = checked((long)window.WindowDurationMinutes.Value * 60L);
            long bucket = capturedAtUtc.ToUnixTimeSeconds() / durationSeconds;
            return $"duration:{window.WindowDurationMinutes.Value.ToString(CultureInfo.InvariantCulture)}:{bucket.ToString(CultureInfo.InvariantCulture)}";
        }

        return $"observed:{capturedAtUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// 短期枠が回復閾値以上なら回復通知候補を生成します。
    /// </summary>
    /// <param name="snapshot">現在の取得結果です。</param>
    /// <param name="target">通知対象の短期枠です。</param>
    /// <param name="settings">短期枠通知設定です。</param>
    /// <returns>短期枠回復通知候補、またはnullです。</returns>
    private static RateLimitNotificationCandidate? EvaluateShortWindow(
        UsageSnapshot snapshot,
        RateLimitWindow target,
        AppSettings settings)
    {
        if (!settings.ShortWindowRecoveryEnabled
            || target.RemainingPercent < settings.ShortWindowRecoveryThresholdPercent)
        {
            return null;
        }

        return CreateCandidate(
            snapshot,
            target,
            RateLimitNotificationType.ShortWindowRecovered,
            RateLimitNotificationStage.Recovered);
    }

    /// <summary>
    /// 長期枠のリセット完了を優先し、該当しなければ現在のリセット前段階を判定します。
    /// </summary>
    /// <param name="currentSnapshot">現在の取得結果です。</param>
    /// <param name="previousSnapshot">直前の取得結果です。</param>
    /// <param name="target">通知対象の長期枠です。</param>
    /// <param name="previousWindow">直前の同一利用枠です。</param>
    /// <param name="settings">長期枠通知設定です。</param>
    /// <returns>長期枠通知候補、またはnullです。</returns>
    private static RateLimitNotificationCandidate? EvaluateLongWindow(
        UsageSnapshot currentSnapshot,
        UsageSnapshot? previousSnapshot,
        RateLimitWindow target,
        RateLimitWindow? previousWindow,
        AppSettings settings)
    {
        if (settings.LongWindowResetCompletedNotificationEnabled
            && IsResetCompleted(currentSnapshot, previousSnapshot, target, previousWindow))
        {
            return new RateLimitNotificationCandidate
            {
                Window = target,
                RecoveryWindowId = CreateRecoveryWindowId(target, currentSnapshot.CapturedAtUtc),
                NotificationType = RateLimitNotificationType.LongWindowResetCompleted,
                NotificationStage = RateLimitNotificationStage.Completed,
                ConditionMetAtUtc = currentSnapshot.CapturedAtUtc,
            };
        }

        if (!settings.LongWindowPreResetNotificationEnabled || target.ResetsAtUtc is null)
        {
            return null;
        }

        TimeSpan remaining = target.ResetsAtUtc.Value - currentSnapshot.CapturedAtUtc;
        if (remaining <= TimeSpan.Zero)
        {
            return null;
        }

        if (remaining <= TimeSpan.FromHours(settings.LongWindowFinalWarningHours)
            && target.RemainingPercent >= settings.LongWindowFinalWarningThresholdPercent)
        {
            return CreateCandidate(
                currentSnapshot,
                target,
                RateLimitNotificationType.LongWindowFinalWarning,
                RateLimitNotificationStage.Final);
        }

        if (remaining <= TimeSpan.FromHours(settings.LongWindowStandardWarningHours)
            && target.RemainingPercent >= settings.LongWindowStandardWarningThresholdPercent)
        {
            return CreateCandidate(
                currentSnapshot,
                target,
                RateLimitNotificationType.LongWindowStandardWarning,
                RateLimitNotificationStage.Standard);
        }

        if (remaining <= TimeSpan.FromHours(settings.LongWindowEarlyWarningHours)
            && target.RemainingPercent >= settings.LongWindowEarlyWarningThresholdPercent)
        {
            return CreateCandidate(
                currentSnapshot,
                target,
                RateLimitNotificationType.LongWindowEarlyWarning,
                RateLimitNotificationStage.Early);
        }

        return null;
    }

    /// <summary>
    /// 予定時刻後の再取得で、新しい長期枠期間を確認できたか判定します。
    /// </summary>
    /// <param name="currentSnapshot">現在の取得結果です。</param>
    /// <param name="previousSnapshot">直前の取得結果です。</param>
    /// <param name="currentWindow">現在の長期枠です。</param>
    /// <param name="previousWindow">直前の長期枠です。</param>
    /// <returns>新しい期間を確認できた場合はtrueです。</returns>
    private static bool IsResetCompleted(
        UsageSnapshot currentSnapshot,
        UsageSnapshot? previousSnapshot,
        RateLimitWindow currentWindow,
        RateLimitWindow? previousWindow)
    {
        if (previousSnapshot is null || previousWindow is null)
        {
            return false;
        }

        bool expectedResetReached = previousWindow.ResetsAtUtc is null
            || currentSnapshot.CapturedAtUtc >= previousWindow.ResetsAtUtc.Value;
        if (!expectedResetReached)
        {
            return false;
        }

        bool resetTimeAdvanced = HasResetTimeAdvanced(currentWindow, previousWindow);
        bool usedPercentDropped = previousWindow.UsedPercent - currentWindow.UsedPercent
            >= SignificantUsedPercentDrop;
        string currentId = CreateRecoveryWindowId(currentWindow, currentSnapshot.CapturedAtUtc);
        string previousId = CreateRecoveryWindowId(previousWindow, previousSnapshot.CapturedAtUtc);
        return resetTimeAdvanced || usedPercentDropped || !string.Equals(currentId, previousId, StringComparison.Ordinal);
    }

    /// <summary>
    /// 現在のリセット時刻が直前より将来へ移動したか判定します。
    /// </summary>
    /// <param name="currentWindow">現在の利用枠です。</param>
    /// <param name="previousWindow">直前の利用枠です。</param>
    /// <returns>リセット時刻が進んだ場合はtrueです。</returns>
    private static bool HasResetTimeAdvanced(
        RateLimitWindow currentWindow,
        RateLimitWindow? previousWindow)
    {
        return currentWindow.ResetsAtUtc is not null
            && previousWindow?.ResetsAtUtc is not null
            && currentWindow.ResetsAtUtc.Value > previousWindow.ResetsAtUtc.Value;
    }

    /// <summary>
    /// 現在の条件から通知候補を生成します。
    /// </summary>
    /// <param name="snapshot">現在の取得結果です。</param>
    /// <param name="target">通知対象です。</param>
    /// <param name="notificationType">通知種別です。</param>
    /// <param name="notificationStage">通知段階です。</param>
    /// <returns>生成した通知候補です。</returns>
    private static RateLimitNotificationCandidate CreateCandidate(
        UsageSnapshot snapshot,
        RateLimitWindow target,
        RateLimitNotificationType notificationType,
        RateLimitNotificationStage notificationStage)
    {
        return new RateLimitNotificationCandidate
        {
            Window = target,
            RecoveryWindowId = CreateRecoveryWindowId(target, snapshot.CapturedAtUtc),
            NotificationType = notificationType,
            NotificationStage = notificationStage,
            ConditionMetAtUtc = snapshot.CapturedAtUtc,
        };
    }

    /// <summary>
    /// 直前スナップショットから同一識別値の利用枠を検索します。
    /// </summary>
    /// <param name="snapshot">検索する直前スナップショットです。</param>
    /// <param name="target">現在の通知対象です。</param>
    /// <returns>一致した直前の利用枠、またはnullです。</returns>
    private static RateLimitWindow? FindMatchingWindow(UsageSnapshot? snapshot, RateLimitWindow target)
    {
        return snapshot?.RateLimits.FirstOrDefault(window =>
            string.Equals(window.LimitId, target.LimitId, StringComparison.Ordinal)
            && window.Position == target.Position
            && window.WindowDurationMinutes == target.WindowDurationMinutes);
    }

    /// <summary>
    /// 保存済み状態から通知候補と同じ複合キーの状態を検索します。
    /// </summary>
    /// <param name="states">検索する通知状態です。</param>
    /// <param name="candidate">現在の通知候補です。</param>
    /// <returns>一致する保存済み状態、またはnullです。</returns>
    private static RateLimitNotificationState? FindMatchingState(
        IReadOnlyList<RateLimitNotificationState> states,
        RateLimitNotificationCandidate candidate)
    {
        return states.FirstOrDefault(state =>
            string.Equals(state.LimitId, candidate.Window.LimitId, StringComparison.Ordinal)
            && state.Position == candidate.Window.Position
            && state.WindowDurationMinutes == candidate.Window.WindowDurationMinutes
            && string.Equals(state.RecoveryWindowId, candidate.RecoveryWindowId, StringComparison.Ordinal)
            && state.NotificationType == candidate.NotificationType
            && state.NotificationStage == candidate.NotificationStage);
    }
}
