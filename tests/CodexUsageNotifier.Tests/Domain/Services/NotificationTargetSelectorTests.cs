using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Domain.Services;

namespace CodexUsageNotifier.Tests.Domain.Services;

/// <summary>
/// 将来の通知対象にする利用枠の自動・手動選択を検証します。
/// </summary>
[TestClass]
public sealed class NotificationTargetSelectorTests
{
    /// <summary>
    /// 自動選択では期間がより短い未知枠より300分枠を優先することを検証します。
    /// </summary>
    [TestMethod]
    public void Select_Automatic_PrefersThreeHundredMinuteWindow()
    {
        RateLimitWindow sixtyMinutes = CreateWindow("other", RateLimitPosition.Primary, 60);
        RateLimitWindow fiveHours = CreateWindow("codex", RateLimitPosition.Secondary, 300);

        RateLimitWindow? result = NotificationTargetSelector.Select(
            [sixtyMinutes, fiveHours],
            new NotificationTargetSelection());

        Assert.AreSame(fiveHours, result);
    }

    /// <summary>
    /// 300分枠がない場合は既知の期間が最も短い利用枠を選択することを検証します。
    /// </summary>
    [TestMethod]
    public void Select_AutomaticWithoutFiveHour_SelectsShortestDuration()
    {
        RateLimitWindow weekly = CreateWindow("codex", RateLimitPosition.Primary, 10080);
        RateLimitWindow daily = CreateWindow("future", RateLimitPosition.Secondary, 1440);

        RateLimitWindow? result = NotificationTargetSelector.Select(
            [weekly, daily],
            new NotificationTargetSelection());

        Assert.AreSame(daily, result);
    }

    /// <summary>
    /// 比較可能な正のウィンドウ長がない場合は自動選択しないことを検証します。
    /// </summary>
    [TestMethod]
    public void Select_AutomaticWithoutComparableDuration_ReturnsNull()
    {
        RateLimitWindow missing = CreateWindow("missing", RateLimitPosition.Primary, null);
        RateLimitWindow invalid = CreateWindow("invalid", RateLimitPosition.Secondary, 0);

        RateLimitWindow? result = NotificationTargetSelector.Select(
            [missing, invalid],
            new NotificationTargetSelection());

        Assert.IsNull(result);
    }

    /// <summary>
    /// 手動選択ではLimitId、位置、ウィンドウ長がすべて一致する枠だけを返すことを検証します。
    /// </summary>
    [TestMethod]
    public void Select_Manual_SelectsExactIdentity()
    {
        RateLimitWindow primary = CreateWindow("codex", RateLimitPosition.Primary, 10080);
        RateLimitWindow secondary = CreateWindow("codex", RateLimitPosition.Secondary, 10080);
        NotificationTargetSelection selection = new()
        {
            Mode = NotificationTargetSelectionMode.Manual,
            LimitId = "codex",
            Position = RateLimitPosition.Secondary,
            WindowDurationMinutes = 10080,
        };

        RateLimitWindow? result = NotificationTargetSelector.Select([primary, secondary], selection);

        Assert.AreSame(secondary, result);
    }

    /// <summary>
    /// テスト用の利用枠を生成します。
    /// </summary>
    /// <param name="limitId">利用枠識別子です。</param>
    /// <param name="position">レスポンス内の位置です。</param>
    /// <param name="durationMinutes">ウィンドウ長です。</param>
    /// <returns>テスト用利用枠です。</returns>
    private static RateLimitWindow CreateWindow(
        string limitId,
        RateLimitPosition position,
        int? durationMinutes)
    {
        return new RateLimitWindow
        {
            LimitId = limitId,
            Position = position,
            WindowDurationMinutes = durationMinutes,
            Classification = durationMinutes switch
            {
                300 => RateLimitClassification.FiveHour,
                10080 => RateLimitClassification.Weekly,
                _ => RateLimitClassification.Unknown,
            },
        };
    }
}
