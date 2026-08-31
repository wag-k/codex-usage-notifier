using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Presentation.Trends;

namespace CodexUsageNotifier.Tests.Presentation.Trends;

/// <summary>
/// 使用率推移の座標計算、gap分割、および最近傍検索を検証します。
/// </summary>
[TestClass]
public sealed class UsageTrendLayoutCalculatorTests
{
    private static readonly DateTimeOffset StartUtc = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>0%、50%、100%を固定Y軸の下端・中央・上端へ配置することを検証します。</summary>
    [TestMethod]
    [DataRow(0D, 200D)]
    [DataRow(50D, 100D)]
    [DataRow(100D, 0D)]
    public void CalculatePoints_UsedPercent_MapsToFixedYAxis(double usedPercent, double expectedY)
    {
        UsageTrendPlotPoint point = UsageTrendLayoutCalculator.CalculatePoints(
            [CreatePoint(StartUtc, usedPercent)],
            StartUtc,
            StartUtc.AddHours(1),
            400,
            200).Single();

        Assert.AreEqual(expectedY, point.Y, 0.001);
    }

    /// <summary>時間軸の先頭と末尾を描画領域の左右端へ配置することを検証します。</summary>
    [TestMethod]
    public void CalculatePoints_TimeEndpoints_MapToLeftAndRight()
    {
        IReadOnlyList<UsageTrendPlotPoint> result = UsageTrendLayoutCalculator.CalculatePoints(
            [CreatePoint(StartUtc, 10), CreatePoint(StartUtc.AddHours(1), 20)],
            StartUtc,
            StartUtc.AddHours(1),
            400,
            200);

        Assert.AreEqual(0D, result[0].X, 0.001);
        Assert.AreEqual(400D, result[1].X, 0.001);
    }

    /// <summary>1点かつ時間幅0でも中央へ配置し例外にならないことを検証します。</summary>
    [TestMethod]
    public void CalculatePoints_OnePointAndZeroDuration_UsesCenter()
    {
        UsageTrendPlotPoint result = UsageTrendLayoutCalculator.CalculatePoints(
            [CreatePoint(StartUtc, 50)],
            StartUtc,
            StartUtc,
            400,
            200).Single();

        Assert.AreEqual(200D, result.X, 0.001);
    }

    /// <summary>幅と高さが0以下でも非負座標を返すことを検証します。</summary>
    [TestMethod]
    public void CalculatePoints_NonPositiveSize_ReturnsZeroCoordinates()
    {
        UsageTrendPlotPoint result = UsageTrendLayoutCalculator.CalculatePoints(
            [CreatePoint(StartUtc, 50)],
            StartUtc,
            StartUtc.AddHours(1),
            -1,
            0).Single();

        Assert.AreEqual(0D, result.X);
        Assert.AreEqual(0D, result.Y);
    }

    /// <summary>未観測系列を空の座標一覧として扱えることを検証します。</summary>
    [TestMethod]
    public void CalculatePoints_MissingSeries_ReturnsEmpty()
    {
        IReadOnlyList<UsageTrendPlotPoint> result = UsageTrendLayoutCalculator.CalculatePoints(
            [],
            StartUtc,
            StartUtc.AddHours(1),
            400,
            200);

        Assert.AreEqual(0, result.Count);
    }

    /// <summary>閾値を超える長時間gapで線を別segmentへ分割することを検証します。</summary>
    [TestMethod]
    public void SplitSegments_LongGap_CreatesSeparateSegments()
    {
        IReadOnlyList<IReadOnlyList<UsageTrendSeriesPoint>> result =
            UsageTrendLayoutCalculator.SplitSegments(
                [
                    CreatePoint(StartUtc, 10),
                    CreatePoint(StartUtc.AddHours(1), 20),
                    CreatePoint(StartUtc.AddHours(5), 30),
                ],
                TimeSpan.FromHours(2));

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(2, result[0].Count);
        Assert.AreEqual(1, result[1].Count);
    }

    /// <summary>gapがない系列を1segmentのまま維持することを検証します。</summary>
    [TestMethod]
    public void SplitSegments_NoLongGap_ReturnsOneSegment()
    {
        IReadOnlyList<IReadOnlyList<UsageTrendSeriesPoint>> result =
            UsageTrendLayoutCalculator.SplitSegments(
                [CreatePoint(StartUtc, 10), CreatePoint(StartUtc.AddHours(1), 20)],
                TimeSpan.FromHours(2));

        Assert.AreEqual(1, result.Count);
    }

    /// <summary>指定時刻に最も近い取得点を返すことを検証します。</summary>
    [TestMethod]
    public void FindNearestPoint_BetweenPoints_ReturnsNearestTimestamp()
    {
        UsageTrendSeriesPoint expected = CreatePoint(StartUtc.AddHours(1), 20);

        UsageTrendSeriesPoint? result = UsageTrendLayoutCalculator.FindNearestPoint(
            [CreatePoint(StartUtc, 10), expected],
            StartUtc.AddMinutes(50));

        Assert.AreSame(expected, result);
    }

    /// <summary>空系列の最近傍検索がnullを返すことを検証します。</summary>
    [TestMethod]
    public void FindNearestPoint_EmptySeries_ReturnsNull()
    {
        Assert.IsNull(UsageTrendLayoutCalculator.FindNearestPoint([], StartUtc));
    }

    /// <summary>X軸目盛が端点を含む指定件数になることを検証します。</summary>
    [TestMethod]
    public void CreateTimeTicks_ValidRange_IncludesBothEndpoints()
    {
        IReadOnlyList<DateTimeOffset> result = UsageTrendLayoutCalculator.CreateTimeTicks(
            StartUtc,
            StartUtc.AddHours(24),
            5);

        Assert.AreEqual(5, result.Count);
        Assert.AreEqual(StartUtc, result[0]);
        Assert.AreEqual(StartUtc.AddHours(24), result[^1]);
    }

    /// <summary>テスト用の系列点を生成します。</summary>
    private static UsageTrendSeriesPoint CreatePoint(DateTimeOffset capturedAtUtc, double usedPercent)
    {
        return new UsageTrendSeriesPoint
        {
            CapturedAtUtc = capturedAtUtc,
            CapturedAtLocal = capturedAtUtc,
            UsedPercent = usedPercent,
            Classification = RateLimitClassification.Weekly,
        };
    }
}
