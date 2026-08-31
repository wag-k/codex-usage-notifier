using CodexUsageNotifier.Application.Notifications;
using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Tests.Application.Notifications;

/// <summary>
/// 配送時補助Contextが既存の週間枠選択と計算済み残量を利用することを検証します。
/// </summary>
[TestClass]
public sealed class RateLimitNotificationDisplayContextTests
{
    /// <summary>位置ではなくWeekly分類の候補から計算済み残量を取得することを検証します。</summary>
    [TestMethod]
    public void FromSnapshot_WeeklyCandidate_UsesExistingRemainingPercent()
    {
        UsageSnapshot snapshot = new()
        {
            RateLimits =
            [
                new RateLimitWindow
                {
                    LimitId = "codex",
                    Position = RateLimitPosition.Primary,
                    Classification = RateLimitClassification.Weekly,
                    WindowDurationMinutes = 10080,
                    UsedPercent = 37,
                    RemainingPercent = 63,
                },
            ],
        };

        RateLimitNotificationDisplayContext context =
            RateLimitNotificationDisplayContext.FromSnapshot(snapshot);

        Assert.AreEqual(63D, context.WeeklyRemainingPercent);
    }

    /// <summary>週間枠が存在しないSnapshotを未観測として扱うことを検証します。</summary>
    [TestMethod]
    public void FromSnapshot_WithoutWeeklyCandidate_ReturnsUnobserved()
    {
        UsageSnapshot snapshot = new()
        {
            RateLimits =
            [
                new RateLimitWindow
                {
                    Classification = RateLimitClassification.FiveHour,
                    WindowDurationMinutes = 300,
                    RemainingPercent = 99,
                },
            ],
        };

        RateLimitNotificationDisplayContext context =
            RateLimitNotificationDisplayContext.FromSnapshot(snapshot);

        Assert.IsNull(context.WeeklyRemainingPercent);
        Assert.AreEqual(
            "未観測",
            context.FormatWeeklyRemainingPercent(System.Globalization.CultureInfo.InvariantCulture));
    }
}
