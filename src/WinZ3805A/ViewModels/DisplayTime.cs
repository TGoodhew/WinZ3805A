using WinZ3805A.Device.Models;

namespace WinZ3805A.ViewModels;

/// <summary>One instant, ready to show: the value, the zone label, and whether it was converted.</summary>
/// <param name="Value">The time to render.</param>
/// <param name="ZoneLabel">What to print beside it, such as <c>UTC</c> or <c>BST</c>.</param>
/// <param name="WasConverted">
/// True when the instant was moved into another zone, so the interface can say so rather than
/// leaving the user to wonder whose clock they are reading.
/// </param>
public readonly record struct DisplayTime(DateTimeOffset Value, string ZoneLabel, bool WasConverted);

/// <summary>
/// Converts what the receiver reported into the zone the user wants to read it in (#95).
/// </summary>
/// <remarks>
/// <para>
/// The specification covers the receiver's own time zone — <c>:PTIM:TZONe</c> in §8.3 — but not the
/// application's. Reconfiguring the instrument is not an acceptable way to change a display
/// preference: it is a tier-C act that also changes the timecode output.
/// </para>
/// <para>
/// <b>The date is the reason this matters.</b> An hour of difference is obvious and easily
/// discounted; a date that is a day out near local midnight is not, and is exactly what a user
/// glancing at a window on a second monitor will misread.
/// </para>
/// <para>
/// Pure and UI-free so the conversion, and particularly the date boundary, is tested rather than
/// eyeballed.
/// </para>
/// </remarks>
public static class DisplayTimeConverter
{
    /// <summary>
    /// Renders a device-reported time in the requested zone.
    /// </summary>
    /// <param name="reported">What the receiver said, or <see langword="null"/>.</param>
    /// <param name="scale">Which scale the receiver is reporting on (§11.2).</param>
    /// <param name="zone">The zone to display in, usually <see cref="TimeZoneInfo.Local"/>.</param>
    /// <returns>The converted time, or <see langword="null"/> when there is nothing to show.</returns>
    /// <remarks>
    /// A receiver already set to a local scale is <b>not</b> converted. The offset it applied is its
    /// own and is not reported, so the instant behind the value is unknown — converting would be
    /// arithmetic on a number whose meaning we do not have, and would land a second offset on top of
    /// the first. It is shown as given and labelled as the device's own local time.
    /// </remarks>
    public static DisplayTime? Convert(DateTimeOffset? reported, TimeScale scale, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        if (reported is not DateTimeOffset value)
        {
            return null;
        }

        if (scale is TimeScale.Local or TimeScale.LocalGps)
        {
            return new DisplayTime(value, "device local", WasConverted: false);
        }

        // GPS time is not UTC - it does not take leap seconds - but the receiver reports the offset
        // separately and the parser records the scale, so relabelling is honest where converting the
        // leap difference silently would not be.
        if (scale == TimeScale.Unknown)
        {
            return new DisplayTime(value, "as reported", WasConverted: false);
        }

        DateTimeOffset converted = TimeZoneInfo.ConvertTime(value, zone);
        bool moved = converted.Offset != value.Offset;

        return new DisplayTime(converted, LabelFor(zone, converted, scale), moved);
    }

    /// <summary>
    /// What to print beside the time.
    /// </summary>
    /// <remarks>
    /// UTC keeps its name because that is what a timing user expects to see, and because "GMT
    /// Standard Time" would be both wrong half the year and unrecognisable to the audience. Any
    /// other zone gets the Windows display name, which already accounts for daylight saving.
    /// </remarks>
    private static string LabelFor(TimeZoneInfo zone, DateTimeOffset converted, TimeScale scale)
    {
        if (converted.Offset == TimeSpan.Zero && zone.BaseUtcOffset == TimeSpan.Zero && !zone.IsDaylightSavingTime(converted))
        {
            return scale == TimeScale.Gps ? "GPS" : "UTC";
        }

        return zone.IsDaylightSavingTime(converted) ? zone.DaylightName : zone.StandardName;
    }
}
