using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Presentation.Trends;

/// <summary>
/// 永続化済み履歴をFiveHourとWeeklyの表示用系列へ変換します。
/// </summary>
public static class UsageTrendMapper
{
    /// <summary>
    /// 全履歴を取得時刻と分類で重複排除し、表示用系列へ変換します。
    /// </summary>
    /// <param name="entries">永続化済みの取得履歴です。</param>
    /// <param name="localTimeZone">表示に使用するローカルタイムゾーンです。</param>
    /// <returns>FiveHourとWeeklyだけを含む表示履歴です。</returns>
    public static UsageTrendHistory Map(
        IReadOnlyList<UsageHistoryEntry> entries,
        TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(localTimeZone);
        List<UsageTrendSeriesPoint> fiveHour = [];
        List<UsageTrendSeriesPoint> weekly = [];

        foreach (UsageHistoryEntry entry in entries.OrderBy(item => item.CapturedAtUtc))
        {
            AddFirstObservation(entry, RateLimitClassification.FiveHour, localTimeZone, fiveHour);
            AddFirstObservation(entry, RateLimitClassification.Weekly, localTimeZone, weekly);
        }

        return new UsageTrendHistory
        {
            FiveHourPoints = DeduplicateByTimestamp(fiveHour),
            WeeklyPoints = DeduplicateByTimestamp(weekly),
        };
    }

    /// <summary>
    /// 指定期間に含まれる取得点だけをメモリ上で抽出します。
    /// </summary>
    /// <param name="history">90日分まで読み込み済みの履歴です。</param>
    /// <param name="range">表示する期間です。</param>
    /// <param name="nowUtc">期間終端とする現在UTC時刻です。</param>
    /// <returns>指定期間に含まれる系列です。</returns>
    public static UsageTrendHistory Filter(
        UsageTrendHistory history,
        UsageTrendRange range,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(history);
        DateTimeOffset fromUtc = nowUtc - UsageTrendRangeDefinition.GetDuration(range);
        return new UsageTrendHistory
        {
            FiveHourPoints = FilterPoints(history.FiveHourPoints, fromUtc, nowUtc),
            WeeklyPoints = FilterPoints(history.WeeklyPoints, fromUtc, nowUtc),
        };
    }

    /// <summary>
    /// 1取得内の有効な使用率を追加します。週間系列はcodexだけを対象にします。
    /// </summary>
    private static void AddFirstObservation(
        UsageHistoryEntry entry,
        RateLimitClassification classification,
        TimeZoneInfo localTimeZone,
        List<UsageTrendSeriesPoint> destination)
    {
        RateLimitObservation? observation = entry.RateLimits.FirstOrDefault(candidate =>
            candidate.Classification == classification
            && (classification != RateLimitClassification.Weekly
                || string.Equals(candidate.LimitId, "codex", StringComparison.Ordinal))
            && double.IsFinite(candidate.UsedPercent));
        if (observation is null)
        {
            return;
        }

        destination.Add(new UsageTrendSeriesPoint
        {
            CapturedAtUtc = entry.CapturedAtUtc,
            CapturedAtLocal = TimeZoneInfo.ConvertTime(entry.CapturedAtUtc, localTimeZone),
            UsedPercent = Math.Clamp(observation.UsedPercent, 0D, 100D),
            ResetsAtUtc = observation.ResetsAtUtc,
            Classification = classification,
        });
    }

    /// <summary>
    /// 同一取得時刻の同一系列を1点へまとめます。
    /// </summary>
    private static UsageTrendSeriesPoint[] DeduplicateByTimestamp(
        IEnumerable<UsageTrendSeriesPoint> points)
    {
        return points
            .DistinctBy(point => point.CapturedAtUtc)
            .OrderBy(point => point.CapturedAtUtc)
            .ToArray();
    }

    /// <summary>
    /// UTC期間境界を含む取得点だけを返します。
    /// </summary>
    private static UsageTrendSeriesPoint[] FilterPoints(
        IReadOnlyList<UsageTrendSeriesPoint> points,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        return points
            .Where(point => point.CapturedAtUtc >= fromUtc && point.CapturedAtUtc <= toUtc)
            .ToArray();
    }
}
