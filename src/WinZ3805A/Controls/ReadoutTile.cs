using Windows.Foundation;
using System.Globalization;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace WinZ3805A.Controls;

/// <summary>How large a readout is, from the three sizes §9.5.3 defines.</summary>
/// <remarks>
/// An enum rather than an injectable <c>Style</c>, so a page can pick one of the three specified
/// sizes and cannot introduce a fourth.
/// </remarks>
public enum ReadoutSize
{
    /// <summary>20 / 24. Card-level figures and table numerics.</summary>
    Small = 0,

    /// <summary>32 / 36. TFOM, FFOM, 1 PPS TI, EFC.</summary>
    Medium,

    /// <summary>56 / 56. Medallion centre value, satellite count.</summary>
    Large,
}

/// <summary>
/// A label, a number, its unit, and optionally a severity — with §9.5.3's numeric rules enforced
/// centrally so no page can get them wrong locally (§9.10.2, P0-20).
/// </summary>
/// <remarks>
/// <para>
/// This is the control that carries the difference between a careful instrument application and a
/// sloppy one, and it is where most data-dense Windows applications fail. Four things it does that
/// are easy to leave out:
/// </para>
/// <para>
/// <b>Tabular figures</b> (rule 1), from <c>WzReadout*TextStyle</c>. Without them a value stepping
/// from −33.1 to −9.8 shifts sideways, and glanced at from across a bench that reads as motion
/// where there is none.
/// </para>
/// <para>
/// <b>Reserved width</b> (rule 2) from <see cref="MaxIntegerDigits"/>. Tabular figures stop the
/// digits jostling, but the field itself still resizes when the string gets shorter, and a layout
/// that reflows on every poll is worse than one that is slightly too wide. Reserved by measuring a
/// template string of zeros in the same style, which is exact rather than estimated because tabular
/// digits all share one advance.
/// </para>
/// <para>
/// <b>The unit as a separate run</b> (rule 3), in caption size and secondary colour, after a hair
/// space. It is not part of the number: it never changes, so giving it the same weight as the digits
/// makes the digits harder to find.
/// </para>
/// <para>
/// <b>U+2212, not a hyphen</b> (rule 4), and a fixed decimal count per quantity (rule 6). Both live
/// in <see cref="ReadoutFormatter"/>, which is unit-tested.
/// </para>
/// <para>
/// Nothing here animates. §9.13 item 9 forbids a <c>Storyboard</c> targeting a readout value, and
/// §9.8.2 gives readout changes <c>WzDurationInstant</c>.
/// </para>
/// </remarks>
public sealed class ReadoutTile : Control
{
    private const string ValueRunPart = "PART_ValueRun";
    private const string FractionRunPart = "PART_FractionRun";
    private const string PointRunPart = "PART_PointRun";
    private const string PointTextPart = "PART_PointText";
    private const string LabelTextPart = "LabelText";
    private const string ValueGridPart = "PART_ValueGrid";
    private const string ReserveTextPart = "PART_ReserveText";
    private const string SpacerRunPart = "PART_SpacerRun";
    private const string UnitRunPart = "PART_UnitRun";
    private const string ReserveValueRunPart = "PART_ReserveValueRun";

    /// <summary>Identifies the <see cref="Label"/> dependency property.</summary>
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ReadoutTile), new PropertyMetadata(string.Empty, OnAnyChanged));

    /// <summary>Identifies the <see cref="Value"/> dependency property.</summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double?), typeof(ReadoutTile), new PropertyMetadata(null, OnAnyChanged));

    /// <summary>Identifies the <see cref="Unit"/> dependency property.</summary>
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(ReadoutTile), new PropertyMetadata(string.Empty, OnAnyChanged));

    /// <summary>Identifies the <see cref="DecimalPlaces"/> dependency property.</summary>
    public static readonly DependencyProperty DecimalPlacesProperty = DependencyProperty.Register(
        nameof(DecimalPlaces), typeof(int), typeof(ReadoutTile), new PropertyMetadata(1, OnAnyChanged));

    /// <summary>Identifies the <see cref="MaxIntegerDigits"/> dependency property.</summary>
    public static readonly DependencyProperty MaxIntegerDigitsProperty = DependencyProperty.Register(
        nameof(MaxIntegerDigits), typeof(int), typeof(ReadoutTile), new PropertyMetadata(3, OnAnyChanged));

    /// <summary>Identifies the <see cref="AllowNegative"/> dependency property.</summary>
    public static readonly DependencyProperty AllowNegativeProperty = DependencyProperty.Register(
        nameof(AllowNegative), typeof(bool), typeof(ReadoutTile), new PropertyMetadata(true, OnAnyChanged));

    /// <summary>Identifies the <see cref="Size"/> dependency property.</summary>
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(ReadoutSize), typeof(ReadoutTile), new PropertyMetadata(ReadoutSize.Medium, OnSizeChanged));

    /// <summary>Initialises a new tile.</summary>
    public ReadoutTile()
    {
        DefaultStyleKey = typeof(ReadoutTile);
    }

    /// <summary>What the number is, in sentence case — "Time interval", "Satellites tracked".</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>The value, or <see langword="null"/> when the device did not report one.</summary>
    public double? Value
    {
        get => (double?)GetValue(ValueProperty);
        set
        {
            // Assigned only when it differs (#403): SetValue takes an IInspectable, so every
            // assignment boxes and mints a COM wrapper the runtime never lets go of. See
            // _valueShown for why the shadow is kept by the callback and not here.
            if (Nullable.Equals(_valueShown, value))
            {
                return;
            }

            SetValue(ValueProperty, value);
        }
    }

    /// <summary>
    /// What <see cref="Value"/> currently holds, so an unchanged assignment is skipped.
    /// </summary>
    /// <remarks>
    /// Maintained in the change callback rather than the setter, so a binding or a Style Setter -
    /// neither of which goes through the CLR property - cannot leave it stale and cause a real
    /// change to be skipped. Compared against rather than GetValue, because a read crosses the
    /// same boundary a write does.
    /// </remarks>
    private double? _valueShown;

    /// <summary>The unit, typeset separately — "ns", "dB", "°".</summary>
    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    /// <summary>Decimal places, fixed for this quantity (§9.5.3 rule 6).</summary>
    public int DecimalPlaces
    {
        get => (int)GetValue(DecimalPlacesProperty);
        set => SetValue(DecimalPlacesProperty, value);
    }

    /// <summary>The most whole-number digits this quantity reaches, which sets the reserved width.</summary>
    public int MaxIntegerDigits
    {
        get => (int)GetValue(MaxIntegerDigitsProperty);
        set => SetValue(MaxIntegerDigitsProperty, value);
    }

    /// <summary>Whether to reserve a column for the sign. False for counts, which cannot go negative.</summary>
    public bool AllowNegative
    {
        get => (bool)GetValue(AllowNegativeProperty);
        set => SetValue(AllowNegativeProperty, value);
    }

    /// <summary>Which of the three §9.5.3 readout sizes to use.</summary>
    public ReadoutSize Size
    {
        get => (ReadoutSize)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateSizeState(useTransitions: false);
        Refresh();
    }

    private static void OnAnyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var tile = (ReadoutTile)d;

        if (e.Property == ValueProperty)
        {
            tile._valueShown = (double?)e.NewValue;
        }

        tile.Refresh();
    }

    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var tile = (ReadoutTile)d;
        tile.UpdateSizeState(useTransitions: false);
        tile.Refresh();
    }

    /// <remarks>
    /// Always without transitions: §9.8.2 gives readout changes <c>WzDurationInstant</c>, and
    /// §9.13 item 9 forbids animating a readout at all.
    /// </remarks>
    private void UpdateSizeState(bool useTransitions) =>
        VisualStateManager.GoToState(this, Size.ToString(), useTransitions);

    /// <summary>
    /// How large the "no value" dash is drawn, whatever size the readout itself is.
    /// </summary>
    /// <remarks>
    /// A fixed size rather than a fraction of the readout, because the dash is not a number and
    /// gains nothing from being scaled like one. It has to read as "nothing here" at a glance from
    /// across a bench, which is the opposite of what §9.5.3's sizes are for.
    /// </remarks>
    private const double PlaceholderFontSize = 24;

    /// <summary>How many characters the reserved template string holds.</summary>
    /// <remarks>
    /// Read from the run rather than recomputed from the properties, so it cannot disagree with the
    /// string actually being measured.
    /// </remarks>
    private int ReserveLength() =>
        GetTemplateChild(ReserveValueRunPart) is Run run ? run.Text?.Length ?? 0 : 0;

    /// <summary>
    /// Places the caption on the decimal axis, once every child has its final size.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This has to happen in arrange, not in a <c>SizeChanged</c> handler.</b> The axis is
    /// measured from three elements' <c>ActualWidth</c>, and those are only true after a layout
    /// pass — so a handler-driven version was correct only when its last firing happened to follow
    /// the final pass. Changing digit count reorders those passes, which made the caption jump
    /// between correct and badly offset with no code change involved. A measurement taken at the
    /// wrong moment is not a slightly-wrong measurement, it is a coin toss.
    /// </para>
    /// <para>
    /// The offset is applied as a render transform, which runs after arrange and changes no desired
    /// size — so this cannot invalidate the layout that produced its inputs, and needs no guard. An
    /// earlier version applied it as a margin so it would participate in layout; the comment on the
    /// transform in <see cref="AlignLabelToAxis"/> records why that could not stand.
    /// </para>
    /// </remarks>
    protected override Size ArrangeOverride(Size finalSize)
    {
        Size arranged = base.ArrangeOverride(finalSize);

        AlignLabelToAxis();

        return arranged;
    }

    private void AlignLabelToAxis()
    {
        if (GetTemplateChild(LabelTextPart) is not FrameworkElement label ||
            GetTemplateChild(ValueGridPart) is not FrameworkElement grid ||
            GetTemplateChild(ReserveTextPart) is not FrameworkElement reserve)
        {
            return;
        }

        if (reserve.ActualWidth <= 0 || grid.ActualWidth <= 0)
        {
            return;
        }

        // With a fractional part the axis is the MIDDLE of the decimal point, not the boundary
        // beside it — at 56 px that glyph is wide enough for the difference to read as an error.
        double point = GetTemplateChild(PointTextPart) is FrameworkElement separator
            ? separator.ActualWidth
            : 0;

        double axis;
        if (point > 0)
        {
            axis = reserve.ActualWidth + (point / 2);
        }
        else
        {
            // WITHOUT A DECIMAL POINT THE ANCHOR IS THE ONES DIGIT, which is the same rule: the
            // column the eye returns to, and the one right-alignment holds still.
            //
            // Centring on the middle of the reserved column instead put the caption half a digit
            // left of the number on every count, because the reserve is as wide as the largest
            // value — two digits for "satellites" — while the usual reading is one, right-aligned
            // into its right half. Reported as "satellites isn't centered", and it was.
            //
            // Tabular figures are what make this arithmetic exact rather than approximate: every
            // digit shares one advance (§9.5.3 rule 1), so a character's width is the reserve's
            // width over its length. The sign, when reserved, is assumed to share that advance —
            // true in Segoe UI Variable's tabular set, and a fraction of a pixel out if it ever
            // is not.
            int characters = ReserveLength();
            double character = characters > 0 ? reserve.ActualWidth / characters : 0;

            axis = reserve.ActualWidth - (character / 2);
        }

        double offset = axis - (grid.ActualWidth / 2);

        // A RENDER TRANSFORM, NOT A MARGIN, and this is the whole correctness story.
        //
        // A margin participates in layout: it widened the caption's slot, which widened the tile,
        // which moved the axis, which changed the margin. The decimal case happened to converge;
        // the integer one oscillated and WinUI killed the process with a layout cycle
        // (0xc000027b out of Microsoft.UI.Xaml.dll, which names nothing). Feeding a measurement
        // back into the layout that produced it was fragile even while it appeared to work.
        //
        // A transform is applied after arrange and changes no desired size, so there is no loop to
        // converge. The caption may now overhang its tile, which is harmless: §9.6's XXL spacing
        // between readouts is far wider than half a digit.
        if (label.RenderTransform is not TranslateTransform shift)
        {
            shift = new TranslateTransform();
            label.RenderTransform = shift;
        }

        shift.X = offset;
    }

    /// <summary>Resolves a theme brush, or null when the key is absent.</summary>
    /// <remarks>
    /// The indexer on <c>ResourceDictionary</c> throws on a missing key, and this is called during
    /// the first layout pass, so a typo would be a startup crash rather than a visible defect.
    /// </remarks>
    private static Brush? Lookup(string key) =>
        Application.Current.Resources.TryGetValue(key, out object? found) ? found as Brush : null;

    private void Refresh()
    {

        string formatted = ReadoutFormatter.Format(Value, DecimalPlaces);

        // Split at the decimal separator, because that is the axis everything else hangs from.
        // The integer side is right-aligned against it and the fractional side runs left from it,
        // so the point itself never moves as digits are gained or lost.
        int point = formatted.IndexOf(
            CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator, StringComparison.Ordinal);

        string separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        string integerPart = point < 0 ? formatted : formatted[..point];
        string pointPart = point < 0 ? string.Empty : separator;
        string fractionPart = point < 0 ? string.Empty : formatted[(point + separator.Length)..];

        if (GetTemplateChild(PointRunPart) is Run pointRun)
        {
            pointRun.Text = pointPart;
        }

        if (GetTemplateChild(FractionRunPart) is Run fractionRun)
        {
            fractionRun.Text = fractionPart;
        }

        if (GetTemplateChild(ValueRunPart) is Run valueRun)
        {
            valueRun.Text = integerPart;

            // §11.1's "—" is right, and at readout size it stops reading as one. An em dash set in
            // Segoe UI Variable Display Semibold at 56 px is a 40 x 5 px bar: on the primary window
            // it looks like a rule someone drew under the caption rather than like an absent
            // reading, which is how it was reported ("why is there just a line under 1 PPS TI").
            //
            // So the placeholder keeps the glyph the specification names and drops the emphasis the
            // digits earn. Half size and secondary foreground, restored the moment a value returns -
            // this is a property of what is being shown, not a state the control remembers.
            // ClearValue rather than assigning a sentinel. FontSize is a double with no "unset"
            // value - NaN is legal for Width and is NOT legal here - so the way back to the size
            // the style provides is to remove the local value, not to overwrite it with one.
            //
            // The brush is resolved through TryLookup rather than the indexer for the same reason
            // VisualPngExport does it: these keys live in a merged ThemeDictionary, the indexer
            // throws when it cannot find one, and this runs during the first layout pass - so a
            // miss takes the whole application down at startup rather than showing a wrong colour.
            if (Value is null)
            {
                valueRun.FontSize = PlaceholderFontSize;
                valueRun.Foreground = Lookup("WzTextSecondaryBrush") ?? valueRun.Foreground;
            }
            else
            {
                valueRun.ClearValue(TextElement.FontSizeProperty);
                valueRun.ClearValue(TextElement.ForegroundProperty);
            }
        }

        // The hair space is dropped along with the unit, so a unitless readout does not carry a
        // stray sliver of space that shifts it off the reserved width.
        bool hasUnit = !string.IsNullOrEmpty(Unit);
        if (GetTemplateChild(SpacerRunPart) is Run spacerRun)
        {
            spacerRun.Text = hasUnit ? ReadoutFormatter.HairSpace : string.Empty;
        }

        if (GetTemplateChild(UnitRunPart) is Run unitRun)
        {
            unitRun.Text = hasUnit ? Unit : string.Empty;
        }

        // The reserve covers the DIGITS only. The unit lives in its own column now and never
        // changes width, so reserving it here would reserve it twice and push the number left.
        //
        // The reserve is invisible but measured, so the tile keeps a constant width whatever the
        // value does. It mirrors the value line run for run — widest number, hair space, unit —
        // rather than just the number: the unit is set in caption size, so reserving the number
        // alone leaves the line able to outgrow its own reservation exactly when the value is at
        // its widest, which is the one case the reservation exists for.
        if (GetTemplateChild(ReserveValueRunPart) is Run reserveValueRun)
        {
            reserveValueRun.Text = ReadoutFormatter.WidestString(MaxIntegerDigits, DecimalPlaces, AllowNegative);
        }



        // One phrase rather than three adjacent runs, which a screen reader would otherwise read as
        // disconnected fragments.
        AutomationProperties.SetName(this, ReadoutFormatter.ToSpokenText(Label, formatted, Unit));
    }
}
