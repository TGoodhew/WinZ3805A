using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

using Windows.System;

namespace WinZ3805A.Controls;

/// <summary>
/// The §10.5 sky plot: a polar view of what the receiver can see (P0-9, §9.10.2).
/// </summary>
/// <remarks>
/// <para>
/// North up, 0° elevation at the rim, 90° at the centre, with a dashed circle at the elevation
/// mask. Marker area scales with signal strength and fill comes from §9.4.4's sequential ramp. The
/// projection itself lives in <see cref="SkyPlotGeometry"/>, where it is tested against
/// hand-computed positions.
/// </para>
/// <para>
/// <b>Markers are real elements, not painted geometry.</b> Each is a <c>Button</c> carrying the
/// sentence §9.10.2 specifies as its automation name, which makes the automation tree correct by
/// construction rather than by a hand-written peer that has to be kept in step with the drawing.
/// A11Y-10 is then a property of the thing that is actually on screen. They are not tab stops: the
/// plot is one stop and arrow keys move within it (§9.10.2), so a plot of twelve satellites does
/// not cost twelve tabs to walk past.
/// </para>
/// <para>
/// <b>Colour is never the only channel.</b> Tracked is a filled disc and predicted a hollow ring,
/// so the two are told apart in greyscale and under every form of colour blindness (§9.4.3,
/// A11Y-12). The ramp adds precision for those who can see it, and the tables below the plot carry
/// the same numbers for those who cannot. A11Y-11's non-spatial alternate is the Plot/List toggle
/// on the same card, not those tables — see <c>SatellitesPage</c> for why the distinction matters.
/// </para>
/// <para>
/// Hand-drawn on a <c>Canvas</c>, with no charting dependency (P0-9). The shapes involved are
/// circles, a cross and some text; a charting library would be a package, a licence and a
/// theming problem in exchange for none of that.
/// </para>
/// </remarks>
public sealed class SkyPlotControl : Control
{
    /// <summary>Identifies the <see cref="Satellites"/> dependency property.</summary>
    public static readonly DependencyProperty SatellitesProperty = DependencyProperty.Register(
        nameof(Satellites),
        typeof(IReadOnlyList<SkyPlotSatellite>),
        typeof(SkyPlotControl),
        new PropertyMetadata(null, OnPlotChanged));

    /// <summary>Identifies the <see cref="ElevationMaskDegrees"/> dependency property.</summary>
    public static readonly DependencyProperty ElevationMaskDegreesProperty = DependencyProperty.Register(
        nameof(ElevationMaskDegrees),
        typeof(int?),
        typeof(SkyPlotControl),
        new PropertyMetadata(null, OnPlotChanged));

    /// <summary>Identifies the <see cref="SelectedPrn"/> dependency property.</summary>
    public static readonly DependencyProperty SelectedPrnProperty = DependencyProperty.Register(
        nameof(SelectedPrn),
        typeof(int?),
        typeof(SkyPlotControl),
        new PropertyMetadata(null, OnSelectionChanged));

    /// <summary>Identifies the <see cref="MaxPlotSize"/> dependency property.</summary>
    public static readonly DependencyProperty MaxPlotSizeProperty = DependencyProperty.Register(
        nameof(MaxPlotSize),
        typeof(double),
        typeof(SkyPlotControl),
        new PropertyMetadata(360.0, OnPlotChanged));

    /// <summary>Pixels of margin inside the control, leaving room for the cardinal labels.</summary>
    private const double Inset = 18;

    /// <summary>Marker radius at the bottom and the top of the signal scale.</summary>
    private const double MinMarkerRadius = 4;
    private const double MaxMarkerRadius = 9;

    /// <summary>A predicted satellite has no strength to size it by, so it takes one size.</summary>
    private const double PredictedMarkerRadius = 5.5;

    /// <summary>
    /// The clickable square around a marker, which is much larger than the marker itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A11Y-5 asks for 32 x 32 px pointer targets and a signal-strength marker is 8 to 18 px
    /// across, so the visible disc is inset inside a transparent button that is easier to hit.
    /// </para>
    /// <para>
    /// <b>24 px rather than A11Y-5's 32.</b> A marker's position is the satellite's actual position
    /// in the sky and cannot be moved to make room — the same "essential position" case WCAG 2.5.8
    /// carves out — so past a point, enlarging the target stops helping and starts stealing a
    /// neighbouring satellite's clicks, which is worse than a small target because it silently
    /// selects the wrong one. 24 px gives about four times the disc's own clickable area without
    /// reaching a neighbour under 6 degrees away, which is the figure §9.10.2 gives. The compliant
    /// paths to every satellite are the keyboard model and the list alternate on the same card,
    /// which is what §9.10.2 requires the alternate view for — its rows are full-width and clear
    /// <b>both</b> of A11Y-5's floors, the 40 px touch one included, which is what makes them an
    /// alternate rather than a second exception. That was not true when this comment was first
    /// written: the rows measured 26 px (#25). Recorded as #117 rather than left as an oversight.
    /// </para>
    /// </remarks>
    private const double MarkerHitSize = 24;

    private Canvas? _canvas;
    private Panel? _surface;

    /// <summary>The satellites currently plotted, in PRN order — the keyboard's order too.</summary>
    private IReadOnlyList<SkyPlotSatellite> _plotted = [];

    /// <summary>
    /// Which satellite the arrow keys are on, <b>by PRN</b>, or null before they have moved.
    /// </summary>
    /// <remarks>
    /// A PRN and not an index into <see cref="_plotted"/>, which is rebuilt and re-sorted on every
    /// reading. See <see cref="SkyPlotCursor"/> for what an index cost and what a PRN buys.
    /// </remarks>
    private int? _cursorPrn;

    /// <summary>Initialises a new sky plot.</summary>
    public SkyPlotControl()
    {
        DefaultStyleKey = typeof(SkyPlotControl);

        IsTabStop = true;
        UseSystemFocusVisuals = true;

        SizeChanged += (_, _) => Rebuild();
        GotFocus += (_, _) => UpdateCursorRing();
        LostFocus += (_, _) => UpdateCursorRing();
        ActualThemeChanged += (_, _) => Rebuild();
    }

    /// <summary>Raised when the user picks a satellite, by click, tap or Enter.</summary>
    public event EventHandler<int>? SatelliteInvoked;

    /// <summary>What to draw. Satellites without both angles are skipped.</summary>
    public IReadOnlyList<SkyPlotSatellite>? Satellites
    {
        get => (IReadOnlyList<SkyPlotSatellite>?)GetValue(SatellitesProperty);
        set => SetValue(SatellitesProperty, value);
    }

    /// <summary>The mask angle for the dashed circle, or null to omit it.</summary>
    public int? ElevationMaskDegrees
    {
        get => (int?)GetValue(ElevationMaskDegreesProperty);
        set => SetValue(ElevationMaskDegreesProperty, value);
    }

    /// <summary>Which satellite is highlighted, kept in step with the table beside the plot.</summary>
    public int? SelectedPrn
    {
        get => (int?)GetValue(SelectedPrnProperty);
        set => SetValue(SelectedPrnProperty, value);
    }

    /// <summary>
    /// The largest the plot will draw itself, whatever room it is given. §9.6.1's Compact row caps
    /// it at 360 px, and there is nothing to gain above that: the plot carries twelve markers and a
    /// circle, and a metre of ultrawide spent on it is a metre not spent on the table.
    /// </summary>
    public double MaxPlotSize
    {
        get => (double)GetValue(MaxPlotSizeProperty);
        set => SetValue(MaxPlotSizeProperty, value);
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _canvas = GetTemplateChild("PART_Canvas") as Canvas;
        _surface = GetTemplateChild("PART_Surface") as Panel;

        Rebuild();
    }

    /// <inheritdoc />
    /// <remarks>
    /// §9.10.2's keyboard model: arrows move a ring between markers in PRN order, Enter selects.
    /// All four arrows walk the same order rather than steering by compass bearing — a spatial
    /// model sounds right and is not, because two satellites can share a bearing and one at the
    /// zenith has none, so "the next one to the left" is undefined exactly where a user is most
    /// likely to press it.
    /// </remarks>
    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_plotted.Count == 0)
        {
            base.OnKeyDown(e);
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Right:
            case VirtualKey.Down:
                MoveCursor(1);
                e.Handled = true;
                return;

            case VirtualKey.Left:
            case VirtualKey.Up:
                MoveCursor(-1);
                e.Handled = true;
                return;

            case VirtualKey.Home:
                SetCursor(PlottedPrns[0]);
                e.Handled = true;
                return;

            case VirtualKey.End:
                SetCursor(PlottedPrns[^1]);
                e.Handled = true;
                return;

            case VirtualKey.Enter:
            case VirtualKey.Space:
                // Only a satellite that is currently plotted can be selected. A cursor left on a
                // PRN that has since dropped out selects nothing at all rather than whatever now
                // occupies its old place, which is the whole point of keying on the PRN.
                if (CursorSatellite is { } target)
                {
                    Invoke(target.Prn);
                    e.Handled = true;
                }

                return;

            default:
                base.OnKeyDown(e);
                return;
        }
    }

    // ===========================================================================================

    private static void OnPlotChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((SkyPlotControl)sender).Rebuild();

    private static void OnSelectionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((SkyPlotControl)sender).Rebuild();

    /// <summary>The satellite the cursor is on, or null when its PRN is not currently plotted.</summary>
    private SkyPlotSatellite? CursorSatellite =>
        _cursorPrn is int prn ? _plotted.FirstOrDefault(satellite => satellite.Prn == prn) : null;

    /// <summary>
    /// §9.10.2's keyboard order, as <see cref="SkyPlotCursor"/> wants it.
    /// </summary>
    /// <remarks>
    /// Projected on demand rather than cached beside <see cref="_plotted"/>. A dozen ints per key
    /// press costs nothing, and a second field would be one more thing that has to be rebuilt in
    /// step with the first — which is the class of mistake this whole change is repairing.
    /// </remarks>
    private IReadOnlyList<int> PlottedPrns => _plotted.Select(satellite => satellite.Prn).ToList();

    private void MoveCursor(int delta) => SetCursor(SkyPlotCursor.Step(PlottedPrns, _cursorPrn, delta));

    private void SetCursor(int? prn)
    {
        _cursorPrn = prn;
        UpdateCursorRing();
    }

    private void Invoke(int prn)
    {
        SelectedPrn = prn;
        SatelliteInvoked?.Invoke(this, prn);
    }

    /// <summary>Redraws everything. Cheap enough at a dozen markers on a 10 s cadence.</summary>
    private void Rebuild()
    {
        if (_canvas is null)
        {
            return;
        }

        _canvas.Children.Clear();

        double side = Math.Min(MaxPlotSize, Math.Max(ActualWidth, 0));
        if (side <= 0)
        {
            side = MaxPlotSize;
        }

        if (_surface is not null)
        {
            _surface.Width = side;
            _surface.Height = side;
        }

        _canvas.Width = side;
        _canvas.Height = side;

        double centre = side / 2;
        double radius = centre - Inset;
        if (radius <= 0)
        {
            return;
        }

        DrawRings(centre, radius);
        DrawCardinals(centre, radius);
        DrawMask(centre, radius);
        DrawMarkers(centre, radius);
        UpdateCursorRing();
    }

    private void DrawRings(double centre, double radius)
    {
        // The horizon, and the 30° and 60° parallels. Three is enough to read elevation off the
        // plot without the rings competing with the markers for attention.
        foreach (double elevation in new[] { 0d, 30d, 60d })
        {
            double r = SkyPlotGeometry.MaskRadius(elevation, radius);
            Ellipse ring = new()
            {
                Width = r * 2,
                Height = r * 2,
                Stroke = Brush("WzStrokeDefaultBrush"),
                StrokeThickness = 1,
                Fill = elevation == 0 ? Brush("WzLayerFillBrush") : null,
            };

            Canvas.SetLeft(ring, centre - r);
            Canvas.SetTop(ring, centre - r);
            _canvas!.Children.Add(ring);
        }

        // The cardinal cross, which is what makes north-up legible at a glance.
        foreach ((double x1, double y1, double x2, double y2) in new[]
        {
            (centre, centre - radius, centre, centre + radius),
            (centre - radius, centre, centre + radius, centre),
        })
        {
            Line spoke = new()
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = Brush("WzStrokeSubtleBrush"),
                StrokeThickness = 1,
            };

            _canvas!.Children.Add(spoke);
        }
    }

    private void DrawCardinals(double centre, double radius)
    {
        foreach ((string label, double azimuth) in new[] { ("N", 0d), ("E", 90d), ("S", 180d), ("W", 270d) })
        {
            TextBlock text = new()
            {
                Text = label,
                Foreground = Brush("WzTextSecondaryBrush"),
                Style = Resource<Style>("WzCaptionTextStyle"),
            };

            // Decoration. The compass is legible from the markers' own automation names, which
            // carry azimuth in degrees; four loose letters in the tree are noise.
            AutomationProperties.SetAccessibilityView(
                text, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

            (double x, double y) = SkyPlotGeometry.Project(0, azimuth, radius + (Inset / 2));

            // Measured before placing, because centring on a glyph needs its width and a TextBlock
            // that has not been measured reports zero.
            text.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

            Canvas.SetLeft(text, centre + x - (text.DesiredSize.Width / 2));
            Canvas.SetTop(text, centre + y - (text.DesiredSize.Height / 2));
            _canvas!.Children.Add(text);
        }
    }

    private void DrawMask(double centre, double radius)
    {
        if (ElevationMaskDegrees is not int mask)
        {
            return;
        }

        double r = SkyPlotGeometry.MaskRadius(mask, radius);

        Ellipse circle = new()
        {
            Width = r * 2,
            Height = r * 2,
            Stroke = Brush("WzCautionBrush"),
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 3],
        };

        AutomationProperties.SetName(circle, $"Elevation mask, {mask} degrees");
        AutomationProperties.SetAccessibilityView(circle, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

        Canvas.SetLeft(circle, centre - r);
        Canvas.SetTop(circle, centre - r);
        _canvas!.Children.Add(circle);
    }

    private void DrawMarkers(double centre, double radius)
    {
        // PRN order, which is the keyboard order and §9.4.4's rule for stable colour assignment:
        // a satellite keeps its place across sessions rather than moving as the table reshuffles.
        _plotted = (Satellites ?? [])
            .Where(satellite => satellite.CanPlot)
            .OrderBy(satellite => satellite.Prn)
            .ToList();

        // Nothing clamps or rewrites the cursor here, and that absence is the fix: this method runs
        // on every reading, and any adjustment made from it moves the ring at a moment the user did
        // not choose. _cursorPrn survives the rebuild untouched; UpdateCursorRing decides what can
        // be drawn for it.

        foreach (SkyPlotSatellite satellite in _plotted)
        {
            (double x, double y) = SkyPlotGeometry.Project(
                satellite.ElevationDegrees!.Value,
                satellite.AzimuthDegrees!.Value,
                radius);

            bool tracked = satellite.Marker == SkyPlotMarkerKind.Tracked;
            SignalStrengthScale scale = SignalStrengthScale.For(satellite.Kind);

            double markerRadius = tracked
                ? SkyPlotGeometry.MarkerRadius(satellite.SignalStrength, scale, MinMarkerRadius, MaxMarkerRadius)
                : PredictedMarkerRadius;

            Button marker = new()
            {
                Style = Resource<Style>("WzSkyPlotMarkerStyle"),
                Width = MarkerHitSize,
                Height = MarkerHitSize,
                Padding = new Thickness((MarkerHitSize / 2) - markerRadius),
                IsTabStop = false,
                Tag = satellite.Prn,
                Background = tracked
                    ? Brush($"WzSequential{SkyPlotGeometry.RampStep(satellite.SignalStrength, scale)}Brush")
                    : Brush("WzCardFillBrush"),
                BorderBrush = satellite.Marker switch
                {
                    SkyPlotMarkerKind.Tracked => Brush("WzStrokeDefaultBrush"),
                    SkyPlotMarkerKind.BelowMask or SkyPlotMarkerKind.Ignored => Brush("WzTextTertiaryBrush"),
                    SkyPlotMarkerKind.Acquiring => Brush("WzTextPrimaryBrush"),
                    _ => Brush("WzTextSecondaryBrush"),
                },

                // Stroke weight is §9.4.3's second channel for acquiring (#320): a heavier ring
                // survives dichromacy, greyscale and high contrast where a hue would not, and the
                // plot has no room for a word. The list view says "Acquiring" instead (A11Y-11).
                BorderThickness = new Thickness(tracked ? 1 : satellite.Marker switch
                {
                    SkyPlotMarkerKind.Acquiring => 2.5,
                    _ => 1.5,
                }),

                // Ignored dims with below-mask, and for the same reason: both are satellites the
                // receiver is not going to try, so neither should compete with the ones it is.
                Opacity = satellite.Marker is SkyPlotMarkerKind.BelowMask or SkyPlotMarkerKind.Ignored ? 0.55 : 1,
            };

            // §10.5's dashed ring for an excluded satellite. Through the template's own state group
            // rather than a second Style — see WzSkyPlotMarkerStyle for why, and for why a dash and
            // not a hue.
            VisualStateManager.GoToState(
                marker,
                satellite.Marker == SkyPlotMarkerKind.Ignored ? "Excluded" : "Included",
                useTransitions: false);

            AutomationProperties.SetName(marker, satellite.Description);
            ToolTipService.SetToolTip(marker, satellite.Description);
            marker.Click += (sender, _) =>
            {
                if (sender is Button clicked && clicked.Tag is int prn)
                {
                    _cursorPrn = prn;
                    Invoke(prn);
                }
            };

            Canvas.SetLeft(marker, centre + x - (MarkerHitSize / 2));
            Canvas.SetTop(marker, centre + y - (MarkerHitSize / 2));
            _canvas!.Children.Add(marker);
        }

        DrawSelection(centre, radius);
    }

    /// <summary>The ring that says which satellite the table has selected.</summary>
    private void DrawSelection(double centre, double radius)
    {
        if (SelectedPrn is not int prn)
        {
            return;
        }

        SkyPlotSatellite? found = _plotted.FirstOrDefault(satellite => satellite.Prn == prn);
        if (found is null)
        {
            return;
        }

        (double x, double y) = SkyPlotGeometry.Project(
            found.ElevationDegrees!.Value, found.AzimuthDegrees!.Value, radius);

        const double ringRadius = MaxMarkerRadius + 5;

        Ellipse ring = new()
        {
            Width = ringRadius * 2,
            Height = ringRadius * 2,
            Stroke = Brush("WzAccentFillBrush"),
            StrokeThickness = 2,
            IsHitTestVisible = false,
        };

        Canvas.SetLeft(ring, centre + x - ringRadius);
        Canvas.SetTop(ring, centre + y - ringRadius);
        _canvas!.Children.Add(ring);
    }

    /// <summary>
    /// The focus ring the arrow keys move, drawn only while the plot has keyboard focus.
    /// </summary>
    /// <remarks>
    /// Distinct from the selection ring, and it has to be: the keyboard cursor and the table's
    /// selection are different things until Enter is pressed, and a user who could not see which
    /// one the arrows were on would be pressing Enter blind (A11Y-2).
    /// </remarks>
    private void UpdateCursorRing()
    {
        if (_canvas is null)
        {
            return;
        }

        SkyPlotSatellite? satellite = CursorSatellite;
        RenderCursorName(satellite);

        foreach (UIElement existing in _canvas.Children.Where(child => child is Ellipse { Tag: "cursor" }).ToList())
        {
            _canvas.Children.Remove(existing);
        }

        if (FocusState == FocusState.Unfocused || satellite is null)
        {
            return;
        }

        double side = _canvas.Width;
        double centre = side / 2;
        double radius = centre - Inset;

        (double x, double y) = SkyPlotGeometry.Project(
            satellite.ElevationDegrees!.Value, satellite.AzimuthDegrees!.Value, radius);

        const double ringRadius = MaxMarkerRadius + 8;

        Ellipse ring = new()
        {
            Tag = "cursor",
            Width = ringRadius * 2,
            Height = ringRadius * 2,
            Stroke = Brush("WzTextPrimaryBrush"),
            StrokeThickness = 2,
            StrokeDashArray = [3, 2],
            IsHitTestVisible = false,
        };

        Canvas.SetLeft(ring, centre + x - ringRadius);
        Canvas.SetTop(ring, centre + y - ringRadius);
        _canvas.Children.Add(ring);
    }

    /// <summary>Says where the cursor is, for a reader that cannot see the ring.</summary>
    /// <remarks>
    /// <para>
    /// Set as the cursor moves rather than only on Enter, so a screen-reader user hears what they
    /// are on before deciding to select it — and refreshed from every rebuild, so it cannot go on
    /// describing a satellite that has since dropped out of the plot.
    /// </para>
    /// <para>
    /// A cursor whose PRN is no longer plotted says so. Losing the satellite you were on is a thing
    /// that happened, and the ring disappearing is not information a reader receives.
    /// </para>
    /// <para>
    /// An unset cursor is left alone. The name it still carries is the hint the page put there —
    /// "arrow keys move between satellites, Enter selects" — which is the right thing to hear
    /// before the arrows have been pressed and cannot be reconstructed from here.
    /// </para>
    /// </remarks>
    private void RenderCursorName(SkyPlotSatellite? satellite)
    {
        if (satellite is not null)
        {
            AutomationProperties.SetName(this, $"Sky plot. {satellite.Description}");
            return;
        }

        if (_cursorPrn is int prn)
        {
            AutomationProperties.SetName(this, $"Sky plot. PRN {prn} is no longer shown.");
        }
    }

    /// <summary>
    /// Resolves a theme brush by key.
    /// </summary>
    /// <remarks>
    /// Looked up per rebuild rather than cached, and the control rebuilds on
    /// <c>ActualThemeChanged</c>. A brush captured once would be the theme that was current when
    /// the plot was first drawn, which looks right until the first switch and then never recovers.
    /// </remarks>
    private Brush? Brush(string key) => Resource<Brush>(key);

    private T? Resource<T>(string key)
        where T : class
    {
        if (Resources.TryGetValue(key, out object? local) && local is T typed)
        {
            return typed;
        }

        return Application.Current.Resources.TryGetValue(key, out object? found) ? found as T : null;
    }
}
