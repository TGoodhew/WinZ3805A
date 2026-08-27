using System.Globalization;

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

    /// <summary>Identifies the <see cref="States"/> dependency property.</summary>
    /// <summary>Identifies the <see cref="Anchoring"/> dependency property.</summary>
    public static readonly DependencyProperty AnchoringProperty = DependencyProperty.Register(
        nameof(Anchoring),
        typeof(TrendAnchoring),
        typeof(TrendChart),
        new PropertyMetadata(TrendAnchoring.Zero, OnChartChanged));

    /// <summary>Identifies the <see cref="MinimumSpan"/> dependency property.</summary>
    public static readonly DependencyProperty MinimumSpanProperty = DependencyProperty.Register(
        nameof(MinimumSpan),
        typeof(double),
        typeof(TrendChart),
        new PropertyMetadata(1.0, OnChartChanged));

    /// <summary>Identifies the <see cref="Decimals"/> dependency property.</summary>
    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(
        nameof(Decimals),
        typeof(int),
        typeof(TrendChart),
        new PropertyMetadata(0, OnChartChanged));

    public static readonly DependencyProperty StatesProperty = DependencyProperty.Register(
        nameof(States),
        typeof(IReadOnlyList<TrendSample>),
        typeof(TrendChart),
        new PropertyMetadata(null, OnChartChanged));

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

    /// <summary>
    /// The receiver's state over the same window, for §49's background shading.
    /// </summary>
    /// <remarks>
    /// <c>Value</c> carries a <see cref="ReceiverMode"/> cast to a double. Shading is drawn only
    /// where the mode is <b>not</b> locked, so a healthy trace has a plain background and the eye
    /// is drawn to the stretches that were not — which is the §9.1 argument that the interesting
    /// state is the one that should stand out, not the ordinary one.
    /// </remarks>
    public IReadOnlyList<TrendSample>? States
    {
        get => (IReadOnlyList<TrendSample>?)GetValue(StatesProperty);
        set => SetValue(StatesProperty, value);
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

    /// <summary>What the y-axis is framed on (§10.7.1). Zero by default, which is §9.10.2's TI rule.</summary>
    public TrendAnchoring Anchoring
    {
        get => (TrendAnchoring)GetValue(AnchoringProperty);
        set => SetValue(AnchoringProperty, value);
    }

    /// <summary>
    /// The smallest total range a <see cref="TrendAnchoring.Data"/> axis will show, in the value's
    /// own units. Ignored when <see cref="Anchoring"/> is <see cref="TrendAnchoring.Zero"/>, which
    /// uses <see cref="Floor"/> instead.
    /// </summary>
    /// <remarks>
    /// Two properties rather than one that changes meaning with the mode. A half-range and a span
    /// differ by a factor of two, and a number whose unit depends on a neighbouring property is how
    /// #101 and #27 happened.
    /// </remarks>
    public double MinimumSpan
    {
        get => (double)GetValue(MinimumSpanProperty);
        set => SetValue(MinimumSpanProperty, value);
    }

    /// <summary>
    /// Decimal places on the axis labels. <b>Fixed per chart, never varying with the range</b>
    /// (§9.11 item 6).
    /// </summary>
    public int Decimals
    {
        get => (int)GetValue(DecimalsProperty);
        set => SetValue(DecimalsProperty, value);
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

        (double minimum, double maximum) = Anchoring == TrendAnchoring.Data
            ? TrendDecimation.AutoBounds(columns, MinimumSpan)
            : TrendDecimation.ZeroAnchoredBounds(columns, Floor);

        double span = maximum - minimum;

        if (span <= 0 || double.IsNaN(span) || double.IsInfinity(span))
        {
            return;
        }

        // General in the bounds rather than symmetric about zero. Under Zero anchoring minimum is
        // exactly -maximum, so the middle of the plot is still exactly 0 and §9.4.4's requirement
        // that the diverging fill's neutral point map to zero is met by the same arithmetic.
        double scale = height / span;

        DrawStateShading(surface, height);
        DrawMidLine(surface, width, height / 2);

        foreach (TrendColumn column in columns)
        {
            double x = column.Column + 0.5;
            double top = height - ((column.Maximum - minimum) * scale);
            double bottom = height - ((column.Minimum - minimum) * scale);

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

        DrawAxisLabels(surface, width, height, minimum, maximum);
        DrawClippedNote(surface, columns, width, minimum, maximum);
    }

    /// <summary>
    /// Names anything the axis leaves out, so nothing is dropped silently (#209).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The axis is framed on the range the bulk of the window occupies rather than on its extremes,
    /// because one aberrant reading otherwise sets the scale for a week. That is only defensible if
    /// the chart says when it has done it: <b>an excursion is the diagnostic content on a timing
    /// instrument</b>, and a rule that quietly rescales around the largest one is worse than an
    /// unreadable axis.
    /// </para>
    /// <para>
    /// So the trace is still drawn for every column — the draw path clamps to the plot edge, so an
    /// excluded extreme appears pinned to the top or bottom — and this says how many there were and
    /// how far the furthest went. It appears only when something is outside, which is almost never.
    /// </para>
    /// </remarks>
    private void DrawClippedNote(
        Canvas surface,
        IReadOnlyList<TrendColumn> columns,
        double width,
        double minimum,
        double maximum)
    {
        (int count, double? extreme) = TrendDecimation.Outside(columns, minimum, maximum);

        if (count == 0 || extreme is not double furthest)
        {
            return;
        }

        TextBlock note = new()
        {
            Text = $"{count} beyond the axis, to {Format(furthest)}",
            Style = Resource<Style>("WzCaptionTextStyle"),
            Foreground = Resource<Brush>("WzTextTertiaryBrush"),
        };

        note.Measure(new Size(width, double.PositiveInfinity));
        Canvas.SetLeft(note, Math.Max(0, width - note.DesiredSize.Width));
        Canvas.SetTop(note, 0);
        surface.Children.Add(note);
    }

    /// <summary>
    /// Shades the columns where the receiver was not locked (#49).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Drawn first, so the trace sits on top of it rather than under it, and at low opacity so it
    /// reads as ground rather than as data. A shaded stretch is context for the trace above it —
    /// an excursion during holdover means something different from the same excursion while
    /// locked, and without this the two are indistinguishable.
    /// </para>
    /// <para>
    /// <b>Colour is not the only channel here either.</b> Shading marks a region rather than
    /// encoding a value, and the caption under the chart names what it means in words, so a reader
    /// who cannot see the tint is not being denied a reading (§9.4.3, A11Y-12).
    /// </para>
    /// </remarks>
    private void DrawStateShading(Canvas surface, double height)
    {
        if (States is not { Count: > 0 } states)
        {
            return;
        }

        IReadOnlyList<(int Column, int State)> shaded = TrendDecimation.ToStateColumns(
            states, FromTicks, ToTicks, (int)Math.Floor(surface.ActualWidth));

        Brush? caution = Resource<Brush>("WzCautionBrush");
        if (caution is null)
        {
            return;
        }

        foreach ((int column, int state) in shaded)
        {
            if ((ReceiverMode)state == ReceiverMode.Locked)
            {
                continue;
            }

            surface.Children.Add(new Rectangle
            {
                Width = 1,
                Height = height,
                Fill = caution,
                Opacity = 0.18,
                Margin = new Thickness(column, 0, 0, 0),
            });
        }
    }

    /// <summary>
    /// Picks the stroke for a column: §9.4.4's diverging ramp, or one flat series colour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The diverging ramp is keyed on the column's own extreme rather than on the data's midpoint.
    /// A column that straddles zero takes the neutral token, which is the honest answer: within
    /// that eight-minute bucket the receiver was on both sides.
    /// </para>
    /// <para>
    /// <b>It applies only to a zero-anchored axis, and that is not a coincidence.</b> §9.4.4 asks
    /// the neutral midpoint to map to <i>exactly</i> zero; on an axis that does not contain zero
    /// there is nothing for it to map to, and anchoring it on the window mean would make the same
    /// colour break mean "on time" on one chart and "near where it has lately been" on the other.
    /// A data-framed series therefore takes a single stroke from §9.4.4's categorical palette and
    /// carries no colour-borne value at all — which is also the first thing in the application to
    /// consume that palette.
    /// </para>
    /// </remarks>
    private Brush BrushFor(TrendColumn column)
    {
        string key = Anchoring == TrendAnchoring.Data
            ? "WzSeries1Brush"
            : column switch
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

    /// <summary>One axis figure: U+2212 for a negative, never a hyphen, at this chart's precision.</summary>
    /// <remarks>§9.5.3 and P0-20. The decimal count is fixed per chart and never varies with the range.</remarks>
    private string Format(double value) => value switch
    {
        < 0 => $"\u2212{Math.Abs(value).ToString($"F{Decimals}", CultureInfo.InvariantCulture)} {Unit}",
        > 0 => $"+{value.ToString($"F{Decimals}", CultureInfo.InvariantCulture)} {Unit}",
        _ => $"{0d.ToString($"F{Decimals}", CultureInfo.InvariantCulture)} {Unit}",
    };

    /// <remarks>
    /// The line under the middle axis label. On a zero-anchored chart that is zero itself; on a
    /// data-framed one it is the midpoint of the window, and it is drawn because three labels down
    /// the left edge are easier to read against a rule than against nothing.
    /// </remarks>
    private void DrawMidLine(Canvas surface, double width, double zeroY) =>
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
    /// Three labels and no more: the two extremes and the midpoint. §9.1's restraint applies to a
    /// chart as much as to a readout — a grid of ten tick labels is decoration on a plot whose job
    /// is to show a shape. On a zero-anchored axis the midpoint is zero, so this reads exactly as
    /// it always did.
    /// </remarks>
    private void DrawAxisLabels(Canvas surface, double width, double height, double minimum, double maximum)
    {
        Add(Format(maximum), 0);
        Add(Format((minimum + maximum) / 2), (height / 2) - 8);
        Add(Format(minimum), height - 16);

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
