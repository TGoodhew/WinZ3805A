using System.Globalization;

namespace WinZ3805A.Device.Parsing;

/// <summary>
/// Turns the single-value answers of the §7.3 fast tier into numbers (§6.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here throws</b>, on the same principle as <see cref="StatusScreenParser"/> (§11.1): a
/// value that will not parse becomes <see langword="null"/> and renders as an em dash. A poll that
/// threw would take down the loop that produced it, and one odd reply per hour would then look like
/// a dead application.
/// </para>
/// <para>
/// Every response arrives with a <b>leading space</b> — the device answers <c>_+3</c>, not
/// <c>+3</c>. That is a framing artefact of the receiver rather than part of any value, and it is
/// the single most likely thing to break a naive <c>int.Parse</c>, so trimming happens here once
/// instead of at every call site.
/// </para>
/// <para>
/// Values also carry an explicit sign the .NET parsers accept happily (<c>+3</c>), and reals arrive
/// in scientific notation with a three-digit exponent (<c>-5.4E-009</c>). Both need
/// <see cref="CultureInfo.InvariantCulture"/>: the receiver is not localised, so parsing its output
/// against a comma-decimal culture silently yields the wrong number rather than failing.
/// </para>
/// </remarks>
public static class ScalarParsers
{
    /// <summary>Parses a signed integer answer such as <c>+3</c>.</summary>
    public static int? ParseInteger(string? response)
    {
        string? text = Clean(response);
        return text is not null
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;
    }

    /// <summary>Parses a real answer such as <c>-5.4E-009</c>.</summary>
    public static double? ParseDecimal(string? response)
    {
        string? text = Clean(response);
        return text is not null
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

    /// <summary>
    /// Parses a real answer expressed in seconds and returns it in nanoseconds.
    /// </summary>
    /// <remarks>
    /// The time interval is the case this exists for: the receiver answers <c>:SYNC:TINT?</c> in
    /// seconds (<c>-5.4E-009</c>) while every display of it, and §9.10.2's medallion ring, works in
    /// nanoseconds. Converting once here keeps the factor of a billion out of the view models.
    /// </remarks>
    public static double? ParseSecondsAsNanoseconds(string? response) =>
        ParseDecimal(response) is double seconds ? seconds * 1e9 : null;

    /// <summary>Parses an enumerated keyword answer such as <c>LOCK</c>, upper-cased.</summary>
    public static string? ParseKeyword(string? response)
    {
        string? text = Clean(response);
        return text?.ToUpperInvariant();
    }

    /// <summary>
    /// Parses the first field of a comma-separated answer, such as the <c>+6.00000E+002</c> of
    /// <c>:SYNC:HOLD:DUR?</c>'s <c>+6.00000E+002,0</c>.
    /// </summary>
    public static double? ParseFirstOfList(string? response)
    {
        string? text = Clean(response);
        if (text is null)
        {
            return null;
        }

        int comma = text.IndexOf(',', StringComparison.Ordinal);
        return ParseDecimal(comma < 0 ? text : text[..comma]);
    }

    /// <summary>
    /// Parses a boolean answer, which the receiver spells <c>0</c> or <c>1</c>.
    /// </summary>
    public static bool? ParseBoolean(string? response) => ParseInteger(response) switch
    {
        0 => false,
        int => true,
        null => null,
    };

    /// <summary>Trims the leading space and anything else stray, or returns null for an empty answer.</summary>
    private static string? Clean(string? response)
    {
        string? text = response?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
