using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace WinZ3805A.Controls;

/// <summary>The three medallion diameters §9.10.2 sanctions.</summary>
public enum MedallionSize
{
    /// <summary>64 px. The §10.3 compact main window.</summary>
    Compact = 64,

    /// <summary>96 px. Beside a page heading.</summary>
    Standard = 96,

    /// <summary>160 px. The §10.3 main window.</summary>
    Large = 160,
}

/// <summary>
/// The application's signature element: a mode glyph wrapped by a live 60-sample radial sparkline
/// of the 1 PPS time interval (§9.10.2, §10.3, P0-18).
/// </summary>
/// <remarks>
/// <para>
/// One object answers both questions a two-second glance is asking — what state the receiver is in,
/// and how well it is behaving. A calm ring means a calm loop; a ring that grows teeth means the
/// loop is hunting, and that is visible before any figure of merit changes.
/// </para>
/// <para>
/// <b>The ring is qualitative and must never be read for values.</b> The figure itself is always
/// set beside it in <c>WzReadoutMedium</c>. That is why the scale adapts (§9.10.2): absolute
/// nanoseconds would make a good receiver draw a flat line forever and a poor one clip. The
/// arithmetic is in <see cref="MedallionRingMath"/>, which is tested.
/// </para>
/// <para>
/// <b>Nothing here animates.</b> §9.8.2 gives the ring redraw <c>WzDurationInstant</c> and §9.13
/// item 7 forbids it pulsing in holdover — it changes colour, shape and glyph at once instead,
/// which is louder precisely because everything else on the window is still. No
/// <c>Storyboard</c> touches this geometry; the ring is rebuilt and assigned.
/// </para>
/// <para>
/// The circle is reserved to this control alone (§9.3). Everything else in the application is a 4
/// or 8 px radius, and that reservation is what lets the eye find the medallion without focusing.
/// </para>
/// </remarks>
public sealed class StatusMedallion : Control
{
    private const string RingPart = "PART_Ring";
    private const string PlainRingPart = "PART_PlainRing";
    private const string GlyphPart = "Glyph";
    private const string CountPart = "Count";


    /// <summary>What fraction of the radius the sparkline band occupies.</summary>
    private const double BandFraction = 0.16;

    /// <summary>How much of each slot's angular width the bar fills, leaving a hairline gap.</summary>
    private const double BarDutyCycle = 0.55;

    /// <summary>Identifies the <see cref="Mode"/> dependency property.</summary>
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(ReceiverMode), typeof(StatusMedallion),
        new PropertyMetadata(ReceiverMode.Disconnected, OnModeChanged));

    /// <summary>Identifies the <see cref="Samples"/> dependency property.</summary>
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(IReadOnlyList<double?>), typeof(StatusMedallion),
        new PropertyMetadata(null, OnVisualChanged));

    /// <summary>Identifies the <see cref="Size"/> dependency property.</summary>
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(MedallionSize), typeof(StatusMedallion),
        new PropertyMetadata(MedallionSize.Large, OnSizeChanged));

    /// <summary>Identifies the <see cref="SatelliteCount"/> dependency property.</summary>
    public static readonly DependencyProperty SatelliteCountProperty = DependencyProperty.Register(
        nameof(SatelliteCount), typeof(int?), typeof(StatusMedallion),
        new PropertyMetadata(null, OnAnnouncementChanged));

    /// <summary>Identifies the <see cref="TimeIntervalNanoseconds"/> dependency property.</summary>
    public static readonly DependencyProperty TimeIntervalNanosecondsProperty = DependencyProperty.Register(
        nameof(TimeIntervalNanoseconds), typeof(double?), typeof(StatusMedallion),
        new PropertyMetadata(null, OnAnnouncementChanged));

    /// <summary>Identifies the <see cref="ModeDetail"/> dependency property.</summary>
    public static readonly DependencyProperty ModeDetailProperty = DependencyProperty.Register(
        nameof(ModeDetail), typeof(string), typeof(StatusMedallion),
        new PropertyMetadata(null, OnAnnouncementChanged));

    /// <summary>
    /// Identifies the <see cref="PlainRingThickness"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty PlainRingThicknessProperty = DependencyProperty.Register(
        nameof(PlainRingThickness), typeof(double), typeof(StatusMedallion),
        new PropertyMetadata(0d, OnVisualChanged));

    /// <summary>Initialises a new medallion.</summary>
    public StatusMedallion()
    {
        DefaultStyleKey = typeof(StatusMedallion);
    }

    /// <summary>What the receiver is doing. Drives severity, glyph and label together (§10.3).</summary>
    public ReceiverMode Mode
    {
        get => (ReceiverMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>The time-interval window, oldest first, with nulls for polls that did not land.</summary>
    public IReadOnlyList<double?>? Samples
    {
        get => (IReadOnlyList<double?>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    /// <summary>Which of the three §9.10.2 diameters to draw.</summary>
    public MedallionSize Size
    {
        get => (MedallionSize)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>Satellites tracked, for the spoken description.</summary>
    public int? SatelliteCount
    {
        get => (int?)GetValue(SatelliteCountProperty);
        set => SetValue(SatelliteCountProperty, value);
    }

    /// <summary>The current time interval in nanoseconds, for the spoken description.</summary>
    public double? TimeIntervalNanoseconds
    {
        get => (double?)GetValue(TimeIntervalNanosecondsProperty);
        set => SetValue(TimeIntervalNanosecondsProperty, value);
    }

    /// <summary>The qualifier after the mode, such as "stabilising frequency".</summary>
    public string? ModeDetail
    {
        get => (string?)GetValue(ModeDetailProperty);
        set => SetValue(ModeDetailProperty, value);
    }

    /// <summary>
    /// When greater than zero, the ring is drawn as a plain stroke of this thickness instead of a
    /// sparkline.
    /// </summary>
    /// <remarks>
    /// This is how §9.10.2's high-contrast rule is honoured without the control asking the system
    /// what theme it is in. The default style sets it from a theme resource that is 0 in Light and
    /// Dark and 2 in HighContrast, so the switch happens through the same mechanism as every other
    /// token and re-resolves when the user changes theme. In that mode severity is carried by the
    /// glyph and the label, exactly as §9.10.2 requires — sixty small marks in a single system
    /// colour would be noise rather than information.
    /// </remarks>
    public double PlainRingThickness
    {
        get => (double)GetValue(PlainRingThicknessProperty);
        set => SetValue(PlainRingThicknessProperty, value);
    }

    /// <summary>The severity this mode carries (§10.3), for the template to colour from.</summary>
    public Severity Severity => ReceiverModes.SeverityOf(Mode);

    /// <inheritdoc />
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        ApplySize();
        UpdateVisualState();
        Redraw();
        UpdateAnnouncement();
    }

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var medallion = (StatusMedallion)d;
        medallion.UpdateVisualState();
        medallion.UpdateAnnouncement();
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var medallion = (StatusMedallion)d;
        medallion.Redraw();
        medallion.UpdateAnnouncement();
    }

    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var medallion = (StatusMedallion)d;
        medallion.ApplySize();
        medallion.Redraw();
    }

    private static void OnAnnouncementChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        StatusMedallion medallion = (StatusMedallion)d;
        medallion.UpdateAnnouncement();

        // SatelliteCount is drawn as well as announced now (#279), and it shares this callback with
        // the other two properties that only feed the sentence. Redrawing the centre for all three
        // is cheaper than a second callback and cannot fall out of step with the announcement.
        medallion.UpdateCentre();
    }

    private void ApplySize()
    {
        double diameter = (double)(int)Size;
        Width = diameter;
        Height = diameter;

        if (GetTemplateChild(GlyphPart) is TextBlock glyph)
        {
            glyph.FontSize = MedallionRingMath.GlyphSize(diameter);
        }

        if (GetTemplateChild(CountPart) is TextBlock count)
        {
            count.FontSize = MedallionRingMath.CountSize(diameter);
        }

        UpdateCentre();
    }

    /// <summary>
    /// Chooses what the centre holds: the tracked-satellite count, or the mode glyph (#279).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The count replaces the glyph in compact, and only in compact.</b> G1 asks for mode and
    /// count legible at two metres and §9.6.2 names those two as the only things compact keeps, so
    /// the number earns the centre there. At Standard and Large the readout row is on screen and
    /// already carries the count; putting it here as well would print it twice.
    /// </para>
    /// <para>
    /// <b>The glyph is the fallback when there is no count</b>, rather than §11.1's em dash. The
    /// dash is right in a readout, where a column of figures needs a placeholder holding its
    /// column; in a 64 px circle it is a wide bar that says less than the shape it replaced. So a
    /// receiver that has not reported a count shows the state, which is the thing it does know.
    /// </para>
    /// <para>
    /// <b>The medallion carries colour only while the count is shown</b>, and that is a deliberate
    /// exception recorded in §9.4.3 rather than an oversight. §9.6.2 keeps the mode text beside the
    /// medallion in compact, always and in words, so the state is on the surface in a second
    /// channel - just not inside the circle. The alternatives were worse: the ring already carries
    /// the sixty-sample sparkline, and a numeral sized for two metres leaves no room beside it.
    /// </para>
    /// </remarks>
    private void UpdateCentre()
    {
        if (GetTemplateChild(GlyphPart) is not TextBlock glyph
            || GetTemplateChild(CountPart) is not TextBlock count)
        {
            return;
        }

        bool showCount = Size == MedallionSize.Compact && SatelliteCount is int;

        count.Text = SatelliteCount is int satellites
            ? satellites.ToString(System.Globalization.CultureInfo.CurrentCulture)
            : string.Empty;

        count.Visibility = showCount ? Visibility.Visible : Visibility.Collapsed;
        glyph.Visibility = showCount ? Visibility.Collapsed : Visibility.Visible;
    }


    /// <remarks>
    /// Always without transitions. §9.8.2 gives severity changes <c>WzDurationInstant</c>, and a
    /// medallion that eased between colours would be movement in peripheral vision on a window
    /// left running for weeks.
    /// </remarks>
    private void UpdateVisualState() =>
        VisualStateManager.GoToState(this, Mode.ToString(), useTransitions: false);

    /// <summary>
    /// Rebuilds the ring geometry. Called on every fast poll, which is once a second.
    /// </summary>
    /// <remarks>
    /// The geometry is constructed and assigned outright rather than animated between states —
    /// P0-18 requires that no <c>Storyboard</c> targets it. Sixty short line segments in one
    /// <see cref="PathGeometry"/> is cheap enough to do at 1 Hz without any of that.
    /// </remarks>
    private void Redraw()
    {
        if (GetTemplateChild(RingPart) is not Microsoft.UI.Xaml.Shapes.Path ring)
        {
            return;
        }

        bool plain = PlainRingThickness > 0;
        if (GetTemplateChild(PlainRingPart) is Ellipse plainRing)
        {
            plainRing.Visibility = plain ? Visibility.Visible : Visibility.Collapsed;
            plainRing.StrokeThickness = PlainRingThickness;
        }

        if (plain)
        {
            ring.Visibility = Visibility.Collapsed;
            return;
        }

        ring.Visibility = Visibility.Visible;

        double diameter = (double)(int)Size;
        double centre = diameter / 2;
        double band = diameter * BandFraction;
        double midRadius = centre - (band / 2) - 1;

        IReadOnlyList<double?> samples = Samples ?? [];
        int slots = ReceiverStateStoreWindow;
        double halfRange = MedallionRingMath.HalfRange(samples);

        PathGeometry geometry = new();

        // Oldest sample at the top, running clockwise, so the newest is the mark just anticlockwise
        // of twelve o'clock. A fixed slot per position means the ring does not shuffle as the window
        // fills — a moving baseline would read as motion in the data.
        for (int i = 0; i < samples.Count && i < slots; i++)
        {
            double? fraction = MedallionRingMath.Fraction(samples[i], halfRange);
            if (fraction is not double value)
            {
                continue;
            }

            double angle = ((i / (double)slots) * 2 * Math.PI) - (Math.PI / 2);
            double inner = midRadius;
            double outer = midRadius + (value * band / 2);

            // A reading of exactly zero still deserves a mark, or a perfect loop would look like a
            // dead one. The minimum tick is what distinguishes "on target" from "no data".
            if (Math.Abs(outer - inner) < 1)
            {
                outer = inner + Math.Sign(value == 0 ? 1 : value);
            }

            geometry.Figures.Add(Segment(centre, angle, inner, outer, band));
        }

        ring.Data = geometry;
        ring.StrokeThickness = Math.Max(1, (2 * Math.PI * midRadius / slots) * BarDutyCycle);
    }

    /// <summary>One radial tick, as a line figure from the baseline outward or inward.</summary>
    private static PathFigure Segment(double centre, double angle, double inner, double outer, double band)
    {
        _ = band;
        double cos = Math.Cos(angle);
        double sin = Math.Sin(angle);

        PathFigure figure = new()
        {
            StartPoint = new Windows.Foundation.Point(centre + (inner * cos), centre + (inner * sin)),
            IsClosed = false,
            IsFilled = false,
        };

        figure.Segments.Add(new LineSegment
        {
            Point = new Windows.Foundation.Point(centre + (outer * cos), centre + (outer * sin)),
        });

        return figure;
    }

    /// <summary>
    /// The window length the ring draws, matching <c>ReceiverStateStore.TimeIntervalWindow</c>.
    /// </summary>
    /// <remarks>
    /// Named as a constant here rather than referencing the store, so the control has no dependency
    /// on a service; §9.10.2 fixes the number at sixty independently of where the samples come from.
    /// </remarks>
    private const int ReceiverStateStoreWindow = 60;

    /// <summary>
    /// Builds the full sentence §9.10.2 requires, such as <em>"Locked to GPS, stabilising frequency,
    /// 6 satellites tracked, time interval −33.1 nanoseconds."</em>
    /// </summary>
    /// <remarks>
    /// A sentence rather than a label, because a screen-reader user gets one utterance and has to
    /// hear the whole state in it. The ring is <c>Raw</c> in the template: it is a qualitative
    /// restatement of the number already spoken here, and announcing sixty unnamed marks would bury
    /// the meaning.
    /// </remarks>
    private void UpdateAnnouncement()
    {
        List<string> parts = [ReceiverModes.TextOf(Mode)];

        if (!string.IsNullOrWhiteSpace(ModeDetail))
        {
            parts.Add(ModeDetail.Trim());
        }

        if (SatelliteCount is int satellites)
        {
            parts.Add(satellites == 1 ? "1 satellite tracked" : $"{satellites} satellites tracked");
        }

        if (TimeIntervalNanoseconds is double interval)
        {
            string formatted = ReadoutFormatter.Format(interval, 1, CultureInfo.CurrentCulture);
            parts.Add($"time interval {formatted} nanoseconds");
        }

        AutomationProperties.SetName(this, string.Join(", ", parts) + ".");
    }
}
