using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

using WinZ3805A.Device.Models;

namespace WinZ3805A.Controls;

/// <summary>
/// A satellite's signal strength, drawn against the scale the receiver actually printed (§9.10.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>The two scales are not interchangeable.</b> §11.1 is emphatic: <c>C/N</c> on 58503B-class
/// units runs 26–55 with 35 and above good, while <c>SS</c> on 59551A-class units runs 0–255 with
/// 20–30 weak. A bar scaled to the wrong one is not mislabelled, it is wrong by a factor of five —
/// so this control takes the <see cref="SignalStrengthKind"/> and refuses to draw anything at all
/// when it does not know which scale it is on.
/// </para>
/// <para>
/// The number is always shown beside the bar. §9.4.3 forbids conveying anything by colour alone,
/// and a bar of unknown scale conveys nothing on its own anyway.
/// </para>
/// </remarks>
public sealed class SatelliteStrengthBar : Control
{
    /// <summary>Identifies the <see cref="Strength"/> dependency property.</summary>
    public static readonly DependencyProperty StrengthProperty = DependencyProperty.Register(
        nameof(Strength),
        typeof(int?),
        typeof(SatelliteStrengthBar),
        new PropertyMetadata(null, OnChanged));

    /// <summary>Identifies the <see cref="Kind"/> dependency property.</summary>
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(SignalStrengthKind),
        typeof(SatelliteStrengthBar),
        new PropertyMetadata(SignalStrengthKind.Unknown, OnChanged));

    private ProgressBar? _bar;
    private TextBlock? _value;

    /// <summary>Initialises a new bar.</summary>
    public SatelliteStrengthBar()
    {
        DefaultStyleKey = typeof(SatelliteStrengthBar);
    }

    /// <summary>The reading, on whichever scale <see cref="Kind"/> names.</summary>
    public int? Strength
    {
        get => (int?)GetValue(StrengthProperty);
        set => SetValue(StrengthProperty, value);
    }

    /// <summary>Which scale the reading is on.</summary>
    public SignalStrengthKind Kind
    {
        get => (SignalStrengthKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _bar = GetTemplateChild("PART_Bar") as ProgressBar;
        _value = GetTemplateChild("PART_Value") as TextBlock;

        Render();
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SatelliteStrengthBar)d).Render();

    private void Render()
    {
        SignalStrengthScale scale = SignalStrengthScale.For(Kind);

        if (_value is TextBlock text)
        {
            text.Text = Strength?.ToString(System.Globalization.CultureInfo.CurrentCulture)
                ?? ReadoutFormatter.NoValue;
        }

        if (_bar is ProgressBar bar)
        {
            bar.Minimum = scale.Minimum;
            bar.Maximum = scale.Maximum;
            bar.Value = scale.Clamp(Strength);

            // An unknown scale draws no bar rather than a plausible-looking one. A reader cannot
            // tell a wrong bar from a right one, and this is the field they would judge an
            // antenna by.
            bar.Visibility = scale.IsKnown && Strength is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        AutomationProperties.SetName(this, scale.Describe(Strength));
    }
}
