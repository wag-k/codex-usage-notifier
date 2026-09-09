using CodexUsageNotifier.Application.Notifications;
using CodexUsageNotifier.Application.Ntfy;
using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Tests.Application.Notifications;

/// <summary>スマートフォン向けntfy通知の重要情報と詳細を検証します。</summary>
[TestClass]
public sealed class NtfyNotificationMessageFactoryTests
{
    /// <summary>全5通知種別でタイトルに用途と残量、本文先頭に次回リセットが含まれることを検証します。</summary>
    [DataTestMethod]
    [DataRow(RateLimitNotificationType.ShortWindowRecovered, RateLimitNotificationStage.Recovered, "短期枠回復")]
    [DataRow(RateLimitNotificationType.LongWindowEarlyWarning, RateLimitNotificationStage.Early, "Early")]
    [DataRow(RateLimitNotificationType.LongWindowStandardWarning, RateLimitNotificationStage.Standard, "Standard")]
    [DataRow(RateLimitNotificationType.LongWindowFinalWarning, RateLimitNotificationStage.Final, "Final")]
    [DataRow(RateLimitNotificationType.LongWindowResetCompleted, RateLimitNotificationStage.Completed, "リセット完了")]
    public void CreateAggregate_AllProductionTypes_ContainsPriorityInformation(
        RateLimitNotificationType type,
        RateLimitNotificationStage stage,
        string titleFragment)
    {
        DateTimeOffset now = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        RateLimitNotificationCandidate candidate = new()
        {
            Window = new RateLimitWindow
            {
                LimitId = "codex", Position = RateLimitPosition.Primary,
                Classification = RateLimitClassification.Weekly, WindowDurationMinutes = 10080,
                RemainingPercent = 65, UsedPercent = 35, ResetsAtUtc = now.AddHours(24),
            },
            RecoveryWindowId = "period", NotificationType = type, NotificationStage = stage,
            ConditionMetAtUtc = now,
            ResetCompletionReason = type == RateLimitNotificationType.LongWindowResetCompleted
                ? RateLimitResetCompletionReason.ResetTimeAdvanced : null,
        };

        NtfyNotificationMessage message = NtfyNotificationMessageFactory.CreateAggregate(
            [candidate], now, TimeZoneInfo.Utc, new RateLimitNotificationDisplayContext { WeeklyRemainingPercent = 65 });

        StringAssert.Contains(message.Title, titleFragment);
        StringAssert.Contains(message.Title, "65%");
        StringAssert.StartsWith(message.Body, "次回リセット 2026/09/10 00:00");
        StringAssert.Contains(message.Body, "LimitId: codex");
        StringAssert.Contains(message.Body, "期間: 10080分");
        StringAssert.Contains(message.Body, "通知段階:");
        StringAssert.Contains(message.Body, "位置: プライマリ");
        StringAssert.Contains(message.Body, "分類: 週間枠");
    }

    /// <summary>複数候補でもタイトルだけで件数と週間枠残量を把握できることを検証します。</summary>
    [TestMethod]
    public void CreateAggregate_MultipleCandidates_PutsWeeklyRemainingInTitle()
    {
        DateTimeOffset now = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        RateLimitNotificationCandidate candidate = new()
        {
            Window = new RateLimitWindow
            {
                LimitId = "codex", Position = RateLimitPosition.Primary,
                Classification = RateLimitClassification.FiveHour, WindowDurationMinutes = 300,
                RemainingPercent = 99, UsedPercent = 1, ResetsAtUtc = now.AddHours(5),
            },
            RecoveryWindowId = "period", NotificationType = RateLimitNotificationType.ShortWindowRecovered,
            NotificationStage = RateLimitNotificationStage.Recovered, ConditionMetAtUtc = now,
        };

        RateLimitNotificationCandidate second = new()
        {
            Window = candidate.Window,
            RecoveryWindowId = "period-2",
            NotificationType = candidate.NotificationType,
            NotificationStage = candidate.NotificationStage,
            ConditionMetAtUtc = candidate.ConditionMetAtUtc,
        };
        NtfyNotificationMessage message = NtfyNotificationMessageFactory.CreateAggregate(
            [candidate, second], now, TimeZoneInfo.Utc,
            new RateLimitNotificationDisplayContext { WeeklyRemainingPercent = 23 });

        StringAssert.Contains(message.Title, "2件");
        StringAssert.Contains(message.Title, "週間残り23%");
    }
}
