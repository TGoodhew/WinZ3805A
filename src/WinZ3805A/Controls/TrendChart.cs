using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

using Windows.Foundation;

namespace WinZ3805A.Controls;

/// <summary>
/// §9.10.2's trend chart: a decimated line over a zero-anchored axis with a diverging fill.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hand-drawn, and #38 measured why.</b> `LiveChartsCore.SkiaSharpView.WinUI` builds and renders
/// on the pinned Windows App SDK, but given §9.10.2's 604 800 points it materialised 1 659 MB and
/// has no downsampling of its own. Since min/max-per-column decimation had to be written either
/// way, the library would only ever have drawn the ~1 200 columns a <c>Canvas</c> draws too — in
/// exchange for a native SkiaSharp dependency and an SDK two majors behind this one.
/// </para>
/// <para>
/// The split is the sky plot's: <see cref="TrendDecimation"/> is arithmetic and is unit-tested
/// headlessly; this only draws. Nothing here decides what a column contains.
/// </para>
/// <para>
/// <b>Each column is a vertical stroke from its minimum to its maximum</b>, not a point on a
/// polyline. That is the whole argument of the decimation: a one-second excursion inside an
/// eight-minute column has to be a full-height mark, and joining column midpoints would average it
/// back out of existence. Where a column is a single sample the stroke collapses to a dot, which
/// is correct — that is what one reading looks like.
/// </para>
/// <para>
/// <b>Gaps draw nothing.</b> A column with no samples is absent from the decimator's output and is
/// skipped here rather than bridged, so a period the receiver was disconnected is a hole rather
/// than a straight line between two readings that were never adjacent. Same rule as the medallion
/// ring and §11.1.
/// </para>
/// <para>
/// <b>No animation.</b> §9.8.2 gives readout value changes <c>WzDurationInstant</c>, and §9.13 item
/// 9 forbids a storyboard targeting a readout. A chart that eased between two states would be
/// animating a measurement.
/// </para>
/// </remarks>
public sealed class TrendChart : Control
{
    /// <summary>Identifies the <see cref="Samples"/> dependency property.</summary>
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples),
        typeof(IReadOnlyList<TrendSample>),
        typeof(TrendChart),
        new PropertyMetadata(null, OnChartChanged));

    /// <summary>Identifies the <see cref="FromTicks"/> dependency property.</summary>
    public static readonly DependencyProperty FromTicksProperty = DependencyProperty.Register(
        nameof(FromTicks),
        typeof(long),
        typeof(TrendChart),
        new PropertyMetadata(0L, OnChartChanged));

    /// <summary>Identifies the <see cref="ToTicks"/> dependency property.</summary>
    public static readonly DependencyProperty ToTicksProperty = DependencyProperty.Register(
        nameof(ToTicks),
        typeof(long),
        typeof(TrendChart),
        new PropertyMetadata(0L, OnChartChanged));

    /// <summary>Identifies the <see cref="Unit"/> dependency property.</summary>
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit),
        typeof(string),
        typeof(TrendChart),
        new PropertyMetadata("ns", OnChartChanged));

    /// <summary>Identifies the <see cref="Floor"/> dependency property.</summary>
    public static readonly DependencyProperty FloorProperty = DependencyProperty.Register(
        nameof(Floor),
        typeof(double),
        typeof(TrendChart),
        new PropertyMetadata(50.0, OnChartChanged));

    private Canvas? _surface;

    /// <summary>Creates the control.</summary>
    public TrendChart()
    {
        DefaultStyleKey = typeof(TrendChart);
        SizeChanged += (_, _) => Draw();
        ActualThemeChanged += (_, _) => Draw();
    }

    /// <summary>The series to draw, in ascending time order.</summary>
    public IReadOnlyList<TrendSample>? Samples
    {
        get => (IReadOnlyList<TrendSample>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    /// <summary>The left edge of the window, in UTC ticks.</summary>
    public long FromTicks
    {
        get => (long)GetValue(FromTicksProperty);
        set => SetValue(FromTicksProperty, value);
    }

    /// <summary>The right edge of the window, in UTC ticks.</summary>
    public long ToTicks
    {
        get => (long)GetValue(ToTicksProperty);
        set => SetValue(ToTicksProperty, value);
    }

    /// <summary>The unit shown against the axis labels.</summary>
    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    /// <summary>The smallest half-range the axis will show, in the series' own units.</summary>
    public double Floor
    {
        get => (double)GetValue(FloorProperty);
        set => SetValue(FloorProperty, value);
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _surface = GetTemplateChild("PART_Surface") as Canvas;
        Draw();
    }

    private static void OnChartChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TrendChart)d).Draw();

    private void Draw()
    {
        if (_surface is not Canvas surface)
        {
            return;
        }

        surface.Children.Clear();

        double width = surface.ActualWidth;
        double height = surface.ActualHeight;

        if (width < 8 || height < 8 || ToTicks <= FromTicks)
        {
            return;
        }

        IReadOnlyList<TrendColumn> columns = TrendDecimation.ToColumns(
            Samples ?? [], FromTicks, ToTicks, (int)Math.Floor(width));

        (double minimum, double maximum) = TrendDecimation.ZeroAnchoredBounds(columns, Floor);

        // The axis is symmetric about zero by construction, so the zero line is the vertical
        // centre. §9.4.4 requires the diverging fill's neutral point to be exactly there.
        double zeroY = height / 2;
        double scale = (height / 2) / maximum;

        DrawZeroLine(surface, width, zeroY);

        foreach (TrendColumn column in columns)
        {
            double x = column.Column + 0.5;
            double top = zeroY - (column.Maximum * scale);
            double bottom = zeroY - (column.Minimum * scale);

            // A single-sample column has top == bottom; give it a pixel so it still marks.
            if (Math.Abs(bottom - top) < 1)
            {
                double middle = (top + bottom) / 2;
                top = middle - 0.5;
                bottom = middle + 0.5;
            }

            surface.Children.Add(new Line
            {
                X1 = x,
                X2 = x,
                Y1 = Math.Clamp(top, 0, height),
                Y2 = Math.Clamp(bottom, 0, height),
                StrokeThickness = 1,
                Stroke = BrushFor(column),
            });
        }

        DrawAxisLabels(surface, width, height, maximum);
    }

    /// <summary>
    /// Picks the diverging colour for a column from which side of zero it sits on.
    /// </summary>
    /// <remarks>
    /// §9.4.4's ramp, keyed on the column's own extreme rather than on the data's midpoint. A
    /// column that straddles zero takes the neutral token, which is the honest answer: within that
    /// eight-minute bucket the receiver was on both sides.
    /// </remarks>
    private Brush BrushFor(TrendColumn column)
    {
        string key = column switch
        {
            { Minimum: < 0, Maximum: > 0 } => "WzDivergingZeroBrush",
            { Maximum: <= 0 } => "WzDivergingNegativeBrush",
            _ => "WzDivergingPositiveBrush",
        };

        return Resource<Brush>(key) ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    /// <summary>
    /// Looks a token up without being able to take the process down.
    /// </summary>
    /// <remarks>
    /// <b>Indexing <c>Application.Current.Resources</c> directly is a process-killer.</b> A key that
    /// is not there throws inside the draw path, and WinUI turns that into an uncatchable
    /// <c>0xc000027b</c> in <c>Microsoft.UI.Xaml.dll</c> — the same signature as
    /// <c>ApplicationData.Current</c> and enumerating <c>DisplayArea.FindAll</c>. This control did
    /// exactly that during development by asking for a <c>WzDividerBrush</c> that does not exist,
    /// and the application vanished on navigating to the page rather than reporting anything.
    /// <para>
    /// The fallback is deliberately visible rather than pretty. A grey line that looks wrong is a
    /// bug someone fixes; a silently skipped stroke is a chart that quietly draws nothing.
    /// </para>
    /// </remarks>
    private static T? Resource<T>(string key)
        where T : class =>
        Application.Current.Resources.TryGetValue(key, out object? value) ? value as T : null;

    private void DrawZeroLine(Canvas surface, double width, double zeroY) =>
        surface.Children.Add(new Line
        {
            X1 = 0,
            X2 = width,
            Y1 = zeroY,
            Y2 = zeroY,
            StrokeThickness = 1,
            Stroke = Resource<Brush>("WzStrokeDefaultBrush") ?? new SolidColorBrush(Microsoft.UI.Colors.Gray),
        });

    /// <remarks>
    /// Three labels and no more: the two extremes and zero. §9.1's restraint applies to a chart as
    /// much as to a readout — a grid of ten tick labels is decoration on a plot whose job is to
    /// show a shape.
    /// </remarks>
    private void DrawAxisLabels(Canvas surface, double width, double height, double maximum)
    {
        Add($"+{maximum:F0} {Unit}", 0);
        Add($"0 {Unit}", (height / 2) - 8);
        Add($"−{maximum:F0} {Unit}", height - 16);

        void Add(string text, double top)
        {
            TextBlock label = new()
            {
                Text = text,
                Style = Resource<Style>("WzCaptionTextStyle"),
                Foreground = Resource<Brush>("WzTextTertiaryBrush"),
            };

            label.Measure(new Size(width, height));
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, top);
            surface.Children.Add(label);
        }
    }
}
