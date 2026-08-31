using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Presentation.Trends;

namespace CodexUsageNotifier.Tests.Presentation.Trends;

/// <summary>
/// 使用率推移の急変を保持するtime-bucket downsamplingを検証します。
/// </summary>
[TestClass]
public sealed class UsageTrendDownsamplerTests
{
    /// <summary>点数が上限以下の場合に全点を維持することを検証します。</summary>
    [TestMethod]
    public void Downsample_FewPoints_ReturnsAllPoints()
    {
        IReadOnlyList<UsageTrendSeriesPoint> points = CreatePoints(10);

        IReadOnlyList<UsageTrendSeriesPoint> result = UsageTrendDownsampler.Downsample(points, 1500);

        CollectionAssert.AreEqual(points.ToArray(), result.ToArray());
    }

    /// <summary>大量点を指定上限以下へ削減することを検証します。</summary>
    [TestMethod]
    public void Downsample_ManyPoints_StaysWithinLimit()
    {
        IReadOnlyList<UsageTrendSeriesPoint> result = UsageTrendDownsampler.Downsample(
            CreatePoints(5000),
            1500);

        Assert.IsTrue(result.Count <= 1500);
    }

    /// <summary>大量点でも系列の先頭と末尾を保持することを検証します。</summary>
    [TestMethod]
    public void Downsample_ManyPoints_PreservesFirstAndLast()
    {
        IReadOnlyList<UsageTrendSeriesPoint> points = CreatePoints(5000);

        IReadOnlyList<UsageTrendSeriesPoint> result = UsageTrendDownsampler.Downsample(points, 1500);

        Assert.AreSame(points[0], result[0]);
        Assert.AreSame(points[^1], result[^1]);
    }

    /// <summary>bucket内の局所最小値と局所最大値を保持することを検証します。</summary>
    [TestMethod]
    public void Downsample_LocalExtremes_PreservesMinimumAndMaximum()
    {
        List<UsageTrendSeriesPoint> points = CreatePoints(100).ToList();
        points[40] = points[40] with { UsedPercent = 100 };
        points[41] = points[41] with { UsedPercent = 0 };

        IReadOnlyList<UsageTrendSeriesPoint> result = UsageTrendDownsampler.Downsample(points, 20);

        Assert.IsTrue(result.Any(point => point.UsedPercent == 100));
        Assert.IsTrue(result.Any(point => point.UsedPercent == 0));
    }

    /// <summary>リセット直前の高使用率と直後の低使用率を削除しないことを検証します。</summary>
    [TestMethod]
    public void Downsample_ResetDrop_PreservesBothSides()
    {
        List<UsageTrendSeriesPoint> points = CreatePoints(100).ToList();
        points[50] = points[50] with { UsedPercent = 92 };
        points[51] = points[51] with { UsedPercent = 5 };

        IReadOnlyList<UsageTrendSeriesPoint> result = UsageTrendDownsampler.Downsample(points, 20);

        Assert.IsTrue(result.Any(point => point.UsedPercent == 92));
        Assert.IsTrue(result.Any(point => point.UsedPercent == 5));
    }

    /// <summary>削減後も取得時刻の昇順を維持することを検証します。</summary>
    [TestMethod]
    public void Downsample_UnorderedInput_ReturnsTimestampAscending()
    {
        IReadOnlyList<UsageTrendSeriesPoint> result = UsageTrendDownsampler.Downsample(
            CreatePoints(100).Reverse().ToArray(),
            20);

        Assert.IsTrue(result.Zip(result.Skip(1)).All(pair =>
            pair.First.CapturedAtUtc <= pair.Second.CapturedAtUtc));
    }

    /// <summary>テスト用の時系列を生成します。</summary>
    private static IReadOnlyList<UsageTrendSeriesPoint> CreatePoints(int count)
    {
        DateTimeOffset start = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, count)
            .Select(index => new UsageTrendSeriesPoint
            {
                CapturedAtUtc = start.AddMinutes(index),
                CapturedAtLocal = start.AddMinutes(index),
                UsedPercent = index % 80,
                Classification = RateLimitClassification.Weekly,
            })
            .ToArray();
    }
}
