using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Domain.Services;

namespace CodexUsageNotifier.Tests.Domain.Services;

/// <summary>
/// 短期枠回復、長期枠の段階通知、リセット完了、および重複防止を検証します。
/// </summary>
[TestClass]
public sealed class RateLimitNotificationPolicyTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 8, 5, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// FiveHourの残量が99%以上なら短期枠回復通知候補になることを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_FiveHourAtThreshold_ReturnsRecoveredCandidate()
    {
        RateLimitWindow window = CreateWindow(RateLimitClassification.FiveHour, 300, 99, NowUtc.AddHours(5));
        UsageSnapshot snapshot = CreateSnapshot(window, NowUtc);

        RateLimitNotificationCandidate? result = RateLimitNotificationPolicy.Evaluate(
            snapshot,
            previousSnapshot: null,
            window,
            AppSettings.CreateDefault(),
            Array.Empty<RateLimitNotificationState>());

        Assert.AreEqual(RateLimitNotificationType.ShortWindowRecovered, result?.NotificationType);
        Assert.AreEqual(RateLimitNotificationStage.Recovered, result?.NotificationStage);
    }

    /// <summary>
    /// Weeklyが48時間以内かつ残量50%以上なら早期通知候補になることを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_WeeklyWithinFortyEightHours_ReturnsEarlyCandidate()
    {
        RateLimitWindow window = CreateWindow(RateLimitClassification.Weekly, 10080, 50, NowUtc.AddHours(47));

        RateLimitNotificationCandidate? result = Evaluate(window);

        Assert.AreEqual(RateLimitNotificationType.LongWindowEarlyWarning, result?.NotificationType);
        Assert.AreEqual(RateLimitNotificationStage.Early, result?.NotificationStage);
    }

    /// <summary>
    /// Weeklyが24時間以内かつ残量20%以上なら通常通知候補になることを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_WeeklyWithinTwentyFourHours_ReturnsStandardCandidate()
    {
        RateLimitWindow window = CreateWindow(RateLimitClassification.Weekly, 10080, 20, NowUtc.AddHours(23));

        RateLimitNotificationCandidate? result = Evaluate(window);

        Assert.AreEqual(RateLimitNotificationType.LongWindowStandardWarning, result?.NotificationType);
        Assert.AreEqual(RateLimitNotificationStage.Standard, result?.NotificationStage);
    }

    /// <summary>
    /// Weeklyが6時間以内かつ残量10%以上なら最終通知候補になることを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_WeeklyWithinSixHours_ReturnsFinalCandidate()
    {
        RateLimitWindow window = CreateWindow(RateLimitClassification.Weekly, 10080, 10, NowUtc.AddHours(5));

        RateLimitNotificationCandidate? result = Evaluate(window);

        Assert.AreEqual(RateLimitNotificationType.LongWindowFinalWarning, result?.NotificationType);
        Assert.AreEqual(RateLimitNotificationStage.Final, result?.NotificationStage);
    }

    /// <summary>
    /// 同じ複合キーで送信済みの通知は再び候補にならないことを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_AlreadyDelivered_ReturnsNull()
    {
        RateLimitWindow window = CreateWindow(RateLimitClassification.Weekly, 10080, 65, NowUtc.AddHours(23));
        UsageSnapshot snapshot = CreateSnapshot(window, NowUtc);
        string recoveryWindowId = RateLimitNotificationPolicy.CreateRecoveryWindowId(window, NowUtc);
        RateLimitNotificationState delivered = CreateNotificationState(
            window,
            recoveryWindowId,
            RateLimitNotificationType.LongWindowStandardWarning,
            RateLimitNotificationStage.Standard,
            DeliveryStatus.Succeeded);

        RateLimitNotificationCandidate? result = RateLimitNotificationPolicy.Evaluate(
            snapshot,
            previousSnapshot: null,
            window,
            AppSettings.CreateDefault(),
            [delivered]);

        Assert.IsNull(result);
    }

    /// <summary>
    /// リセット予定時刻へ到達しただけではリセット完了候補にならないことを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_ResetTimeReachedWithoutChangedResponse_ReturnsNull()
    {
        DateTimeOffset resetAtUtc = NowUtc;
        RateLimitWindow previousWindow = CreateWindow(RateLimitClassification.Weekly, 10080, 40, resetAtUtc);
        RateLimitWindow currentWindow = CreateWindow(RateLimitClassification.Weekly, 10080, 40, resetAtUtc);
        UsageSnapshot previous = CreateSnapshot(previousWindow, NowUtc.AddMinutes(-1));
        UsageSnapshot current = CreateSnapshot(currentWindow, NowUtc.AddMinutes(1));

        RateLimitNotificationCandidate? result = RateLimitNotificationPolicy.Evaluate(
            current,
            previous,
            currentWindow,
            AppSettings.CreateDefault(),
            Array.Empty<RateLimitNotificationState>());

        Assert.IsNull(result);
    }

    /// <summary>
    /// リセット予定後の再取得でresetsAtが進んだ場合に完了候補になることを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_ResetTimeAdvanced_ReturnsResetCompletedCandidate()
    {
        RateLimitWindow previousWindow = CreateWindow(RateLimitClassification.Weekly, 10080, 40, NowUtc);
        RateLimitWindow currentWindow = CreateWindow(RateLimitClassification.Weekly, 10080, 1, NowUtc.AddDays(7));
        UsageSnapshot previous = CreateSnapshot(previousWindow, NowUtc.AddMinutes(-1));
        UsageSnapshot current = CreateSnapshot(currentWindow, NowUtc.AddMinutes(1));

        RateLimitNotificationCandidate? result = RateLimitNotificationPolicy.Evaluate(
            current,
            previous,
            currentWindow,
            AppSettings.CreateDefault(),
            Array.Empty<RateLimitNotificationState>());

        Assert.AreEqual(RateLimitNotificationType.LongWindowResetCompleted, result?.NotificationType);
        Assert.AreEqual(RateLimitNotificationStage.Completed, result?.NotificationStage);
    }

    /// <summary>
    /// リセット予定後に使用率が50ポイント以上低下した場合に完了候補になることを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_UsedPercentDroppedSignificantly_ReturnsResetCompletedCandidate()
    {
        RateLimitWindow previousWindow = CreateWindow(RateLimitClassification.Weekly, 10080, 20, NowUtc);
        previousWindow = WithPercent(previousWindow, usedPercent: 80, remainingPercent: 20);
        RateLimitWindow currentWindow = CreateWindow(RateLimitClassification.Weekly, 10080, 70, NowUtc);
        currentWindow = WithPercent(currentWindow, usedPercent: 30, remainingPercent: 70);
        UsageSnapshot previous = CreateSnapshot(previousWindow, NowUtc.AddMinutes(-1));
        UsageSnapshot current = CreateSnapshot(currentWindow, NowUtc.AddMinutes(1));

        RateLimitNotificationCandidate? result = RateLimitNotificationPolicy.Evaluate(
            current,
            previous,
            currentWindow,
            AppSettings.CreateDefault(),
            Array.Empty<RateLimitNotificationState>());

        Assert.AreEqual(RateLimitNotificationType.LongWindowResetCompleted, result?.NotificationType);
    }

    /// <summary>
    /// Unknown枠は既定設定では通知候補にならないことを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_UnknownWithDefaultSettings_ReturnsNull()
    {
        RateLimitWindow window = CreateWindow(RateLimitClassification.Unknown, 1440, 100, NowUtc.AddDays(1));

        RateLimitNotificationCandidate? result = Evaluate(window);

        Assert.IsNull(result);
    }

    /// <summary>
    /// 禁止時間中に保留したリセット完了通知を次の取得でも復元できることを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_DeferredResetCompleted_ReturnsPendingCandidate()
    {
        RateLimitWindow window = CreateWindow(RateLimitClassification.Weekly, 10080, 99, NowUtc.AddDays(7));
        UsageSnapshot current = CreateSnapshot(window, NowUtc.AddHours(1));
        string recoveryWindowId = RateLimitNotificationPolicy.CreateRecoveryWindowId(window, current.CapturedAtUtc);
        RateLimitNotificationState pending = CreateNotificationState(
            window,
            recoveryWindowId,
            RateLimitNotificationType.LongWindowResetCompleted,
            RateLimitNotificationStage.Completed,
            DeliveryStatus.NotAttempted);

        RateLimitNotificationCandidate? result = RateLimitNotificationPolicy.Evaluate(
            current,
            previousSnapshot: current,
            window,
            AppSettings.CreateDefault(),
            [pending]);

        Assert.AreEqual(RateLimitNotificationType.LongWindowResetCompleted, result?.NotificationType);
        Assert.AreEqual(recoveryWindowId, result?.RecoveryWindowId);
    }

    /// <summary>
    /// 1つの利用枠を既定設定で判定します。
    /// </summary>
    /// <param name="window">判定対象の利用枠です。</param>
    /// <returns>通知候補、またはnullです。</returns>
    private static RateLimitNotificationCandidate? Evaluate(RateLimitWindow window)
    {
        UsageSnapshot snapshot = CreateSnapshot(window, NowUtc);
        return RateLimitNotificationPolicy.Evaluate(
            snapshot,
            previousSnapshot: null,
            window,
            AppSettings.CreateDefault(),
            Array.Empty<RateLimitNotificationState>());
    }

    /// <summary>
    /// 指定条件のテスト用利用枠を生成します。
    /// </summary>
    /// <param name="classification">利用枠分類です。</param>
    /// <param name="durationMinutes">利用枠期間です。</param>
    /// <param name="remainingPercent">残量です。</param>
    /// <param name="resetsAtUtc">リセットUTC時刻です。</param>
    /// <returns>テスト用利用枠です。</returns>
    private static RateLimitWindow CreateWindow(
        RateLimitClassification classification,
        int durationMinutes,
        double remainingPercent,
        DateTimeOffset resetsAtUtc)
    {
        return new RateLimitWindow
        {
            LimitId = "codex",
            Position = RateLimitPosition.Primary,
            Classification = classification,
            WindowDurationMinutes = durationMinutes,
            UsedPercent = 100D - remainingPercent,
            RemainingPercent = remainingPercent,
            ResetsAtUtc = resetsAtUtc,
        };
    }

    /// <summary>
    /// 使用率と残量だけを変更した利用枠を生成します。
    /// </summary>
    /// <param name="window">変更元の利用枠です。</param>
    /// <param name="usedPercent">使用率です。</param>
    /// <param name="remainingPercent">残量です。</param>
    /// <returns>変更後の利用枠です。</returns>
    private static RateLimitWindow WithPercent(
        RateLimitWindow window,
        double usedPercent,
        double remainingPercent)
    {
        ArgumentNullException.ThrowIfNull(window);
        return new RateLimitWindow
        {
            LimitId = window.LimitId,
            Position = window.Position,
            Classification = window.Classification,
            WindowDurationMinutes = window.WindowDurationMinutes,
            UsedPercent = usedPercent,
            RemainingPercent = remainingPercent,
            ResetsAtUtc = window.ResetsAtUtc,
        };
    }

    /// <summary>
    /// 1つの利用枠を含むテスト用スナップショットを生成します。
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
    /// 指定複合キーを持つテスト用通知状態を生成します。
    /// </summary>
    /// <param name="window">通知対象です。</param>
    /// <param name="recoveryWindowId">リセット期間IDです。</param>
    /// <param name="notificationType">通知種別です。</param>
    /// <param name="notificationStage">通知段階です。</param>
    /// <param name="status">Windows送信状態です。</param>
    /// <returns>テスト用通知状態です。</returns>
    private static RateLimitNotificationState CreateNotificationState(
        RateLimitWindow window,
        string recoveryWindowId,
        RateLimitNotificationType notificationType,
        RateLimitNotificationStage notificationStage,
        DeliveryStatus status)
    {
        return new RateLimitNotificationState
        {
            LimitId = window.LimitId!,
            Position = window.Position,
            WindowDurationMinutes = window.WindowDurationMinutes!.Value,
            RecoveryWindowId = recoveryWindowId,
            NotificationType = notificationType,
            NotificationStage = notificationStage,
            ConditionMetAtUtc = NowUtc,
            WindowsDeliveryStatus = status,
        };
    }
}
