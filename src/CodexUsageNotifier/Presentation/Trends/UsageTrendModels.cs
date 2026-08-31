using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Presentation.Trends;

/// <summary>
/// 使用率推移グラフで選択できる表示期間を表します。
/// </summary>
public enum UsageTrendRange
{
    /// <summary>直近24時間です。</summary>
    Hours24,

    /// <summary>直近7日です。</summary>
    Days7,

    /// <summary>直近30日です。</summary>
    Days30,

    /// <summary>直近90日です。</summary>
    Days90,
}

/// <summary>
/// 使用率推移グラフの1系列に含まれる1取得点を表します。
/// </summary>
public sealed record UsageTrendSeriesPoint
{
    /// <summary>取得UTC時刻を取得または設定します。</summary>
    public DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>画面表示用のローカル取得時刻を取得または設定します。</summary>
    public DateTimeOffset CapturedAtLocal { get; init; }

    /// <summary>観測した使用率を取得または設定します。</summary>
    public double UsedPercent { get; init; }

    /// <summary>観測した残量を取得します。</summary>
    public double RemainingPercent => Math.Clamp(100D - UsedPercent, 0D, 100D);

    /// <summary>取得できたUTCの次回リセット時刻を取得または設定します。</summary>
    public DateTimeOffset? ResetsAtUtc { get; init; }

    /// <summary>利用枠の分類を取得または設定します。</summary>
    public RateLimitClassification Classification { get; init; }
}

/// <summary>
/// 5時間枠と週間枠の全読み込み済み使用率推移を保持します。
/// </summary>
public sealed record UsageTrendHistory
{
    /// <summary>5時間枠の取得点を取得または設定します。</summary>
    public IReadOnlyList<UsageTrendSeriesPoint> FiveHourPoints { get; init; } =
        Array.Empty<UsageTrendSeriesPoint>();

    /// <summary>週間枠の取得点を取得または設定します。</summary>
    public IReadOnlyList<UsageTrendSeriesPoint> WeeklyPoints { get; init; } =
        Array.Empty<UsageTrendSeriesPoint>();

    /// <summary>いずれかの系列に取得点が存在するかどうかを取得します。</summary>
    public bool HasAnyPoints => FiveHourPoints.Count > 0 || WeeklyPoints.Count > 0;
}

/// <summary>
/// グラフ上の論理座標と元データを対応付けます。
/// </summary>
public readonly record struct UsageTrendPlotPoint
{
    /// <summary>
    /// 論理座標を初期化します。
    /// </summary>
    /// <param name="x">グラフ左端からのX座標です。</param>
    /// <param name="y">グラフ上端からのY座標です。</param>
    /// <param name="source">対応する系列データです。</param>
    public UsageTrendPlotPoint(double x, double y, UsageTrendSeriesPoint source)
    {
        ArgumentNullException.ThrowIfNull(source);
        X = x;
        Y = y;
        Source = source;
    }

    /// <summary>X座標を取得します。</summary>
    public double X { get; }

    /// <summary>Y座標を取得します。</summary>
    public double Y { get; }

    /// <summary>座標に対応する系列データを取得します。</summary>
    public UsageTrendSeriesPoint Source { get; }
}
