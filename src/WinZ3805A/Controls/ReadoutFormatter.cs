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
/// <c>ReadoutTile</c> is the only caller in the application. Nothing formats a readout by hand,
/// because rules 1 to 4 and 6 are exactly the ones a page gets wrong locally. Named in plain text
/// rather than with a cref: this file is also compiled into the test assembly, where that type does
/// not exist, and an unresolvable cref is a build error there.
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
