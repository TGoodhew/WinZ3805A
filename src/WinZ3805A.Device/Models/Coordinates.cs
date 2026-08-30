using System.Globalization;

namespace WinZ3805A.Device.Models;

/// <summary>
/// Renders decimal degrees back into the degrees–minutes–seconds form the receiver prints.
/// </summary>
/// <remarks>
/// <para>
/// The parser stores signed decimal degrees, which is what every consumer wants to compute with.
/// §10.6 shows the position the way the receiver does — <c>N 47° 31′ 18.822″</c> — because that is
/// the form a user compares against a survey sheet, a map, or the front panel of another
/// instrument.
/// </para>
/// <para>
/// <b>The carry is the whole difficulty.</b> Rounding seconds to three decimals can produce 60.000,
/// which must become the next minute rather than being printed; and that carry can cascade into the
/// degree. Getting it wrong shifts a position by a minute of arc — about 1.8 km of latitude —
/// silently, in the one field a timing receiver exists to hold fixed.
/// </para>
/// </remarks>
public static class Coordinates
{
    /// <summary>U+00B0 DEGREE SIGN.</summary>
    public const string DegreeSign = "°";

    /// <summary>U+2032 PRIME, which is the minute mark. Not an apostrophe.</summary>
    public const string MinuteSign = "′";

    /// <summary>U+2033 DOUBLE PRIME, which is the second mark. Not a quotation mark.</summary>
    public const string SecondSign = "″";

    /// <summary>Formats a latitude, or returns <see langword="null"/> if there is none.</summary>
    /// <param name="degrees">Signed decimal degrees, positive north.</param>
    public static string? Latitude(double? degrees) => Format(degrees, "N", "S", maximumDegrees: 90);

    /// <summary>Formats a longitude, or returns <see langword="null"/> if there is none.</summary>
    /// <param name="degrees">Signed decimal degrees, positive east.</param>
    public static string? Longitude(double? degrees) => Format(degrees, "E", "W", maximumDegrees: 180);

    /// <summary>
    /// Splits signed decimal degrees into hemisphere, degrees, minutes and seconds.
    /// </summary>
    /// <param name="value">Signed decimal degrees.</param>
    /// <param name="positive">The hemisphere letter for a positive value.</param>
    /// <param name="negative">The hemisphere letter for a negative value.</param>
    /// <param name="maximumDegrees">90 for latitude, 180 for longitude.</param>
    public static (string Hemisphere, int Degrees, int Minutes, double Seconds)? Split(
        double? value,
        string positive,
        string negative,
        int maximumDegrees)
    {
        ArgumentException.ThrowIfNullOrEmpty(positive);
        ArgumentException.ThrowIfNullOrEmpty(negative);

        if (value is not double signed || double.IsNaN(signed) || double.IsInfinity(signed))
        {
            return null;
        }

        // Zero is on the positive side of the line. There is no "negative zero" hemisphere, and a
        // receiver sitting on the equator or the prime meridian must not flip letters on noise.
        string hemisphere = signed < 0 ? negative : positive;
        double magnitude = Math.Abs(signed);

        int wholeDegrees = (int)magnitude;
        double remainingMinutes = (magnitude - wholeDegrees) * 60.0;
        int wholeMinutes = (int)remainingMinutes;
        double seconds = (remainingMinutes - wholeMinutes) * 60.0;

        // Round first, then carry. Rounding 59.9996 to three decimals gives 60.000, which is not a
        // number of seconds - it is the next minute.
        seconds = Math.Round(seconds, 3, MidpointRounding.AwayFromZero);

        if (seconds >= 60.0)
        {
            seconds -= 60.0;
            wholeMinutes++;
        }

        if (wholeMinutes >= 60)
        {
            wholeMinutes -= 60;
            wholeDegrees++;
        }

        // A value past the pole or the antimeridian is not a position this can render honestly.
        // §11.1 forbids throwing, so it degrades to "no value" the way an unparsed field does.
        //
        // The comparison has to include the minutes and seconds, not just the whole degrees: 90.5
        // splits into 90 degrees and 30 minutes, which is half a degree past the pole and would
        // otherwise render as a perfectly ordinary-looking "N 90 deg 30' 00.000"".
        bool pastTheLimit = wholeDegrees > maximumDegrees
            || (wholeDegrees == maximumDegrees && (wholeMinutes > 0 || seconds > 0));

        return pastTheLimit ? null : (hemisphere, wholeDegrees, wholeMinutes, seconds);
    }

    private static string? Format(double? value, string positive, string negative, int maximumDegrees)
    {
        if (Split(value, positive, negative, maximumDegrees) is not (string hemisphere, int degrees, int minutes, double seconds))
        {
            return null;
        }

        // Fixed widths so a column of coordinates stays aligned (§9.5.3 rule 7), and the seconds
        // always carry the receiver's own three decimals.
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hemisphere} {degrees}{DegreeSign} {minutes:00}{MinuteSign} {seconds:00.000}{SecondSign}");
    }
}
