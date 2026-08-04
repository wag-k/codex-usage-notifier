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
    /// 週間枠だけを観測した場合に5時間枠を未観測とし、全枠と自動選択対象を表示します。
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

        Assert.AreEqual("未観測", viewModel.FiveHourRateLimit);
        StringAssert.Contains(viewModel.WeeklyRateLimit, "残り 65%");
        StringAssert.Contains(viewModel.AllRateLimits, "Position=Primary");
        StringAssert.Contains(viewModel.AllRateLimits, "Classification=Weekly");
        StringAssert.Contains(viewModel.NotificationTarget, "Duration=10080分");
    }
}
