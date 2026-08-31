namespace CodexUsageNotifier.Presentation.Trends;

/// <summary>
/// 時間bucketごとの先頭・最小・最大・末尾を残して表示点数を抑えます。
/// </summary>
public static class UsageTrendDownsampler
{
    /// <summary>
    /// 急激な増減を保持しながら指定上限以下へ表示点を削減します。
    /// </summary>
    /// <param name="points">取得時刻順へ並べる元系列です。</param>
    /// <param name="maximumPointCount">表示に許可する最大点数です。</param>
    /// <returns>時系列順の表示用取得点です。</returns>
    public static IReadOnlyList<UsageTrendSeriesPoint> Downsample(
        IReadOnlyList<UsageTrendSeriesPoint> points,
        int maximumPointCount)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPointCount);

        UsageTrendSeriesPoint[] ordered = points
            .OrderBy(point => point.CapturedAtUtc)
            .ToArray();
        if (ordered.Length <= maximumPointCount)
        {
            return ordered;
        }

        if (maximumPointCount == 1)
        {
            return [ordered[^1]];
        }

        if (maximumPointCount < 4)
        {
            return [ordered[0], ordered[^1]];
        }

        int bucketCount = Math.Max(1, maximumPointCount / 4);
        long firstTicks = ordered[0].CapturedAtUtc.UtcTicks;
        long lastTicks = ordered[^1].CapturedAtUtc.UtcTicks;
        long durationTicks = Math.Max(1L, lastTicks - firstTicks + 1L);
        double bucketWidthTicks = durationTicks / (double)bucketCount;
        List<(UsageTrendSeriesPoint Point, int OriginalIndex)> selected = [];

        foreach (IGrouping<int, (UsageTrendSeriesPoint Point, int Index)> bucket in ordered
            .Select((point, index) => (Point: point, Index: index))
            .GroupBy(item => Math.Min(
                bucketCount - 1,
                (int)((item.Point.CapturedAtUtc.UtcTicks - firstTicks) / bucketWidthTicks))))
        {
            (UsageTrendSeriesPoint Point, int Index)[] items = bucket.ToArray();
            AddDistinct(selected, items[0]);
            AddDistinct(selected, items.MinBy(item => item.Point.UsedPercent));
            AddDistinct(selected, items.MaxBy(item => item.Point.UsedPercent));
            AddDistinct(selected, items[^1]);
        }

        return selected
            .OrderBy(item => item.OriginalIndex)
            .Select(item => item.Point)
            .Take(maximumPointCount)
            .ToArray();
    }

    /// <summary>
    /// 同じ元データ位置を重複追加しないよう候補へ加えます。
    /// </summary>
    private static void AddDistinct(
        List<(UsageTrendSeriesPoint Point, int OriginalIndex)> destination,
        (UsageTrendSeriesPoint Point, int Index) candidate)
    {
        if (destination.All(item => item.OriginalIndex != candidate.Index))
        {
            destination.Add((candidate.Point, candidate.Index));
        }
    }
}
