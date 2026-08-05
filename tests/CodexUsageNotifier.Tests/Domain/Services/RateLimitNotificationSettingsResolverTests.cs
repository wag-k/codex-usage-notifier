using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Domain.Services;

namespace CodexUsageNotifier.Tests.Domain.Services;

/// <summary>
/// 利用枠分類別の既定通知設定と識別値による上書きを検証します。
/// </summary>
[TestClass]
public sealed class RateLimitNotificationSettingsResolverTests
{
    /// <summary>
    /// FiveHourは短期回復通知だけが初期状態で有効になることを検証します。
    /// </summary>
    [TestMethod]
    public void Resolve_FiveHour_EnablesOnlyShortRecovery()
    {
        RateLimitNotificationSetting result = Resolve(RateLimitClassification.FiveHour, 300);

        Assert.IsTrue(result.ShortWindowRecoveryEnabled);
        Assert.IsFalse(result.LongWindowEarlyWarningEnabled);
        Assert.IsFalse(result.LongWindowResetCompletedEnabled);
    }

    /// <summary>
    /// Weeklyは3段階のリセット前通知とリセット完了通知が初期状態で有効になることを検証します。
    /// </summary>
    [TestMethod]
    public void Resolve_Weekly_EnablesAllLongWindowNotifications()
    {
        RateLimitNotificationSetting result = Resolve(RateLimitClassification.Weekly, 10080);

        Assert.IsFalse(result.ShortWindowRecoveryEnabled);
        Assert.IsTrue(result.LongWindowEarlyWarningEnabled);
        Assert.IsTrue(result.LongWindowStandardWarningEnabled);
        Assert.IsTrue(result.LongWindowFinalWarningEnabled);
        Assert.IsTrue(result.LongWindowResetCompletedEnabled);
    }

    /// <summary>
    /// Unknownは初期状態ですべての通知が無効になることを検証します。
    /// </summary>
    [TestMethod]
    public void Resolve_Unknown_DisablesAllNotifications()
    {
        RateLimitNotificationSetting result = Resolve(RateLimitClassification.Unknown, 1440);

        Assert.IsFalse(result.IsAnyEnabled);
    }

    /// <summary>
    /// LimitId、Position、WindowDurationMinutesが一致する保存設定を分類別既定値より優先することを検証します。
    /// </summary>
    [TestMethod]
    public void Resolve_ExactIdentity_UsesConfiguredSetting()
    {
        RateLimitWindow window = CreateWindow(RateLimitClassification.Unknown, 1440);
        RateLimitNotificationSetting configured = new()
        {
            LimitId = "codex",
            Position = RateLimitPosition.Primary,
            WindowDurationMinutes = 1440,
            ShortWindowRecoveryEnabled = true,
        };

        RateLimitNotificationSetting result = RateLimitNotificationSettingsResolver.Resolve(window, [configured]);

        Assert.AreSame(configured, result);
    }

    /// <summary>
    /// 指定分類の利用枠に適用される既定設定を解決します。
    /// </summary>
    private static RateLimitNotificationSetting Resolve(
        RateLimitClassification classification,
        int durationMinutes)
    {
        return RateLimitNotificationSettingsResolver.Resolve(
            CreateWindow(classification, durationMinutes),
            Array.Empty<RateLimitNotificationSetting>());
    }

    /// <summary>
    /// 指定分類と期間を持つテスト用利用枠を生成します。
    /// </summary>
    private static RateLimitWindow CreateWindow(
        RateLimitClassification classification,
        int durationMinutes)
    {
        return new RateLimitWindow
        {
            LimitId = "codex",
            Position = RateLimitPosition.Primary,
            Classification = classification,
            WindowDurationMinutes = durationMinutes,
        };
    }
}
