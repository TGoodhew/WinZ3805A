using System.Globalization;

namespace WinZ3805A.Controls;

/// <summary>
/// The numeric typesetting rules of §9.5.3, as pure string logic.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately free of every WinUI type. That is not tidiness: it is what lets the rules be
/// unit-tested, and §9.5.3 is the part of the design system where a mistake is invisible in a
/// screenshot and obvious across a bench. The test project compiles this file directly by link
/// rather than referencing the app, which would drag a Windows-only WinExe into a headless test
/// run.
/// </para>
/// <para>
/// This is the one formatter every readout goes through — <c>ReadoutTile</c>, the strength bars,
/// the medallion's sentence, the chart labels, and every view model that puts a number in a string.
/// Nothing formats a readout by hand, because rules 1 to 4 and 6 are exactly the ones a page gets
/// wrong locally. <c>ReadoutTile</c> is named in plain text rather than with a cref: this file is
/// also compiled into the test assembly, where that type does not exist, and an unresolvable cref
/// is a build error there.
/// </para>
/// </remarks>
public static class ReadoutFormatter
{
    /// <summary>
    /// U+2212 MINUS SIGN, which is what a negative readout uses (§9.5.3 rule 4).
    /// </summary>
    /// <remarks>
    /// Not a hyphen. A hyphen is optically too short and sits too high beside lining figures, so
    /// <c>-33.1</c> reads as slightly broken where <c>−33.1</c> reads as a number. Raw device text
    /// in <c>WzMonoTextStyle</c> is exempt — it is reproduced verbatim.
    /// </remarks>
    public const string MinusSign = "−";

    /// <summary>
    /// U+200A HAIR SPACE, which separates a value from its unit (§9.5.3 rule 3).
    /// </summary>
    public const string HairSpace = " ";

    /// <summary>What a field with no value shows (§11.1).</summary>
    public const string NoValue = "—";

    /// <summary>
    /// Undoes §9.5.3's typesetting, for a value on its way out of the application (§9.7.4).
    /// </summary>
    /// <returns>The machine-readable text, or null when the field holds nothing.</returns>
    /// <remarks>
    /// <para>
    /// <b>A copied value is data leaving the application, not a readout</b> — the same distinction
    /// <c>CsvDocument</c> draws, and for the same reason. A readout shows −33.1 with
    /// <see cref="MinusSign"/> because a hyphen is optically too short beside lining figures, and
    /// separates a unit with <see cref="HairSpace"/>; a spreadsheet handed either gets text rather
    /// than a number. Verified on the live receiver: the 1 PPS TI reading copies as
    /// <c>-2.9</c> with U+002D, against the U+2212 on screen.
    /// </para>
    /// <para>
    /// <see cref="NoValue"/> comes back as null rather than as an em dash. §11.1 makes that glyph
    /// mean <i>the device did not report one</i>, and pasting it would put a character that looks
    /// like data where the absence of data was the fact.
    /// </para>
    /// </remarks>
    public static string? ToMachineText(string? text)
    {
        if (text is null)
        {
            return null;
        }

        string plain = text
            .Replace(MinusSign, "-", StringComparison.Ordinal)
            .Replace(HairSpace, string.Empty, StringComparison.Ordinal)
            .Trim();

        return plain.Length == 0 || string.Equals(plain, NoValue, StringComparison.Ordinal)
            ? null
            : plain;
    }

    /// <summary>An angle in whole degrees, or <see cref="NoValue"/>.</summary>
    /// <remarks>
    /// Here rather than on a row type because the satellite tables, the sky plot and the plot's
    /// A11Y-11 list alternate all render the same two angles, and §11.1's rule that a missing
    /// reading shows as an em dash is only one rule if it is written once.
    /// </remarks>
    public static string Degrees(int? value) => value is int degrees
        ? $"{degrees.ToString(System.Globalization.CultureInfo.CurrentCulture)}°"
        : NoValue;

    /// <summary>
    /// A duration in seconds, rendered in the engineering unit that suits its magnitude.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The receiver reports every interval in seconds — <c>2.7E-006</c> for a holdover uncertainty,
    /// <c>1.0E-006</c> for its threshold — and nobody reads those side by side and sees that one is
    /// nearly three times the other. §9.5.3 rule 3 puts a hair space before the unit; the unit
    /// itself is chosen here rather than fixed per field, because the same quantity spans
    /// nanoseconds to seconds over a long holdover.
    /// </para>
    /// <para>
    /// <b>Steps in thousands, never in between.</b> Only ns, µs, ms and s are produced, so a value
    /// that grows never passes through a unit a reader has to think about, and two figures shown
    /// together are comparable as often as the decade allows.
    /// </para>
    /// </remarks>
    /// <param name="seconds">The interval, or <see langword="null"/>.</param>
    /// <param name="decimalPlaces">Decimals to show, fixed per field as §9.5.3 rule 6 requires.</param>
    /// <param name="culture">Supplies the decimal separator; defaults to the current culture.</param>
    /// <returns>The value and its unit, or <see cref="NoValue"/> with no unit.</returns>
    public static (string Value, string Unit) Seconds(
        double? seconds,
        int decimalPlaces = 1,
        CultureInfo? culture = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decimalPlaces);

        if (seconds is not double value || double.IsNaN(value) || double.IsInfinity(value))
        {
            return (NoValue, string.Empty);
        }

        double magnitude = Math.Abs(value);

        // Exact zero has no magnitude to choose from. Nanoseconds is the receiver's own resolution
        // and the unit every other timing readout in the app uses, so a zero reads consistently
        // beside them rather than as "0.0 s".
        (double scale, string unit) = magnitude switch
        {
            0 => (1e9, "ns"),
            < 1e-6 => (1e9, "ns"),
            < 1e-3 => (1e6, "µs"),
            < 1 => (1e3, "ms"),
            _ => (1, "s"),
        };

        return (Format(value * scale, decimalPlaces, culture), unit);
    }

    /// <summary>
    /// Formats a value to a fixed number of decimal places, with U+2212 for negatives.
    /// </summary>
    /// <param name="value">The value, or <see langword="null"/> when the device did not report one.</param>
    /// <param name="decimalPlaces">
    /// How many decimals this quantity always shows (§9.5.3 rule 6). Fixed per quantity, never
    /// varying with the value: a column that changes precision row to row cannot be scanned.
    /// </param>
    /// <param name="culture">
    /// Supplies the decimal separator. Defaults to the current culture, so a German user sees a
    /// comma; only the negative sign is overridden, because U+2212 is a typographic rule rather
    /// than a locale convention.
    /// </param>
    /// <returns>The formatted number, or <see cref="NoValue"/>.</returns>
    public static string Format(double? value, int decimalPlaces, CultureInfo? culture = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decimalPlaces);

        if (value is not double number || double.IsNaN(number) || double.IsInfinity(number))
        {
            return NoValue;
        }

        return number.ToString($"F{decimalPlaces}", NumberFormat(culture));
    }

    /// <summary>
    /// The widest string this quantity can render, used to reserve width (§9.5.3 rule 2).
    /// </summary>
    /// <param name="maxIntegerDigits">The most digits the whole part can reach.</param>
    /// <param name="decimalPlaces">The fixed decimal count, as passed to <see cref="Format"/>.</param>
    /// <param name="allowNegative">
    /// Whether the quantity can go negative. A time interval can and a satellite count cannot, and
    /// reserving a sign column for a count wastes width that the layout has already committed.
    /// </param>
    /// <param name="culture">Supplies the decimal separator, as in <see cref="Format"/>.</param>
    /// <returns>
    /// A string of the maximum width, made of zeros. With tabular figures every digit has the same
    /// advance, so zeros measure exactly as wide as the real value will — which is what makes
    /// reserving width from a template string correct rather than approximate.
    /// </returns>
    public static string WidestString(
        int maxIntegerDigits,
        int decimalPlaces,
        bool allowNegative = true,
        CultureInfo? culture = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxIntegerDigits, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(decimalPlaces);

        NumberFormatInfo format = NumberFormat(culture);
        string sign = allowNegative ? format.NegativeSign : string.Empty;
        string whole = new('0', maxIntegerDigits);
        string fraction = decimalPlaces == 0
            ? string.Empty
            : format.NumberDecimalSeparator + new string('0', decimalPlaces);

        return sign + whole + fraction;
    }

    /// <summary>
    /// Joins a value and its unit for a screen reader, which needs one phrase rather than two
    /// adjacent runs.
    /// </summary>
    /// <remarks>
    /// Uses an ordinary space, not the hair space: the hair space is a typographic detail for the
    /// eye, and some screen readers spell out unusual whitespace or run the words together.
    /// </remarks>
    public static string ToSpokenText(string? label, string value, string? unit)
    {
        string spoken = string.IsNullOrEmpty(unit) ? value : $"{value} {unit}";
        return string.IsNullOrEmpty(label) ? spoken : $"{label}: {spoken}";
    }

    /// <summary>
    /// The current culture's number format with the negative sign replaced by U+2212.
    /// </summary>
    private static NumberFormatInfo NumberFormat(CultureInfo? culture)
    {
        NumberFormatInfo format = (NumberFormatInfo)(culture ?? CultureInfo.CurrentCulture).NumberFormat.Clone();
        format.NegativeSign = MinusSign;
        return format;
    }
}
