using CodexUsageNotifier.Application.Notifications;
using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Tests.Application.Notifications;

/// <summary>
/// 通知種別ごとのWindows通知タイトルと主要な診断項目を検証します。
/// </summary>
[TestClass]
public sealed class WindowsNotificationMessageFactoryTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 8, 5, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 短期枠回復通知に残量、利用枠識別値、およびリセット時刻が含まれることを検証します。
    /// </summary>
    [TestMethod]
    public void Create_ShortWindowRecovered_ContainsWindowDetails()
    {
        RateLimitNotificationCandidate candidate = CreateCandidate(
            RateLimitClassification.FiveHour,
            300,
            RateLimitNotificationType.ShortWindowRecovered,
            RateLimitNotificationStage.Recovered);

        WindowsNotificationMessage result = WindowsNotificationMessageFactory.Create(candidate, NowUtc);

        Assert.AreEqual("Codexの短期利用枠が回復しました", result.Title);
        StringAssert.Contains(result.Body, "対象：5時間枠");
        StringAssert.Contains(result.Body, "残り使用量：65%");
        StringAssert.Contains(result.Body, "LimitId：codex");
        StringAssert.Contains(result.Body, "位置：Primary");
        StringAssert.Contains(result.Body, "次回リセット：");
    }

    /// <summary>
    /// 長期枠リセット前通知に段階、残り時間、およびバックログ案内が含まれることを検証します。
    /// </summary>
    [TestMethod]
    public void Create_LongWindowWarning_ContainsStageAndRemainingTime()
    {
        RateLimitNotificationCandidate candidate = CreateCandidate(
            RateLimitClassification.Weekly,
            10080,
            RateLimitNotificationType.LongWindowStandardWarning,
            RateLimitNotificationStage.Standard);

        WindowsNotificationMessage result = WindowsNotificationMessageFactory.Create(candidate, NowUtc);

        Assert.AreEqual("Codex週間枠のリセットが近づいています", result.Title);
        StringAssert.Contains(result.Body, "段階：Standard");
        StringAssert.Contains(result.Body, "リセットまで：約24時間");
        StringAssert.Contains(result.Body, "バックログを確認してください");
    }

    /// <summary>
    /// 長期枠リセット完了通知が新しい利用期間の開始を示すことを検証します。
    /// </summary>
    [TestMethod]
    public void Create_LongWindowResetCompleted_UsesResetCompletedTitle()
    {
        RateLimitNotificationCandidate candidate = CreateCandidate(
            RateLimitClassification.Weekly,
            10080,
            RateLimitNotificationType.LongWindowResetCompleted,
            RateLimitNotificationStage.Completed);

        WindowsNotificationMessage result = WindowsNotificationMessageFactory.Create(candidate, NowUtc);

        Assert.AreEqual("Codex長期枠の新しい利用期間が始まりました", result.Title);
        StringAssert.Contains(result.Body, "対象：週間枠");
    }

    /// <summary>
    /// 指定した分類と通知種別を持つテスト用候補を生成します。
    /// </summary>
    /// <param name="classification">利用枠分類です。</param>
    /// <param name="durationMinutes">利用枠期間です。</param>
    /// <param name="notificationType">通知種別です。</param>
    /// <param name="notificationStage">通知段階です。</param>
    /// <returns>Windows通知本文の生成に使用する候補です。</returns>
    private static RateLimitNotificationCandidate CreateCandidate(
        RateLimitClassification classification,
        int durationMinutes,
        RateLimitNotificationType notificationType,
        RateLimitNotificationStage notificationStage)
    {
        return new RateLimitNotificationCandidate
        {
            Window = new RateLimitWindow
            {
                LimitId = "codex",
                Position = RateLimitPosition.Primary,
                Classification = classification,
                WindowDurationMinutes = durationMinutes,
                UsedPercent = 35,
                RemainingPercent = 65,
                ResetsAtUtc = NowUtc.AddHours(24),
            },
            RecoveryWindowId = "reset:test",
            NotificationType = notificationType,
            NotificationStage = notificationStage,
            ConditionMetAtUtc = NowUtc,
        };
    }
}
