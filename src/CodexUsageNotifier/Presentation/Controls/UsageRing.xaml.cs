using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexUsageNotifier.Presentation.ViewModels;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using UserControl = System.Windows.Controls.UserControl;

namespace CodexUsageNotifier.Presentation.Controls;

/// <summary>
/// 利用可能残量をWPF標準描画だけで円形表示します。
/// </summary>
public partial class UsageRing : UserControl
{
    /// <summary>残量割合を表す依存関係プロパティです。</summary>
    public static readonly DependencyProperty PercentageProperty = DependencyProperty.Register(
        nameof(Percentage),
        typeof(double?),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnVisualPropertyChanged));

    /// <summary>観測状態を表す依存関係プロパティです。</summary>
    public static readonly DependencyProperty IsObservedProperty = DependencyProperty.Register(
        nameof(IsObserved),
        typeof(bool),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnVisualPropertyChanged));

    /// <summary>進捗線の色を表す依存関係プロパティです。</summary>
    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush),
        typeof(Brush),
        typeof(UsageRing),
        new FrameworkPropertyMetadata(
            new SolidColorBrush(Color.FromRgb(37, 99, 235)),
            FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>円形残量表示を初期化します。</summary>
    public UsageRing()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    /// <summary>0から100へ正規化される残量割合を取得または設定します。</summary>
    public double? Percentage
    {
        get => (double?)GetValue(PercentageProperty);
        set => SetValue(PercentageProperty, value);
    }

    /// <summary>利用枠を観測できているかを取得または設定します。</summary>
    public bool IsObserved
    {
        get => (bool)GetValue(IsObservedProperty);
        set => SetValue(IsObservedProperty, value);
    }

    /// <summary>進捗線の色を取得または設定します。</summary>
    public Brush RingBrush
    {
        get => (Brush)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    /// <summary>コントロール読込後に円弧を更新します。</summary>
    /// <param name="sender">円形表示です。</param>
    /// <param name="e">読込イベントです。</param>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateVisual();
    }

    /// <summary>コントロールの大きさに合わせて円弧を更新します。</summary>
    /// <param name="sender">円形表示です。</param>
    /// <param name="e">サイズ変更イベントです。</param>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateVisual();
    }

    /// <summary>表示用依存関係プロパティの変更を円弧へ反映します。</summary>
    /// <param name="dependencyObject">変更された円形表示です。</param>
    /// <param name="eventArgs">依存関係プロパティの変更情報です。</param>
    private static void OnVisualPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is UsageRing usageRing)
        {
            usageRing.UpdateVisual();
        }
    }

    /// <summary>現在の割合と観測状態から円弧と文字列を描画します。</summary>
    private void UpdateVisual()
    {
        double? normalized = UsageRingValue.Normalize(Percentage);
        bool canDisplay = IsObserved && normalized is not null;
        PercentageText.Text = canDisplay
            ? normalized.GetValueOrDefault().ToString("0.#'%'", CultureInfo.CurrentCulture)
            : "--";
        CaptionText.Text = IsObserved ? "残り" : "未観測";
        FullEllipse.Visibility = canDisplay && normalized >= 100D
            ? Visibility.Visible
            : Visibility.Collapsed;
        ProgressPath.Visibility = canDisplay && normalized is > 0D and < 100D
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (ProgressPath.Visibility != Visibility.Visible)
        {
            ProgressPath.Data = null;
            return;
        }

        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 20D)
        {
            return;
        }

        double centerX = ActualWidth / 2D;
        double centerY = ActualHeight / 2D;
        double radius = (size / 2D) - 12D;
        double startAngle = -90D;
        double endAngle = startAngle + (normalized!.Value * 3.6D);
        Point startPoint = PointOnCircle(centerX, centerY, radius, startAngle);
        Point endPoint = PointOnCircle(centerX, centerY, radius, endAngle);
        PathFigure figure = new()
        {
            StartPoint = startPoint,
            IsClosed = false,
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = endPoint,
            Size = new Size(radius, radius),
            IsLargeArc = normalized > 50D,
            SweepDirection = SweepDirection.Clockwise,
        });
        ProgressPath.Data = new PathGeometry([figure]);
    }

    /// <summary>角度から円周上の座標を計算します。</summary>
    /// <param name="centerX">中心のX座標です。</param>
    /// <param name="centerY">中心のY座標です。</param>
    /// <param name="radius">円の半径です。</param>
    /// <param name="angleDegrees">上端を基準とする角度です。</param>
    /// <returns>円周上の座標です。</returns>
    private static Point PointOnCircle(
        double centerX,
        double centerY,
        double radius,
        double angleDegrees)
    {
        double angleRadians = angleDegrees * Math.PI / 180D;
        return new Point(
            centerX + (radius * Math.Cos(angleRadians)),
            centerY + (radius * Math.Sin(angleRadians)));
    }
}
