using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Domain.Services;

namespace CodexUsageNotifier.Tests.Domain.Services;

/// <summary>
/// 複数利用枠、回復遷移、長期通知段階、リセット完了、および重複防止を検証します。
/// </summary>
[TestClass]
public sealed class RateLimitNotificationPolicyTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 8, 5, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// FiveHour回復通知とWeekly早期通知を同じ取得から同時に候補化できることを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_FiveHourAndWeekly_ReturnsBothCandidates()
    {
        RateLimitWindow shortWindow = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.FiveHour,
            300,
            99,
            NowUtc.AddHours(5));
        RateLimitWindow weeklyWindow = CreateWindow(
            "codex",
            RateLimitPosition.Secondary,
            RateLimitClassification.Weekly,
            10080,
            50,
            NowUtc.AddHours(47));

        RateLimitNotificationEvaluation result = Evaluate([shortWindow, weeklyWindow]);

        Assert.AreEqual(2, result.Candidates.Count);
        CollectionAssert.AreEquivalent(
            new[]
            {
                RateLimitNotificationType.ShortWindowRecovered,
                RateLimitNotificationType.LongWindowEarlyWarning,
            },
            result.Candidates.Select(candidate => candidate.NotificationType).ToArray());
    }

    /// <summary>
    /// Unknown枠は上書き設定がない初期状態では通知候補にならないことを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_UnknownWithDefaultSettings_ReturnsNoCandidate()
    {
        RateLimitWindow window = CreateWindow(
            "future",
            RateLimitPosition.Primary,
            RateLimitClassification.Unknown,
            1440,
            100,
            NowUtc.AddDays(1));

        RateLimitNotificationEvaluation result = Evaluate([window]);

        Assert.AreEqual(0, result.Candidates.Count);
    }

    /// <summary>
    /// リセット時刻のない短期枠が閾値未満から以上へ遷移すると回復連番1の候補になることを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_NoResetShortWindow_CrossingThresholdCreatesRecovery()
    {
        RateLimitWindow below = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.FiveHour,
            300,
            98,
            resetsAtUtc: null);
        RateLimitNotificationEvaluation first = Evaluate([below]);
        RateLimitWindow recovered = WithRemaining(below, 99);

        RateLimitNotificationEvaluation second = Evaluate(
            [recovered],
            CreateSnapshot([below], NowUtc),
            recoveryStates: first.RecoveryStates,
            capturedAtUtc: NowUtc.AddMinutes(1));

        RateLimitNotificationCandidate candidate = second.Candidates.Single();
        Assert.AreEqual(RateLimitNotificationType.ShortWindowRecovered, candidate.NotificationType);
        StringAssert.EndsWith(candidate.RecoveryWindowId, "recovery-sequence-1");
        Assert.AreEqual(1, second.RecoveryStates.Single().RecoverySequence);
    }

    /// <summary>
    /// リセット時刻のない短期枠が閾値以上のままなら回復通知を重複候補化しないことを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_NoResetShortWindow_RemainingAboveDoesNotDuplicate()
    {
        RateLimitWindow window = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.FiveHour,
            300,
            99,
            resetsAtUtc: null);
        RateLimitNotificationEvaluation first = Evaluate([window]);

        RateLimitNotificationEvaluation second = Evaluate(
            [window],
            CreateSnapshot([window], NowUtc),
            recoveryStates: first.RecoveryStates,
            capturedAtUtc: NowUtc.AddMinutes(1));

        Assert.AreEqual(1, first.Candidates.Count);
        Assert.AreEqual(0, second.Candidates.Count);
        Assert.AreEqual(1, second.RecoveryStates.Single().RecoverySequence);
    }

    /// <summary>
    /// 回復後に一度閾値未満へ下がり再び閾値以上になると回復連番が増えることを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_NoResetShortWindow_SecondCrossingIncrementsSequence()
    {
        RateLimitWindow above = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.FiveHour,
            300,
            99,
            resetsAtUtc: null);
        RateLimitNotificationEvaluation first = Evaluate([above]);
        RateLimitWindow below = WithRemaining(above, 98);
        RateLimitNotificationEvaluation second = Evaluate(
            [below],
            CreateSnapshot([above], NowUtc),
            recoveryStates: first.RecoveryStates,
            capturedAtUtc: NowUtc.AddMinutes(1));
        RateLimitNotificationEvaluation third = Evaluate(
            [above],
            CreateSnapshot([below], NowUtc.AddMinutes(1)),
            recoveryStates: second.RecoveryStates,
            capturedAtUtc: NowUtc.AddMinutes(2));

        Assert.AreEqual(2, third.RecoveryStates.Single().RecoverySequence);
        StringAssert.EndsWith(third.Candidates.Single().RecoveryWindowId, "recovery-sequence-2");
    }

    /// <summary>
    /// リセット時刻のない長期枠ではEarly、Standard、Finalを候補にしないことを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_NoResetLongWindow_ReturnsNoPreResetWarning()
    {
        RateLimitWindow weekly = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.Weekly,
            10080,
            100,
            resetsAtUtc: null);

        RateLimitNotificationEvaluation result = Evaluate([weekly]);

        Assert.AreEqual(0, result.Candidates.Count);
    }

    /// <summary>
    /// リセット時刻のない長期枠で使用率が50ポイント低下すると推定完了候補になることを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_NoResetLongWindow_FiftyPointDropInfersReset()
    {
        RateLimitWindow previous = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.Weekly,
            10080,
            20,
            resetsAtUtc: null);
        RateLimitWindow current = WithRemaining(previous, 70);

        RateLimitNotificationEvaluation result = Evaluate(
            [current],
            CreateSnapshot([previous], NowUtc),
            capturedAtUtc: NowUtc.AddMinutes(1));

        RateLimitNotificationCandidate candidate = result.Candidates.Single();
        Assert.AreEqual(RateLimitNotificationType.LongWindowResetCompleted, candidate.NotificationType);
        Assert.AreEqual(RateLimitResetCompletionReason.UsageDropInference, candidate.ResetCompletionReason);
    }

    /// <summary>
    /// リセット時刻のない長期枠で使用率低下が49ポイント以下なら完了候補にならないことを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_NoResetLongWindow_FortyNinePointDropDoesNotInferReset()
    {
        RateLimitWindow previous = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.Weekly,
            10080,
            20,
            resetsAtUtc: null);
        RateLimitWindow current = WithRemaining(previous, 69);

        RateLimitNotificationEvaluation result = Evaluate(
            [current],
            CreateSnapshot([previous], NowUtc),
            capturedAtUtc: NowUtc.AddMinutes(1));

        Assert.AreEqual(0, result.Candidates.Count);
    }

    /// <summary>
    /// 設定モデルの使用率低下推定閾値がリセット完了判定へ使用されることを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_CustomUsageDropThreshold_UsesConfiguredPoints()
    {
        RateLimitWindow previous = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.Weekly,
            10080,
            20,
            resetsAtUtc: null);
        RateLimitWindow current = WithRemaining(previous, 60);

        RateLimitNotificationEvaluation result = Evaluate(
            [current],
            CreateSnapshot([previous], NowUtc),
            capturedAtUtc: NowUtc.AddMinutes(1),
            settings: AppSettings.CreateDefault() with { ResetInferenceUsageDropPoints = 40 });

        Assert.AreEqual(
            RateLimitResetCompletionReason.UsageDropInference,
            result.Candidates.Single().ResetCompletionReason);
    }

    /// <summary>
    /// リセット時刻が進んだ場合はResetTimeAdvanced理由の完了候補になることを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_ResetTimeAdvanced_RecordsReason()
    {
        RateLimitWindow previous = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.Weekly,
            10080,
            60,
            NowUtc);
        RateLimitWindow current = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.Weekly,
            10080,
            99,
            NowUtc.AddDays(7));

        RateLimitNotificationEvaluation result = Evaluate(
            [current],
            CreateSnapshot([previous], NowUtc.AddMinutes(-1)),
            capturedAtUtc: NowUtc.AddMinutes(1));

        Assert.AreEqual(
            RateLimitResetCompletionReason.ResetTimeAdvanced,
            result.Candidates.Single().ResetCompletionReason);
    }

    /// <summary>
    /// リセット予定時刻へ到達しただけではリセット完了候補にならないことを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_ResetTimeReachedWithoutChangedResponse_ReturnsNoCandidate()
    {
        RateLimitWindow previous = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.Weekly,
            10080,
            60,
            NowUtc);

        RateLimitNotificationEvaluation result = Evaluate(
            [previous],
            CreateSnapshot([previous], NowUtc.AddMinutes(-1)),
            capturedAtUtc: NowUtc.AddMinutes(1));

        Assert.AreEqual(0, result.Candidates.Count);
    }

    /// <summary>
    /// Weeklyの48時間、24時間、6時間の各段階を設定どおり判定できることを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_WeeklyWarningBands_ReturnExpectedStages()
    {
        RateLimitNotificationCandidate early = Evaluate([
            CreateWindow("early", RateLimitPosition.Primary, RateLimitClassification.Weekly, 10080, 50, NowUtc.AddHours(47))
        ]).Candidates.Single();
        RateLimitNotificationCandidate standard = Evaluate([
            CreateWindow("standard", RateLimitPosition.Primary, RateLimitClassification.Weekly, 10080, 20, NowUtc.AddHours(23))
        ]).Candidates.Single();
        RateLimitNotificationCandidate final = Evaluate([
            CreateWindow("final", RateLimitPosition.Primary, RateLimitClassification.Weekly, 10080, 10, NowUtc.AddHours(5))
        ]).Candidates.Single();

        Assert.AreEqual(RateLimitNotificationStage.Early, early.NotificationStage);
        Assert.AreEqual(RateLimitNotificationStage.Standard, standard.NotificationStage);
        Assert.AreEqual(RateLimitNotificationStage.Final, final.NotificationStage);
    }

    /// <summary>
    /// 1つの利用枠で送信済み状態があっても別利用枠の通知候補を妨げないことを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_DeliveredStateForOneWindow_DoesNotAffectOtherWindow()
    {
        RateLimitWindow shortWindow = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.FiveHour,
            300,
            99,
            NowUtc.AddHours(5));
        RateLimitWindow weeklyWindow = CreateWindow(
            "codex",
            RateLimitPosition.Secondary,
            RateLimitClassification.Weekly,
            10080,
            50,
            NowUtc.AddHours(47));
        RateLimitNotificationState delivered = new()
        {
            LimitId = "codex",
            Position = RateLimitPosition.Primary,
            WindowDurationMinutes = 300,
            RecoveryWindowId = RateLimitNotificationPolicy.CreateRecoveryWindowId(shortWindow, NowUtc),
            NotificationType = RateLimitNotificationType.ShortWindowRecovered,
            NotificationStage = RateLimitNotificationStage.Recovered,
            WindowsDeliveryStatus = DeliveryStatus.Succeeded,
        };

        RateLimitNotificationEvaluation result = Evaluate(
            [shortWindow, weeklyWindow],
            notificationStates: [delivered]);

        Assert.AreEqual(1, result.Candidates.Count);
        Assert.AreEqual(RateLimitNotificationType.LongWindowEarlyWarning, result.Candidates.Single().NotificationType);
        Assert.AreEqual(RateLimitPosition.Secondary, result.Candidates.Single().Window.Position);
    }

    /// <summary>
    /// 指定した現在値と保存状態を通知ポリシーで評価します。
    /// </summary>
    private static RateLimitNotificationEvaluation Evaluate(
        IReadOnlyList<RateLimitWindow> windows,
        UsageSnapshot? previousSnapshot = null,
        IReadOnlyList<RateLimitNotificationState>? notificationStates = null,
        IReadOnlyList<RateLimitRecoveryState>? recoveryStates = null,
        DateTimeOffset? capturedAtUtc = null,
        AppSettings? settings = null)
    {
        return RateLimitNotificationPolicy.Evaluate(
            CreateSnapshot(windows, capturedAtUtc ?? NowUtc),
            previousSnapshot,
            settings ?? AppSettings.CreateDefault(),
            notificationStates ?? Array.Empty<RateLimitNotificationState>(),
            recoveryStates ?? Array.Empty<RateLimitRecoveryState>());
    }

    /// <summary>
    /// 指定条件のテスト用利用枠を生成します。
    /// </summary>
    private static RateLimitWindow CreateWindow(
        string limitId,
        RateLimitPosition position,
        RateLimitClassification classification,
        int durationMinutes,
        double remainingPercent,
        DateTimeOffset? resetsAtUtc)
    {
        return new RateLimitWindow
        {
            LimitId = limitId,
            Position = position,
            Classification = classification,
            WindowDurationMinutes = durationMinutes,
            UsedPercent = 100D - remainingPercent,
            RemainingPercent = remainingPercent,
            ResetsAtUtc = resetsAtUtc,
        };
    }

    /// <summary>
    /// 利用枠の残量と使用率だけを変更したコピーを生成します。
    /// </summary>
    private static RateLimitWindow WithRemaining(RateLimitWindow window, double remainingPercent)
    {
        return new RateLimitWindow
        {
            LimitId = window.LimitId,
            LimitName = window.LimitName,
            Position = window.Position,
            Classification = window.Classification,
            WindowDurationMinutes = window.WindowDurationMinutes,
            UsedPercent = 100D - remainingPercent,
            RemainingPercent = remainingPercent,
            ResetsAtUtc = window.ResetsAtUtc,
            PlanType = window.PlanType,
            RateLimitReachedType = window.RateLimitReachedType,
        };
    }

    /// <summary>
    /// 指定利用枠を含むテスト用スナップショットを生成します。
    /// </summary>
    private static UsageSnapshot CreateSnapshot(
        IReadOnlyList<RateLimitWindow> windows,
        DateTimeOffset capturedAtUtc)
    {
        return new UsageSnapshot
        {
            CapturedAtUtc = capturedAtUtc,
            RateLimits = windows,
        };
    }
}
