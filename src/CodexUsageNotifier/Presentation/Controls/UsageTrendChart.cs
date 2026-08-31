using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CodexUsageNotifier.Presentation.Trends;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using TextFlowDirection = System.Windows.FlowDirection;

namespace CodexUsageNotifier.Presentation.Controls;

/// <summary>
/// WPFのDrawingContextで5時間枠と週間枠の使用率推移を軽量描画します。
/// </summary>
public sealed class UsageTrendChart : FrameworkElement
{
    private const double PlotLeft = 48D;
    private const double PlotTop = 14D;
    private const double PlotRight = 14D;
    private const double PlotBottom = 34D;
    private static readonly Brush GridBrush = CreateFrozenBrush(Color.FromRgb(234, 238, 245));
    private static readonly Brush AxisTextBrush = CreateFrozenBrush(Color.FromRgb(102, 112, 133));

    /// <summary>5時間枠系列の依存関係プロパティです。</summary>
    public static readonly DependencyProperty FiveHourPointsProperty = DependencyProperty.Register(
        nameof(FiveHourPoints),
        typeof(IReadOnlyList<UsageTrendSeriesPoint>),
        typeof(UsageTrendChart),
        new FrameworkPropertyMetadata(
            Array.Empty<UsageTrendSeriesPoint>(),
            FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>週間枠系列の依存関係プロパティです。</summary>
    public static readonly DependencyProperty WeeklyPointsProperty = DependencyProperty.Register(
        nameof(WeeklyPoints),
        typeof(IReadOnlyList<UsageTrendSeriesPoint>),
        typeof(UsageTrendChart),
        new FrameworkPropertyMetadata(
            Array.Empty<UsageTrendSeriesPoint>(),
            FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>X軸開始UTC時刻の依存関係プロパティです。</summary>
    public static readonly DependencyProperty RangeStartUtcProperty = DependencyProperty.Register(
        nameof(RangeStartUtc),
        typeof(DateTimeOffset),
        typeof(UsageTrendChart),
        new FrameworkPropertyMetadata(DateTimeOffset.UnixEpoch, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>X軸終了UTC時刻の依存関係プロパティです。</summary>
    public static readonly DependencyProperty RangeEndUtcProperty = DependencyProperty.Register(
        nameof(RangeEndUtc),
        typeof(DateTimeOffset),
        typeof(UsageTrendChart),
        new FrameworkPropertyMetadata(DateTimeOffset.UnixEpoch, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>gap分割閾値の依存関係プロパティです。</summary>
    public static readonly DependencyProperty GapThresholdProperty = DependencyProperty.Register(
        nameof(GapThreshold),
        typeof(TimeSpan),
        typeof(UsageTrendChart),
        new FrameworkPropertyMetadata(TimeSpan.FromHours(2), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>表示期間の依存関係プロパティです。</summary>
    public static readonly DependencyProperty SelectedRangeProperty = DependencyProperty.Register(
        nameof(SelectedRange),
        typeof(UsageTrendRange),
        typeof(UsageTrendChart),
        new FrameworkPropertyMetadata(UsageTrendRange.Days7, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>5時間枠線色の依存関係プロパティです。</summary>
    public static readonly DependencyProperty FiveHourBrushProperty = DependencyProperty.Register(
        nameof(FiveHourBrush),
        typeof(Brush),
        typeof(UsageTrendChart),
        new FrameworkPropertyMetadata(Brushes.ForestGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>週間枠線色の依存関係プロパティです。</summary>
    public static readonly DependencyProperty WeeklyBrushProperty = DependencyProperty.Register(
        nameof(WeeklyBrush),
        typeof(Brush),
        typeof(UsageTrendChart),
        new FrameworkPropertyMetadata(Brushes.RoyalBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>5時間枠の表示点を取得または設定します。</summary>
    public IReadOnlyList<UsageTrendSeriesPoint> FiveHourPoints
    {
        get => (IReadOnlyList<UsageTrendSeriesPoint>)GetValue(FiveHourPointsProperty);
        set => SetValue(FiveHourPointsProperty, value);
    }

    /// <summary>週間枠の表示点を取得または設定します。</summary>
    public IReadOnlyList<UsageTrendSeriesPoint> WeeklyPoints
    {
        get => (IReadOnlyList<UsageTrendSeriesPoint>)GetValue(WeeklyPointsProperty);
        set => SetValue(WeeklyPointsProperty, value);
    }

    /// <summary>X軸の開始UTC時刻を取得または設定します。</summary>
    public DateTimeOffset RangeStartUtc
    {
        get => (DateTimeOffset)GetValue(RangeStartUtcProperty);
        set => SetValue(RangeStartUtcProperty, value);
    }

    /// <summary>X軸の終了UTC時刻を取得または設定します。</summary>
    public DateTimeOffset RangeEndUtc
    {
        get => (DateTimeOffset)GetValue(RangeEndUtcProperty);
        set => SetValue(RangeEndUtcProperty, value);
    }

    /// <summary>未観測区間として線を切る時間幅を取得または設定します。</summary>
    public TimeSpan GapThreshold
    {
        get => (TimeSpan)GetValue(GapThresholdProperty);
        set => SetValue(GapThresholdProperty, value);
    }

    /// <summary>X軸ラベルの粒度に使用する表示期間を取得または設定します。</summary>
    public UsageTrendRange SelectedRange
    {
        get => (UsageTrendRange)GetValue(SelectedRangeProperty);
        set => SetValue(SelectedRangeProperty, value);
    }

    /// <summary>5時間枠の線色を取得または設定します。</summary>
    public Brush FiveHourBrush
    {
        get => (Brush)GetValue(FiveHourBrushProperty);
        set => SetValue(FiveHourBrushProperty, value);
    }

    /// <summary>週間枠の線色を取得または設定します。</summary>
    public Brush WeeklyBrush
    {
        get => (Brush)GetValue(WeeklyBrushProperty);
        set => SetValue(WeeklyBrushProperty, value);
    }

    /// <summary>
    /// 軸、grid、直線系列、および最新点markerを描画します。
    /// </summary>
    /// <param name="drawingContext">WPFの描画先です。</param>
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        base.OnRender(drawingContext);
        Rect plot = GetPlotBounds();
        DrawAxes(drawingContext, plot);
        if (plot.Width <= 0D || plot.Height <= 0D)
        {
            return;
        }

        DrawSeries(drawingContext, plot, FiveHourPoints, FiveHourBrush, isDashed: true);
        DrawSeries(drawingContext, plot, WeeklyPoints, WeeklyBrush, isDashed: false);
    }

    /// <summary>
    /// マウス位置に最も近い取得時刻の使用率と残量をTooltipへ設定します。
    /// </summary>
    /// <param name="e">マウス移動情報です。</param>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        Rect plot = GetPlotBounds();
        if (plot.Width <= 0D || RangeEndUtc <= RangeStartUtc)
        {
            ToolTip = null;
            return;
        }

        Point mouse = e.GetPosition(this);
        double fraction = Math.Clamp((mouse.X - plot.Left) / plot.Width, 0D, 1D);
        DateTimeOffset targetUtc = RangeStartUtc
            + TimeSpan.FromTicks((long)((RangeEndUtc - RangeStartUtc).Ticks * fraction));
        UsageTrendSeriesPoint? fiveHour = UsageTrendLayoutCalculator.FindNearestPoint(FiveHourPoints, targetUtc);
        UsageTrendSeriesPoint? weekly = UsageTrendLayoutCalculator.FindNearestPoint(WeeklyPoints, targetUtc);
        UsageTrendSeriesPoint? anchor = SelectNearestAnchor(fiveHour, weekly, targetUtc);
        ToolTip = anchor is null ? null : CreateTooltip(anchor.CapturedAtUtc, fiveHour, weekly);
    }

    /// <summary>利用可能な描画領域を余白から計算します。</summary>
    private Rect GetPlotBounds()
    {
        return new Rect(
            PlotLeft,
            PlotTop,
            Math.Max(0D, ActualWidth - PlotLeft - PlotRight),
            Math.Max(0D, ActualHeight - PlotTop - PlotBottom));
    }

    /// <summary>0～100%固定Y軸と期間別X軸labelを描画します。</summary>
    private void DrawAxes(DrawingContext drawingContext, Rect plot)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Pen gridPen = new(GridBrush, 1D);
        foreach (int percentage in new[] { 0, 25, 50, 75, 100 })
        {
            double y = plot.Bottom - (plot.Height * percentage / 100D);
            drawingContext.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            DrawText(
                drawingContext,
                $"{percentage}%",
                new Point(4D, y - 8D),
                pixelsPerDip);
        }

        IReadOnlyList<DateTimeOffset> ticks = UsageTrendLayoutCalculator.CreateTimeTicks(
            RangeStartUtc,
            RangeEndUtc,
            5);
        for (int index = 0; index < ticks.Count; index++)
        {
            double x = ticks.Count == 1
                ? plot.Left
                : plot.Left + (plot.Width * index / (ticks.Count - 1D));
            drawingContext.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            string label = FormatXAxisLabel(ticks[index].ToLocalTime(), SelectedRange);
            FormattedText text = CreateFormattedText(label, pixelsPerDip);
            double left = Math.Clamp(x - (text.Width / 2D), plot.Left, Math.Max(plot.Left, plot.Right - text.Width));
            drawingContext.DrawText(text, new Point(left, plot.Bottom + 7D));
        }
    }

    /// <summary>1系列をgapごとの直線segmentと最新markerで描画します。</summary>
    private void DrawSeries(
        DrawingContext drawingContext,
        Rect plot,
        IReadOnlyList<UsageTrendSeriesPoint> points,
        Brush brush,
        bool isDashed)
    {
        if (points.Count == 0)
        {
            return;
        }

        Pen pen = new(brush, 2D)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            DashStyle = isDashed ? DashStyles.Dash : DashStyles.Solid,
        };
        foreach (IReadOnlyList<UsageTrendSeriesPoint> segment in UsageTrendLayoutCalculator.SplitSegments(
            points,
            GapThreshold))
        {
            IReadOnlyList<UsageTrendPlotPoint> logical = UsageTrendLayoutCalculator.CalculatePoints(
                segment,
                RangeStartUtc,
                RangeEndUtc,
                plot.Width,
                plot.Height);
            if (logical.Count == 1)
            {
                UsageTrendPlotPoint point = logical[0];
                drawingContext.DrawEllipse(
                    brush,
                    null,
                    new Point(plot.Left + point.X, plot.Top + point.Y),
                    2.5D,
                    2.5D);
                continue;
            }

            StreamGeometry geometry = new();
            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(
                    new Point(plot.Left + logical[0].X, plot.Top + logical[0].Y),
                    isFilled: false,
                    isClosed: false);
                foreach (UsageTrendPlotPoint point in logical.Skip(1))
                {
                    context.LineTo(
                        new Point(plot.Left + point.X, plot.Top + point.Y),
                        isStroked: true,
                        isSmoothJoin: false);
                }
            }

            geometry.Freeze();
            drawingContext.DrawGeometry(null, pen, geometry);
        }

        UsageTrendSeriesPoint latest = points.MaxBy(point => point.CapturedAtUtc)!;
        UsageTrendPlotPoint latestPlot = UsageTrendLayoutCalculator.CalculatePoints(
            [latest],
            RangeStartUtc,
            RangeEndUtc,
            plot.Width,
            plot.Height).Single();
        drawingContext.DrawEllipse(
            Brushes.White,
            new Pen(brush, 2D),
            new Point(plot.Left + latestPlot.X, plot.Top + latestPlot.Y),
            4D,
            4D);
    }

    /// <summary>2系列の候補からマウス時刻に近い基準点を選択します。</summary>
    private static UsageTrendSeriesPoint? SelectNearestAnchor(
        UsageTrendSeriesPoint? fiveHour,
        UsageTrendSeriesPoint? weekly,
        DateTimeOffset targetUtc)
    {
        if (fiveHour is null)
        {
            return weekly;
        }

        if (weekly is null)
        {
            return fiveHour;
        }

        return Math.Abs((fiveHour.CapturedAtUtc - targetUtc).Ticks)
            <= Math.Abs((weekly.CapturedAtUtc - targetUtc).Ticks)
                ? fiveHour
                : weekly;
    }

    /// <summary>基準取得時刻に一致する各系列のTooltip文を生成します。</summary>
    private static string CreateTooltip(
        DateTimeOffset anchorUtc,
        UsageTrendSeriesPoint? fiveHour,
        UsageTrendSeriesPoint? weekly)
    {
        StringBuilder builder = new();
        builder.AppendLine(anchorUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture));
        AppendSeriesTooltip(builder, "5時間枠", anchorUtc, fiveHour);
        builder.AppendLine();
        AppendSeriesTooltip(builder, "週間枠", anchorUtc, weekly);
        return builder.ToString().TrimEnd();
    }

    /// <summary>1系列の使用率、残量、および任意のリセット時刻をTooltipへ追記します。</summary>
    private static void AppendSeriesTooltip(
        StringBuilder builder,
        string title,
        DateTimeOffset anchorUtc,
        UsageTrendSeriesPoint? point)
    {
        builder.AppendLine();
        builder.AppendLine(title);
        if (point is null || point.CapturedAtUtc != anchorUtc)
        {
            builder.Append("未観測");
            return;
        }

        builder.AppendLine(string.Format(
            CultureInfo.CurrentCulture,
            "使用率 {0:0.#}%",
            point.UsedPercent));
        builder.Append(string.Format(
            CultureInfo.CurrentCulture,
            "残り {0:0.#}%",
            point.RemainingPercent));
        if (point.ResetsAtUtc is not null)
        {
            builder.AppendLine();
            builder.Append(string.Format(
                CultureInfo.CurrentCulture,
                "次回リセット {0:yyyy/MM/dd HH:mm}",
                point.ResetsAtUtc.Value.ToLocalTime()));
        }
    }

    /// <summary>期間に応じたX軸labelへローカル時刻を整形します。</summary>
    private static string FormatXAxisLabel(DateTimeOffset localTime, UsageTrendRange range)
    {
        return range == UsageTrendRange.Hours24
            ? localTime.ToString("HH:mm", CultureInfo.CurrentCulture)
            : localTime.ToString("M/d", CultureInfo.CurrentCulture);
    }

    /// <summary>軸labelを指定位置へ描画します。</summary>
    private static void DrawText(
        DrawingContext drawingContext,
        string text,
        Point origin,
        double pixelsPerDip)
    {
        drawingContext.DrawText(CreateFormattedText(text, pixelsPerDip), origin);
    }

    /// <summary>軸label用の軽量なFormattedTextを生成します。</summary>
    private static FormattedText CreateFormattedText(string text, double pixelsPerDip)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            TextFlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11D,
            AxisTextBrush,
            pixelsPerDip);
    }

    /// <summary>共有描画用にFreeze済みSolidColorBrushを生成します。</summary>
    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        SolidColorBrush brush = new(color);
        brush.Freeze();
        return brush;
    }
}
