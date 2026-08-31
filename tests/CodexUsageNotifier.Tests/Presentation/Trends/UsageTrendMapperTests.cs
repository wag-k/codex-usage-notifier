using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Presentation.Trends;

namespace CodexUsageNotifier.Tests.Presentation.Trends;

/// <summary>
/// 既存履歴から使用率推移系列への変換と期間抽出を検証します。
/// </summary>
[TestClass]
public sealed class UsageTrendMapperTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    /// <summary>使用率0%と100%を補正せず表示系列へ保持することを検証します。</summary>
    [TestMethod]
    [DataRow(0D)]
    [DataRow(100D)]
    public void Map_BoundaryUsedPercent_PreservesValue(double usedPercent)
    {
        UsageTrendHistory result = UsageTrendMapper.Map(
            [CreateEntry(NowUtc, CreateObservation(RateLimitClassification.FiveHour, usedPercent))],
            TimeZoneInfo.Utc);

        Assert.AreEqual(usedPercent, result.FiveHourPoints.Single().UsedPercent);
        Assert.AreEqual(100D - usedPercent, result.FiveHourPoints.Single().RemainingPercent);
    }

    /// <summary>FiveHourだけの履歴でWeeklyを0%補完しないことを検証します。</summary>
    [TestMethod]
    public void Map_FiveHourOnly_DoesNotCreateWeeklyPoint()
    {
        UsageTrendHistory result = UsageTrendMapper.Map(
            [CreateEntry(NowUtc, CreateObservation(RateLimitClassification.FiveHour, 42))],
            TimeZoneInfo.Utc);

        Assert.AreEqual(1, result.FiveHourPoints.Count);
        Assert.AreEqual(0, result.WeeklyPoints.Count);
    }

    /// <summary>Weeklyだけの履歴でFiveHourを0%補完しないことを検証します。</summary>
    [TestMethod]
    public void Map_WeeklyOnly_DoesNotCreateFiveHourPoint()
    {
        UsageTrendHistory result = UsageTrendMapper.Map(
            [CreateEntry(NowUtc, CreateObservation(RateLimitClassification.Weekly, 68))],
            TimeZoneInfo.Utc);

        Assert.AreEqual(0, result.FiveHourPoints.Count);
        Assert.AreEqual(1, result.WeeklyPoints.Count);
    }

    /// <summary>同じ取得にあるFiveHourとWeeklyを独立した系列へ変換することを検証します。</summary>
    [TestMethod]
    public void Map_BothClassifications_CreatesTwoSeries()
    {
        UsageTrendHistory result = UsageTrendMapper.Map(
            [CreateEntry(
                NowUtc,
                CreateObservation(RateLimitClassification.FiveHour, 42),
                CreateObservation(RateLimitClassification.Weekly, 68))],
            TimeZoneInfo.Utc);

        Assert.AreEqual(42D, result.FiveHourPoints.Single().UsedPercent);
        Assert.AreEqual(68D, result.WeeklyPoints.Single().UsedPercent);
    }

    /// <summary>Unknown履歴を既知系列へ推測分類しないことを検証します。</summary>
    [TestMethod]
    public void Map_UnknownOnly_ReturnsNoPoints()
    {
        UsageTrendHistory result = UsageTrendMapper.Map(
            [CreateEntry(NowUtc, CreateObservation(RateLimitClassification.Unknown, 50))],
            TimeZoneInfo.Utc);

        Assert.IsFalse(result.HasAnyPoints);
    }

    /// <summary>入力順にかかわらず取得時刻の昇順へ並べることを検証します。</summary>
    [TestMethod]
    public void Map_UnorderedEntries_SortsTimestampAscending()
    {
        UsageTrendHistory result = UsageTrendMapper.Map(
            [
                CreateEntry(NowUtc, CreateObservation(RateLimitClassification.Weekly, 20)),
                CreateEntry(NowUtc.AddHours(-1), CreateObservation(RateLimitClassification.Weekly, 10)),
            ],
            TimeZoneInfo.Utc);

        Assert.IsTrue(result.WeeklyPoints[0].CapturedAtUtc < result.WeeklyPoints[1].CapturedAtUtc);
    }

    /// <summary>同一取得時刻と分類の点を1点へ重複排除することを検証します。</summary>
    [TestMethod]
    public void Map_DuplicateTimestampAndClassification_ReturnsOnePoint()
    {
        UsageTrendHistory result = UsageTrendMapper.Map(
            [
                CreateEntry(NowUtc, CreateObservation(RateLimitClassification.FiveHour, 10)),
                CreateEntry(NowUtc, CreateObservation(RateLimitClassification.FiveHour, 20)),
            ],
            TimeZoneInfo.Utc);

        Assert.AreEqual(1, result.FiveHourPoints.Count);
        Assert.AreEqual(10D, result.FiveHourPoints.Single().UsedPercent);
    }

    /// <summary>UTC取得時刻を指定タイムゾーンのローカル時刻へ変換することを検証します。</summary>
    [TestMethod]
    public void Map_LocalTimeZone_ConvertsDisplayTimestamp()
    {
        TimeZoneInfo timeZone = TimeZoneInfo.CreateCustomTimeZone(
            "TestJst",
            TimeSpan.FromHours(9),
            "TestJst",
            "TestJst");

        UsageTrendHistory result = UsageTrendMapper.Map(
            [CreateEntry(NowUtc, CreateObservation(RateLimitClassification.Weekly, 10))],
            timeZone);

        Assert.AreEqual(
            new DateTimeOffset(2026, 8, 31, 21, 0, 0, TimeSpan.FromHours(9)),
            result.WeeklyPoints.Single().CapturedAtLocal);
    }

    /// <summary>リセット時刻がnullでも表示点を生成できることを検証します。</summary>
    [TestMethod]
    public void Map_ResetTimeMissing_PreservesNull()
    {
        UsageTrendHistory result = UsageTrendMapper.Map(
            [CreateEntry(NowUtc, CreateObservation(RateLimitClassification.Weekly, 10, resetsAtUtc: null))],
            TimeZoneInfo.Utc);

        Assert.IsNull(result.WeeklyPoints.Single().ResetsAtUtc);
    }

    /// <summary>24時間・7日・30日・90日の各期間境界をメモリ上で抽出することを検証します。</summary>
    [TestMethod]
    [DataRow(UsageTrendRange.Hours24, 24)]
    [DataRow(UsageTrendRange.Days7, 168)]
    [DataRow(UsageTrendRange.Days30, 720)]
    [DataRow(UsageTrendRange.Days90, 2160)]
    public void Filter_Range_IncludesBoundary(UsageTrendRange range, int hours)
    {
        UsageTrendHistory history = UsageTrendMapper.Map(
            [
                CreateEntry(NowUtc.AddHours(-hours).AddTicks(-1), CreateObservation(RateLimitClassification.FiveHour, 1)),
                CreateEntry(NowUtc.AddHours(-hours), CreateObservation(RateLimitClassification.FiveHour, 2)),
                CreateEntry(NowUtc, CreateObservation(RateLimitClassification.FiveHour, 3)),
            ],
            TimeZoneInfo.Utc);

        UsageTrendHistory result = UsageTrendMapper.Filter(history, range, NowUtc);

        Assert.AreEqual(2, result.FiveHourPoints.Count);
        Assert.AreEqual(2D, result.FiveHourPoints[0].UsedPercent);
    }

    /// <summary>テスト用の取得履歴を生成します。</summary>
    private static UsageHistoryEntry CreateEntry(
        DateTimeOffset capturedAtUtc,
        params RateLimitObservation[] observations)
    {
        return new UsageHistoryEntry { CapturedAtUtc = capturedAtUtc, RateLimits = observations };
    }

    /// <summary>テスト用の利用枠観測値を生成します。</summary>
    private static RateLimitObservation CreateObservation(
        RateLimitClassification classification,
        double usedPercent,
        DateTimeOffset? resetsAtUtc = null)
    {
        return new RateLimitObservation
        {
            LimitId = "codex",
            Position = RateLimitPosition.Primary,
            WindowDurationMinutes = classification == RateLimitClassification.FiveHour ? 300 : 10080,
            UsedPercent = usedPercent,
            ResetsAtUtc = resetsAtUtc,
            Classification = classification,
        };
    }
}
