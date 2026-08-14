using WinZ3805A.Device.Models;

namespace WinZ3805A.Controls;

/// <summary>
/// The domain of one signal-strength scale, and where "good" sits on it.
/// </summary>
/// <remarks>
/// A separate type so §11.1's two ranges are stated once and are testable without a control, a
/// template, or a dispatcher.
/// </remarks>
public readonly record struct SignalStrengthScale
{
    private SignalStrengthScale(SignalStrengthKind kind, double minimum, double maximum, double good, string label)
    {
        Kind = kind;
        Minimum = minimum;
        Maximum = maximum;
        Good = good;
        Label = label;
    }

    /// <summary>Which scale this is.</summary>
    public SignalStrengthKind Kind { get; }

    /// <summary>The bottom of the domain.</summary>
    public double Minimum { get; }

    /// <summary>The top of the domain.</summary>
    public double Maximum { get; }

    /// <summary>At or above this the signal is considered good.</summary>
    public double Good { get; }

    /// <summary>The column heading the receiver prints — <c>C/N</c> or <c>SS</c>.</summary>
    public string Label { get; }

    /// <summary>Whether the scale is known well enough to draw against.</summary>
    public bool IsKnown => Kind != SignalStrengthKind.Unknown;

    /// <summary>The scale for a given kind.</summary>
    public static SignalStrengthScale For(SignalStrengthKind kind) => kind switch
    {
        // §11.1: 26-55, with 35 and above good.
        SignalStrengthKind.CarrierToNoise => new(kind, 26, 55, 35, "C/N"),

        // §11.1: 0-255, with 20-30 weak. "Good" is taken as the top of the weak band, which is the
        // only threshold the specification states for this scale.
        SignalStrengthKind.SignalStrength => new(kind, 0, 255, 30, "SS"),

        _ => new(SignalStrengthKind.Unknown, 0, 1, 1, string.Empty),
    };

    /// <summary>Brings a reading inside the domain, so a bar cannot overflow its track.</summary>
    public double Clamp(int? strength) =>
        strength is int value ? Math.Clamp(value, Minimum, Maximum) : Minimum;

    /// <summary>Whether a reading is good on this scale.</summary>
    public bool IsGood(int? strength) => IsKnown && strength is int value && value >= Good;

    /// <summary>
    /// One sentence for assistive technology, naming the scale as well as the number.
    /// </summary>
    /// <remarks>
    /// "49" alone is meaningless without knowing whether the scale tops out at 55 or 255, and a
    /// screen-reader user has no bar to look at for the proportion.
    /// </remarks>
    public string Describe(int? strength)
    {
        if (strength is not int value)
        {
            return "Signal strength not reported";
        }

        return IsKnown
            ? $"{Label} {value} of {Maximum:0}"
            : $"Signal strength {value}, scale unknown";
    }
}
