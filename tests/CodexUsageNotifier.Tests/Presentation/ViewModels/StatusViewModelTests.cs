using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Presentation.ViewModels;

namespace CodexUsageNotifier.Tests.Presentation.ViewModels;

/// <summary>
/// 状態画面の全利用枠表示と未観測表示を検証します。
/// </summary>
[TestClass]
public sealed class StatusViewModelTests
{
    /// <summary>
    /// 週間枠だけを観測した場合に5時間枠を未観測とし、全枠と有効な長期通知を表示します。
    /// </summary>
    [TestMethod]
    public void Initialize_WeeklyOnly_ShowsFiveHourAsUnobservedAndDisplaysAllWindows()
    {
        RateLimitWindow weekly = new()
        {
            LimitId = "codex",
            LimitName = "Codex",
            Position = RateLimitPosition.Primary,
            Classification = RateLimitClassification.Weekly,
            WindowDurationMinutes = 10080,
            UsedPercent = 35,
            RemainingPercent = 65,
            PlanType = "plus",
        };
        UsageSnapshot snapshot = new()
        {
            CapturedAtUtc = DateTimeOffset.UnixEpoch,
            RateLimits = [weekly],
        };
        ApplicationState state = new() { LastUsageSnapshot = snapshot };
        StatusViewModel viewModel = new();

        viewModel.Initialize(AppSettings.CreateDefault(), state);

        Assert.AreEqual("5時間枠：未観測", viewModel.FiveHourRateLimit);
        StringAssert.Contains(viewModel.WeeklyRateLimit, "残り 65%");
        StringAssert.Contains(viewModel.AllRateLimits, "Position=Primary");
        StringAssert.Contains(viewModel.AllRateLimits, "Classification=Weekly");
        StringAssert.Contains(viewModel.NotificationTarget, "Duration=10080分");
        StringAssert.Contains(viewModel.AllRateLimits, "通知設定=有効");
        StringAssert.Contains(viewModel.AllRateLimits, "有効通知=Early/Standard/Final/LongWindowResetCompleted");
        StringAssert.Contains(viewModel.AllRateLimits, "リセット時刻未取得");
        StringAssert.Contains(viewModel.AllRateLimits, "回復連番=0");
    }

    /// <summary>
    /// Unknown枠は表示対象に残しつつ初期状態では通知対象外と表示することを検証します。
    /// </summary>
    [TestMethod]
    public void Initialize_UnknownWindow_ShowsNotificationExcluded()
    {
        RateLimitWindow unknown = new()
        {
            LimitId = "future",
            Position = RateLimitPosition.Secondary,
            Classification = RateLimitClassification.Unknown,
            WindowDurationMinutes = 1440,
            UsedPercent = 10,
            RemainingPercent = 90,
        };
        StatusViewModel viewModel = new();

        viewModel.Initialize(
            AppSettings.CreateDefault(),
            new ApplicationState
            {
                LastUsageSnapshot = new UsageSnapshot
                {
                    CapturedAtUtc = DateTimeOffset.UnixEpoch,
                    RateLimits = [unknown],
                },
            });

        StringAssert.Contains(viewModel.AllRateLimits, "Classification=Unknown");
        StringAssert.Contains(viewModel.AllRateLimits, "通知設定=通知対象外");
        StringAssert.Contains(viewModel.NotificationTarget, "通知対象外");
    }
}
