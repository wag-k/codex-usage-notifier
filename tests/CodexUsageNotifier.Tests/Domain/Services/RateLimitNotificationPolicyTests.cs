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
    /// 時刻進行だけでは通知せず、99%以下から実際に残量が増えた場合だけ完了を通知します。
    /// </summary>
    [TestMethod]
    [DataRow(100D, 100D, false)]
    [DataRow(99.5D, 100D, false)]
    [DataRow(99D, 99D, false)]
    [DataRow(99D, 98D, false)]
    [DataRow(99D, 100D, true)]
    [DataRow(98D, 99D, true)]
    public void Evaluate_WeeklyResetAdvance_RequiresRecoveryFromAtMost99Percent(
        double previousRemaining, double currentRemaining, bool expected)
    {
        RateLimitWindow previous = CreateWindow(
            "codex", RateLimitPosition.Primary, RateLimitClassification.Weekly,
            10080, previousRemaining, NowUtc.AddDays(7));
        RateLimitWindow current = CreateWindow(
            "codex", RateLimitPosition.Primary, RateLimitClassification.Weekly,
            10080, currentRemaining, NowUtc.AddDays(7).AddMinutes(1));

        RateLimitNotificationEvaluation result = Evaluate(
            [current], CreateSnapshot([previous], NowUtc.AddMinutes(-1)));

        Assert.AreEqual(expected, result.Candidates.Any(candidate =>
            candidate.NotificationType == RateLimitNotificationType.LongWindowResetCompleted));
    }

    /// <summary>
    /// 100%のまま予定時刻が毎回進んでも、取得ごとに新しい完了通知を作らないことを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_WeeklyFullRemainingWithRepeatedResetAdvance_DoesNotNotify()
    {
        UsageSnapshot? previous = null;
        for (int index = 0; index < 5; index++)
        {
            DateTimeOffset observedAt = NowUtc.AddHours(index);
            RateLimitWindow current = CreateWindow(
                "codex", RateLimitPosition.Primary, RateLimitClassification.Weekly,
                10080, 100, observedAt.AddDays(7));
            RateLimitNotificationEvaluation result = Evaluate(
                [current], previous, capturedAtUtc: observedAt);

            Assert.AreEqual(0, result.Candidates.Count);
            previous = CreateSnapshot([current], observedAt);
        }
    }

    /// <summary>
    /// FiveHour回復通知とWeekly早期通知を同じ取得から同時に候補化できることを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_FiveHourAndWeekly_ReturnsBothCandidates()
    {
        RateLimitWindow shortWindowBeforeRecovery = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.FiveHour,
            300,
            98,
            NowUtc.AddHours(5));
        RateLimitNotificationEvaluation beforeRecovery = Evaluate([shortWindowBeforeRecovery]);
        RateLimitWindow shortWindow = WithRemaining(shortWindowBeforeRecovery, 99);
        RateLimitWindow weeklyWindow = CreateWindow(
            "codex",
            RateLimitPosition.Secondary,
            RateLimitClassification.Weekly,
            10080,
            75,
            NowUtc.AddHours(47));

        RateLimitNotificationEvaluation result = Evaluate(
            [shortWindow, weeklyWindow],
            CreateSnapshot([shortWindowBeforeRecovery], NowUtc),
            recoveryStates: beforeRecovery.RecoveryStates,
            capturedAtUtc: NowUtc.AddMinutes(1));

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
    /// 初回観測から閾値以上の短期枠は回復通知を候補化しないことを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_NoResetShortWindow_InitiallyAboveDoesNotNotify()
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

        Assert.AreEqual(0, first.Candidates.Count);
        Assert.AreEqual(0, second.Candidates.Count);
        Assert.AreEqual(0, second.RecoveryStates.Single().RecoverySequence);
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

        Assert.AreEqual(1, third.RecoveryStates.Single().RecoverySequence);
        StringAssert.EndsWith(third.Candidates.Single().RecoveryWindowId, "recovery-sequence-1");
    }

    /// <summary>
    /// リセット時刻がある短期枠でも100%の初回観測と継続観測では通知しないことを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_ResetShortWindow_RemainingAtOneHundredDoesNotNotifyRepeatedly()
    {
        RateLimitWindow window = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.FiveHour,
            300,
            100,
            NowUtc.AddHours(5));
        RateLimitNotificationEvaluation first = Evaluate([window]);

        RateLimitNotificationEvaluation second = Evaluate(
            [window],
            CreateSnapshot([window], NowUtc),
            recoveryStates: first.RecoveryStates,
            capturedAtUtc: NowUtc.AddMinutes(1));

        Assert.AreEqual(0, first.Candidates.Count);
        Assert.AreEqual(0, second.Candidates.Count);
        Assert.AreEqual(0, second.RecoveryStates.Single().RecoverySequence);
    }

    /// <summary>
    /// リセット時刻がある短期枠でも1ポイント低い状態からの回復だけを通知することを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_ResetShortWindow_OnePointRecoveryCreatesCandidate()
    {
        RateLimitWindow full = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.FiveHour,
            300,
            100,
            NowUtc.AddHours(5));
        RateLimitNotificationEvaluation initial = Evaluate([full]);
        RateLimitWindow reduced = WithRemaining(full, 99);
        RateLimitNotificationEvaluation afterReduction = Evaluate(
            [reduced],
            CreateSnapshot([full], NowUtc),
            recoveryStates: initial.RecoveryStates,
            capturedAtUtc: NowUtc.AddMinutes(1));

        RateLimitNotificationEvaluation recovered = Evaluate(
            [full],
            CreateSnapshot([reduced], NowUtc.AddMinutes(1)),
            recoveryStates: afterReduction.RecoveryStates,
            capturedAtUtc: NowUtc.AddMinutes(2));

        Assert.AreEqual(0, afterReduction.Candidates.Count);
        Assert.AreEqual(RateLimitNotificationType.ShortWindowRecovered, recovered.Candidates.Single().NotificationType);
        Assert.AreEqual(1, recovered.RecoveryStates.Single().RecoverySequence);
    }

    /// <summary>
    /// 1ポイント未満の回復では通知せず、同じ低下基準から合計1ポイント以上回復した時点で通知することを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_ShortWindow_RecoveryBelowOnePointWaitsForEnoughIncrease()
    {
        RateLimitWindow initialWindow = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.FiveHour,
            300,
            100,
            resetsAtUtc: null);
        RateLimitNotificationEvaluation initial = Evaluate([initialWindow]);
        RateLimitWindow reduced = WithRemaining(initialWindow, 98.5);
        RateLimitNotificationEvaluation afterReduction = Evaluate(
            [reduced],
            CreateSnapshot([initialWindow], NowUtc),
            recoveryStates: initial.RecoveryStates,
            capturedAtUtc: NowUtc.AddMinutes(1));
        RateLimitWindow partialRecovery = WithRemaining(initialWindow, 99);
        RateLimitNotificationEvaluation partial = Evaluate(
            [partialRecovery],
            CreateSnapshot([reduced], NowUtc.AddMinutes(1)),
            recoveryStates: afterReduction.RecoveryStates,
            capturedAtUtc: NowUtc.AddMinutes(2));
        RateLimitWindow enoughRecovery = WithRemaining(initialWindow, 99.5);

        RateLimitNotificationEvaluation enough = Evaluate(
            [enoughRecovery],
            CreateSnapshot([partialRecovery], NowUtc.AddMinutes(2)),
            recoveryStates: partial.RecoveryStates,
            capturedAtUtc: NowUtc.AddMinutes(3));

        Assert.AreEqual(0, partial.Candidates.Count);
        Assert.AreEqual(RateLimitNotificationType.ShortWindowRecovered, enough.Candidates.Single().NotificationType);
        Assert.AreEqual(1, enough.RecoveryStates.Single().RecoverySequence);
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
    /// 実ログCase Aの予定時刻前リセットを時刻進行から最初の観測で検出できることを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_EarlyResetFromCaseA_ReturnsResetTimeAdvanced()
    {
        DateTimeOffset previousResetUtc = new(2026, 9, 3, 17, 8, 0, TimeSpan.Zero);
        DateTimeOffset capturedAtUtc = new(2026, 8, 29, 22, 1, 0, TimeSpan.Zero);
        RateLimitWindow previous = CreateWindow(
            "codex",
            RateLimitPosition.Secondary,
            RateLimitClassification.Weekly,
            10080,
            68,
            previousResetUtc);
        RateLimitWindow current = CreateWindow(
            "codex",
            RateLimitPosition.Secondary,
            RateLimitClassification.Weekly,
            10080,
            100,
            new DateTimeOffset(2026, 9, 5, 22, 1, 0, TimeSpan.Zero));

        RateLimitNotificationEvaluation result = Evaluate(
            [current],
            CreateSnapshot([previous], capturedAtUtc.AddHours(-1)),
            capturedAtUtc: capturedAtUtc);

        RateLimitNotificationCandidate candidate = result.Candidates.Single();
        Assert.AreEqual(RateLimitNotificationType.LongWindowResetCompleted, candidate.NotificationType);
        Assert.AreEqual(RateLimitResetCompletionReason.ResetTimeAdvanced, candidate.ResetCompletionReason);
    }

    /// <summary>
    /// 実ログCase Bで時刻進行と56ポイント低下が同時成立しても時刻進行理由へ正規化することを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_EarlyResetFromCaseB_PrioritizesResetTimeAdvanced()
    {
        DateTimeOffset capturedAtUtc = new(2026, 8, 27, 17, 0, 0, TimeSpan.Zero);
        RateLimitWindow previous = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.Weekly,
            10080,
            44,
            new DateTimeOffset(2026, 9, 1, 14, 20, 56, TimeSpan.Zero));
        RateLimitWindow current = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.Weekly,
            10080,
            100,
            new DateTimeOffset(2026, 9, 3, 17, 0, 38, TimeSpan.Zero));

        RateLimitNotificationEvaluation result = Evaluate(
            [current],
            CreateSnapshot([previous], capturedAtUtc.AddHours(-1)),
            capturedAtUtc: capturedAtUtc);

        Assert.AreEqual(
            RateLimitResetCompletionReason.ResetTimeAdvanced,
            result.Candidates.Single().ResetCompletionReason);
    }

    /// <summary>
    /// 予定時刻前でも使用率が閾値以上低下すれば時刻進行なしで推定できることを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_EarlyResetWithUsageDropOnly_ReturnsUsageDropInference()
    {
        DateTimeOffset capturedAtUtc = new(2026, 8, 27, 17, 0, 0, TimeSpan.Zero);
        DateTimeOffset resetUtc = new(2026, 9, 1, 14, 20, 56, TimeSpan.Zero);
        RateLimitWindow previous = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.Weekly,
            10080,
            44,
            resetUtc);
        RateLimitWindow current = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.Weekly,
            10080,
            100,
            resetUtc);

        RateLimitNotificationEvaluation result = Evaluate(
            [current],
            CreateSnapshot([previous], capturedAtUtc.AddHours(-1)),
            capturedAtUtc: capturedAtUtc);

        Assert.AreEqual(
            RateLimitResetCompletionReason.UsageDropInference,
            result.Candidates.Single().ResetCompletionReason);
    }

    /// <summary>
    /// 予定時刻前の小さな使用率低下と同一リセット時刻では完了候補にならないことを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_EarlySmallUsageDropWithSameReset_ReturnsNoCandidate()
    {
        DateTimeOffset capturedAtUtc = new(2026, 8, 27, 17, 0, 0, TimeSpan.Zero);
        DateTimeOffset resetUtc = new(2026, 9, 1, 14, 20, 56, TimeSpan.Zero);
        RateLimitWindow previous = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.Weekly,
            10080,
            60,
            resetUtc);
        RateLimitWindow current = WithRemaining(previous, 65);

        RateLimitNotificationEvaluation result = Evaluate(
            [current],
            CreateSnapshot([previous], capturedAtUtc.AddHours(-1)),
            capturedAtUtc: capturedAtUtc);

        Assert.AreEqual(0, result.Candidates.Count);
    }

    /// <summary>
    /// 秒単位のリセット時刻補正だけでは新しい利用期間と判定しないことを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_ResetTimeAdvancedBySecondsOnly_ReturnsNoCandidate()
    {
        DateTimeOffset capturedAtUtc = new(2026, 8, 27, 17, 0, 0, TimeSpan.Zero);
        RateLimitWindow previous = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.Weekly,
            10080,
            60,
            new DateTimeOffset(2026, 9, 1, 14, 20, 0, TimeSpan.Zero));
        RateLimitWindow current = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.Weekly,
            10080,
            60,
            new DateTimeOffset(2026, 9, 1, 14, 20, 30, TimeSpan.Zero));

        RateLimitNotificationEvaluation result = Evaluate(
            [current],
            CreateSnapshot([previous], capturedAtUtc.AddHours(-1)),
            capturedAtUtc: capturedAtUtc);

        Assert.AreEqual(0, result.Candidates.Count);
    }

    /// <summary>
    /// 初回観測が使用率0%でもリセット完了候補を生成しないことを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_FirstObservationAtZeroUsedPercent_ReturnsNoResetCompletedCandidate()
    {
        RateLimitWindow current = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.Weekly,
            10080,
            100,
            NowUtc.AddDays(7));

        RateLimitNotificationEvaluation result = Evaluate([current]);

        Assert.IsFalse(result.Candidates.Any(candidate =>
            candidate.NotificationType == RateLimitNotificationType.LongWindowResetCompleted));
    }

    /// <summary>
    /// 別Positionの前回枠を比較対象にせず早期リセット通知を生成しないことを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_PreviousWindowWithDifferentIdentity_ReturnsNoResetCompletedCandidate()
    {
        RateLimitWindow previous = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.Weekly,
            10080,
            50,
            NowUtc.AddDays(2));
        RateLimitWindow current = CreateWindow(
            "codex",
            RateLimitPosition.Secondary,
            RateLimitClassification.Weekly,
            10080,
            100,
            NowUtc.AddDays(7));

        RateLimitNotificationEvaluation result = Evaluate(
            [current],
            CreateSnapshot([previous], NowUtc.AddHours(-1)));

        Assert.IsFalse(result.Candidates.Any(candidate =>
            candidate.NotificationType == RateLimitNotificationType.LongWindowResetCompleted));
    }

    /// <summary>
    /// 同じ新期間の通知済み状態がある場合は早期リセット候補を重複生成しないことを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_EarlyResetAlreadyDelivered_ReturnsNoDuplicateCandidate()
    {
        DateTimeOffset capturedAtUtc = NowUtc.AddHours(-2);
        RateLimitWindow previous = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.Weekly,
            10080,
            68,
            NowUtc.AddDays(3));
        RateLimitWindow current = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.Weekly,
            10080,
            100,
            NowUtc.AddDays(5));
        RateLimitNotificationState delivered = new()
        {
            LimitId = "codex",
            Position = RateLimitPosition.Primary,
            WindowDurationMinutes = 10080,
            RecoveryWindowId = RateLimitNotificationPolicy.CreateRecoveryWindowId(current, capturedAtUtc),
            NotificationType = RateLimitNotificationType.LongWindowResetCompleted,
            NotificationStage = RateLimitNotificationStage.Completed,
            WindowsDeliveryStatus = DeliveryStatus.Succeeded,
        };

        RateLimitNotificationEvaluation result = Evaluate(
            [current],
            CreateSnapshot([previous], capturedAtUtc.AddHours(-1)),
            notificationStates: [delivered],
            capturedAtUtc: capturedAtUtc);

        Assert.AreEqual(0, result.Candidates.Count);
    }

    /// <summary>
    /// 再起動後に保存された新期間を再取得しても同じリセット完了候補を生成しないことを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_AfterRestartWithSameNewPeriod_ReturnsNoDuplicateCandidate()
    {
        RateLimitWindow current = CreateWindow(
            "codex",
            RateLimitPosition.Primary,
            RateLimitClassification.Weekly,
            10080,
            100,
            NowUtc.AddDays(7));
        UsageSnapshot savedSnapshot = CreateSnapshot([current], NowUtc.AddMinutes(-1));

        RateLimitNotificationEvaluation result = Evaluate(
            [current],
            savedSnapshot,
            capturedAtUtc: NowUtc);

        Assert.IsFalse(result.Candidates.Any(candidate =>
            candidate.NotificationType == RateLimitNotificationType.LongWindowResetCompleted));
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
    /// Weeklyの120時間、24時間、6時間の各段階を設定どおり判定できることを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_WeeklyWarningBands_ReturnExpectedStages()
    {
        RateLimitNotificationCandidate early = Evaluate([
            CreateWindow("early", RateLimitPosition.Primary, RateLimitClassification.Weekly, 10080, 75, NowUtc.AddHours(119))
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
    /// Final時間帯でFinalを無効にしてもStandardへフォールバックしないことを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_FinalBandWithFinalDisabled_DoesNotSendStandard()
    {
        AppSettings settings = AppSettings.CreateDefault() with
        {
            LongWindowFinalWarningEnabled = false,
        };

        RateLimitNotificationEvaluation result = Evaluate(
            [CreateWindow("codex", RateLimitPosition.Primary, RateLimitClassification.Weekly, 10080, 65, NowUtc.AddHours(5))],
            settings: settings);

        Assert.AreEqual(0, result.Candidates.Count);
    }

    /// <summary>
    /// Standard時間帯でStandardを無効にしてもEarlyへフォールバックしないことを検証します。
    /// </summary>
    [TestMethod]
    public void Evaluate_StandardBandWithStandardDisabled_DoesNotSendEarly()
    {
        AppSettings settings = AppSettings.CreateDefault() with
        {
            LongWindowStandardWarningEnabled = false,
        };

        RateLimitNotificationEvaluation result = Evaluate(
            [CreateWindow("codex", RateLimitPosition.Primary, RateLimitClassification.Weekly, 10080, 65, NowUtc.AddHours(20))],
            settings: settings);

        Assert.AreEqual(0, result.Candidates.Count);
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
            75,
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
