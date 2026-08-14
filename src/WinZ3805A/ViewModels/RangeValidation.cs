using System.Globalization;

using WinZ3805A.Device.Commands;

namespace WinZ3805A.ViewModels;

/// <summary>
/// §9.11's client-side range check: the reason the receiver is never sent a value it will refuse.
/// </summary>
/// <remarks>
/// <para>
/// §9.11 is explicit that this is rejected client-side "rather than letting the device error", and
/// §10.6 lists the ranges for the position fields while §10.7 gives the antenna delay's. Doing it
/// here rather than in each page means the sentence a user reads is the same sentence everywhere,
/// and that the bounds come from the catalog entry the command will actually be built from.
/// </para>
/// <para>
/// The message is the "what to do next" half of §9.11's error pattern, so it is phrased as an
/// instruction rather than a complaint: <em>Enter a value between 0 and 999,999 ns.</em>
/// </para>
/// </remarks>
public static class RangeValidation
{
    /// <summary>
    /// Checks a value against a parameter's declared range, and says what to do if it fails.
    /// </summary>
    /// <returns>The error text, or <see langword="null"/> when the value is acceptable.</returns>
    public static string? Describe(double? value, ParameterSpec parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        return Describe(value, parameter.Minimum, parameter.Maximum, parameter.Unit);
    }

    /// <summary>
    /// Checks a value against explicit bounds, for the fields §10.6 states directly rather than
    /// through a catalog parameter.
    /// </summary>
    /// <returns>The error text, or <see langword="null"/> when the value is acceptable.</returns>
    public static string? Describe(double? value, double? minimum, double? maximum, string? unit = null)
    {
        // NaN is what a NumberBox holds after unparseable text, and it compares false against every
        // bound — so it has to be caught first or an unreadable field would pass as valid.
        if (value is not double number || double.IsNaN(number))
        {
            return $"Enter a value{Range(minimum, maximum, unit)}.";
        }

        if (double.IsInfinity(number))
        {
            return $"Enter a value{Range(minimum, maximum, unit)}.";
        }

        if (minimum is double low && number < low)
        {
            return $"Enter a value{Range(minimum, maximum, unit)}.";
        }

        if (maximum is double high && number > high)
        {
            return $"Enter a value{Range(minimum, maximum, unit)}.";
        }

        return null;
    }

    /// <summary>
    /// The range as words — " between 0 and 999,999 ns", " of at least 0 s", or nothing at all when
    /// the catalog declares no bounds.
    /// </summary>
    private static string Range(double? minimum, double? maximum, string? unit)
    {
        string suffix = string.IsNullOrEmpty(unit) ? string.Empty : $" {unit}";

        return (minimum, maximum) switch
        {
            (double low, double high) => $" between {Format(low)} and {Format(high)}{suffix}",
            (double low, null) => $" of at least {Format(low)}{suffix}",
            (null, double high) => $" of at most {Format(high)}{suffix}",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Grouped, and with no trailing zeros. §9.5.2's numeric rules put separators in figures this
    /// size — 999999 read off a screen is a different number from 999,999 at a glance.
    /// </summary>
    private static string Format(double value) =>
        value.ToString("#,##0.######", CultureInfo.CurrentCulture);
}
