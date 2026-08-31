namespace CodexUsageNotifier.Presentation.Trends;

/// <summary>
/// 使用率推移の表示期間に対応する時間幅と表示名を提供します。
/// </summary>
public static class UsageTrendRangeDefinition
{
    /// <summary>
    /// 表示期間に対応する時間幅を返します。
    /// </summary>
    /// <param name="range">表示期間です。</param>
    /// <returns>期間の時間幅です。</returns>
    public static TimeSpan GetDuration(UsageTrendRange range) => range switch
    {
        UsageTrendRange.Hours24 => TimeSpan.FromHours(24),
        UsageTrendRange.Days7 => TimeSpan.FromDays(7),
        UsageTrendRange.Days30 => TimeSpan.FromDays(30),
        UsageTrendRange.Days90 => TimeSpan.FromDays(90),
        _ => throw new ArgumentOutOfRangeException(nameof(range)),
    };

    /// <summary>
    /// 表示期間の日本語表示名を返します。
    /// </summary>
    /// <param name="range">表示期間です。</param>
    /// <returns>期間切替ボタンへ表示する名前です。</returns>
    public static string GetDisplayName(UsageTrendRange range) => range switch
    {
        UsageTrendRange.Hours24 => "24時間",
        UsageTrendRange.Days7 => "7日",
        UsageTrendRange.Days30 => "30日",
        UsageTrendRange.Days90 => "90日",
        _ => throw new ArgumentOutOfRangeException(nameof(range)),
    };
}
