using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using WinZ3805A.Device.Commands;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Controls;

/// <summary>
/// Applies §9.11's validation model to one <see cref="NumberBox"/>.
/// </summary>
/// <remarks>
/// <para>
/// §9.11 asks for four things at once, and they are easy to get three of: bounds enforced
/// client-side so the device is never sent a value it will refuse; validation <b>on blur</b> for
/// typed entry and <b>on change</b> for the spinner; the error shown as glyph plus text plus
/// border; and <em>Apply</em> disabled while any field in the card is invalid. Doing it per page
/// produced the fourth being forgotten, so it is done here once and pages compose one of these per
/// field.
/// </para>
/// <para>
/// <b><c>ValidationMode</c> is set to <c>Disabled</c> deliberately.</b> <c>NumberBox</c>'s own
/// <c>InvalidInputOverwritten</c> silently reverts an out-of-range entry to the last good value,
/// which cannot coexist with §9.11: there is nothing left to put an error message under, and the
/// user is told nothing about the number they just typed. The bounds are still enforced — they are
/// simply enforced by refusing to <em>send</em>, which is what "reject client-side rather than
/// letting the device error" actually asks for.
/// </para>
/// <para>
/// The spinner is a separate matter. It cannot produce an out-of-range value because
/// <c>NumberBox</c> clamps to <c>Minimum</c> and <c>Maximum</c> when stepping, so the "on change"
/// half of the rule is about the value the user is watching rather than about catching the
/// spinner out.
/// </para>
/// </remarks>
public sealed class NumberFieldValidator
{
    private readonly NumberBox _field;
    private readonly FieldErrorText _error;
    private readonly double? _minimum;
    private readonly double? _maximum;
    private readonly string? _unit;

    /// <summary>Validates a field against a catalogued parameter's declared range.</summary>
    public NumberFieldValidator(NumberBox field, FieldErrorText error, ParameterSpec parameter)
        : this(field, error, parameter?.Minimum, parameter?.Maximum, parameter?.Unit)
    {
        ArgumentNullException.ThrowIfNull(parameter);
    }

    /// <summary>
    /// Validates a field against explicit bounds, for the fields §10.6 states directly rather than
    /// through a catalog parameter.
    /// </summary>
    public NumberFieldValidator(
        NumberBox field,
        FieldErrorText error,
        double? minimum,
        double? maximum,
        string? unit = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(error);

        _field = field;
        _error = error;
        _minimum = minimum;
        _maximum = maximum;
        _unit = unit;

        // The bounds still reach the control, so the spinner clamps and assistive technology can
        // read the range off the field itself rather than only off the error text.
        if (minimum is double low)
        {
            field.Minimum = low;
        }

        if (maximum is double high)
        {
            field.Maximum = high;
        }

        field.ValidationMode = NumberBoxValidationMode.Disabled;

        _announced = IsValid;

        field.LostFocus += OnLostFocus;
        field.ValueChanged += OnValueChanged;

        // The critical brush is resolved imperatively below, so it would otherwise stay resolved
        // against the theme that was current when the error first appeared - including across a
        // switch into high contrast, where the whole palette changes.
        field.ActualThemeChanged += (_, _) => Revalidate();
    }

    /// <summary>Raised whenever the field's validity may have changed, so a card can re-evaluate Apply.</summary>
    public event EventHandler? ValidityChanged;

    /// <summary>Whether the field currently holds a value that may be sent.</summary>
    /// <remarks>
    /// <b>Computed, never cached.</b> A cached flag initialised to true means an untouched card
    /// answers "valid" for fields nobody has entered anything into — which is exactly the state a
    /// freshly navigated page is in, and it left <em>Apply</em> enabled over seven empty boxes.
    /// Whether a value may be sent and whether the user should be told off about it are two
    /// different questions: this answers the first at any moment, and <see cref="Revalidate"/>
    /// answers the second only when §9.11 says to ask.
    /// </remarks>
    public bool IsValid => RangeValidation.Describe(_field.Value, _minimum, _maximum, _unit) is null;

    /// <summary>The validity last reported, so a change can be announced exactly once.</summary>
    private bool _announced = true;

    /// <summary>The value, or null when the field does not hold a usable number.</summary>
    public double? Value =>
        double.IsNaN(_field.Value) || double.IsInfinity(_field.Value) ? null : _field.Value;

    /// <summary>
    /// Re-checks the field and shows or clears the error. Called on blur and on change, and by a
    /// page after it writes the field itself.
    /// </summary>
    public void Revalidate()
    {
        string? message = RangeValidation.Describe(_field.Value, _minimum, _maximum, _unit);
        bool valid = message is null;

        _error.Message = message;
        ApplyBorder(valid);
        Announce(valid);
    }

    /// <summary>Clears the error without judging the field, for a card being reset.</summary>
    public void Reset()
    {
        _error.Message = null;
        ApplyBorder(valid: true);
        Announce(IsValid);
    }

    /// <summary>Raises <see cref="ValidityChanged"/> when the answer has actually moved.</summary>
    private void Announce(bool valid)
    {
        if (valid == _announced)
        {
            return;
        }

        _announced = valid;
        ValidityChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The third channel of §9.11's rule, which is explicit that the border must never carry the
    /// error alone — so it moves in step with the line below the field rather than instead of it.
    /// </summary>
    /// <remarks>
    /// Valid clears the local value rather than restoring a captured brush. The resting border is a
    /// <c>{ThemeResource}</c> in <c>NumberBox</c>'s own template; capturing it once and writing it
    /// back would pin the control to whichever theme was current at construction, which looks
    /// correct until the first theme switch and then never recovers.
    /// </remarks>
    private void ApplyBorder(bool valid)
    {
        if (valid)
        {
            _field.ClearValue(Control.BorderBrushProperty);
            return;
        }

        if (Application.Current.Resources["WzCriticalBrush"] is Brush critical)
        {
            _field.BorderBrush = critical;
        }
    }

    /// <summary>§9.11: typed entry is judged when the user leaves the field, not as they type.</summary>
    private void OnLostFocus(object sender, RoutedEventArgs e) => Revalidate();

    /// <summary>
    /// §9.11: the spinner is judged as it changes. This also fires for typed entry once the box
    /// commits, which is harmless — an error already showing is simply recomputed.
    /// </summary>
    private void OnValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => Revalidate();
}
