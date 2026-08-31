namespace CodexUsageNotifier.Presentation.Trends;

/// <summary>
/// 使用率推移グラフの座標、gap分割、および最近傍取得点を計算します。
/// </summary>
public static class UsageTrendLayoutCalculator
{
    /// <summary>
    /// 取得点を0～100%固定Y軸とUTC時間軸の論理座標へ変換します。
    /// </summary>
    /// <param name="points">変換する系列です。</param>
    /// <param name="fromUtc">X軸の開始UTC時刻です。</param>
    /// <param name="toUtc">X軸の終了UTC時刻です。</param>
    /// <param name="width">描画領域の幅です。</param>
    /// <param name="height">描画領域の高さです。</param>
    /// <returns>元データを保持した論理座標です。</returns>
    public static IReadOnlyList<UsageTrendPlotPoint> CalculatePoints(
        IReadOnlyList<UsageTrendSeriesPoint> points,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        double width,
        double height)
    {
        ArgumentNullException.ThrowIfNull(points);
        double safeWidth = Math.Max(0D, width);
        double safeHeight = Math.Max(0D, height);
        double durationMilliseconds = Math.Max(0D, (toUtc - fromUtc).TotalMilliseconds);
        return points.Select(point =>
        {
            double x = durationMilliseconds <= 0D
                ? safeWidth / 2D
                : Math.Clamp(
                    (point.CapturedAtUtc - fromUtc).TotalMilliseconds / durationMilliseconds,
                    0D,
                    1D) * safeWidth;
            double y = (1D - (Math.Clamp(point.UsedPercent, 0D, 100D) / 100D)) * safeHeight;
            return new UsageTrendPlotPoint(x, y, point);
        }).ToArray();
    }

    /// <summary>
    /// 長時間の未観測区間を跨がない連続線へ系列を分割します。
    /// </summary>
    /// <param name="points">取得時刻順へ並べる系列です。</param>
    /// <param name="gapThreshold">この時間を超える間隔で線を切ります。</param>
    /// <returns>連続描画できる系列の一覧です。</returns>
    public static IReadOnlyList<IReadOnlyList<UsageTrendSeriesPoint>> SplitSegments(
        IReadOnlyList<UsageTrendSeriesPoint> points,
        TimeSpan gapThreshold)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(gapThreshold, TimeSpan.Zero);

        List<IReadOnlyList<UsageTrendSeriesPoint>> segments = [];
        List<UsageTrendSeriesPoint> current = [];
        foreach (UsageTrendSeriesPoint point in points.OrderBy(item => item.CapturedAtUtc))
        {
            if (current.Count > 0
                && point.CapturedAtUtc - current[^1].CapturedAtUtc > gapThreshold)
            {
                segments.Add(current.ToArray());
                current.Clear();
            }

            current.Add(point);
        }

        if (current.Count > 0)
        {
            segments.Add(current.ToArray());
        }

        return segments;
    }

    /// <summary>
    /// 指定UTC時刻に最も近い取得点を返します。
    /// </summary>
    /// <param name="points">検索対象の系列です。</param>
    /// <param name="targetUtc">マウス位置に対応するUTC時刻です。</param>
    /// <returns>最近傍点、または系列が空の場合はnullです。</returns>
    public static UsageTrendSeriesPoint? FindNearestPoint(
        IReadOnlyList<UsageTrendSeriesPoint> points,
        DateTimeOffset targetUtc)
    {
        ArgumentNullException.ThrowIfNull(points);
        return points
            .MinBy(point => Math.Abs((point.CapturedAtUtc - targetUtc).Ticks));
    }

    /// <summary>
    /// X軸へ等間隔に表示するUTC時刻を生成します。
    /// </summary>
    /// <param name="fromUtc">開始UTC時刻です。</param>
    /// <param name="toUtc">終了UTC時刻です。</param>
    /// <param name="tickCount">端点を含む目盛数です。</param>
    /// <returns>UTC時刻の目盛一覧です。</returns>
    public static IReadOnlyList<DateTimeOffset> CreateTimeTicks(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int tickCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tickCount);

        if (tickCount == 1 || toUtc <= fromUtc)
        {
            return [fromUtc];
        }

        TimeSpan interval = TimeSpan.FromTicks((toUtc - fromUtc).Ticks / (tickCount - 1));
        return Enumerable.Range(0, tickCount)
            .Select(index => index == tickCount - 1 ? toUtc : fromUtc + (interval * index))
            .ToArray();
    }
}
