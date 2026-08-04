using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Domain.Services;

namespace CodexUsageNotifier.Tests.Domain.Services;

/// <summary>
/// 日付をまたぐ通知禁止時間と終了時刻を検証します。
/// </summary>
[TestClass]
public sealed class QuietHoursScheduleTests
{
    /// <summary>
    /// 01:00は通知禁止時間内で、同日07:00に終了することを検証します。
    /// </summary>
    [TestMethod]
    public void GetQuietHoursEndUtc_AfterMidnight_ReturnsSameDaySeven()
    {
        DateTimeOffset nowUtc = new(2026, 8, 5, 1, 0, 0, TimeSpan.Zero);

        DateTimeOffset? result = QuietHoursSchedule.GetQuietHoursEndUtc(
            nowUtc,
            TimeZoneInfo.Utc,
            AppSettings.CreateDefault());

        Assert.AreEqual(new DateTimeOffset(2026, 8, 5, 7, 0, 0, TimeSpan.Zero), result);
    }

    /// <summary>
    /// 23:00から翌07:00までの日付をまたぐ設定を正しく扱うことを検証します。
    /// </summary>
    [TestMethod]
    public void GetQuietHoursEndUtc_BeforeMidnight_ReturnsNextDayEnd()
    {
        AppSettings settings = new()
        {
            QuietHoursStart = new TimeOnly(23, 0),
            QuietHoursEnd = new TimeOnly(7, 0),
        };
        DateTimeOffset nowUtc = new(2026, 8, 5, 23, 30, 0, TimeSpan.Zero);

        DateTimeOffset? result = QuietHoursSchedule.GetQuietHoursEndUtc(
            nowUtc,
            TimeZoneInfo.Utc,
            settings);

        Assert.AreEqual(new DateTimeOffset(2026, 8, 6, 7, 0, 0, TimeSpan.Zero), result);
    }
}
