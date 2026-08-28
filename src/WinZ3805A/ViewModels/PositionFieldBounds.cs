namespace WinZ3805A.ViewModels;

/// <summary>One entry field of §10.6's manual position form, and the range it accepts.</summary>
/// <param name="Field">What the field is, for a failure message that names it.</param>
/// <param name="Minimum">The lowest value §10.6 permits.</param>
/// <param name="Maximum">The highest value §10.6 permits.</param>
/// <param name="Unit">The symbol shown beside the value.</param>
public readonly record struct PositionFieldBound(string Field, double Minimum, double Maximum, string Unit);

/// <summary>
/// The ranges §10.6 gives the manual position form, as data rather than as constructor arguments.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pulled out of the page so the numbers can be asserted.</b> P0-12 is verified by "unit tests for
/// coordinate validation bounds", and while <see cref="RangeValidation"/> was well covered — it is
/// the mechanism, and it is pure — the bounds themselves were seven literals in a XAML code-behind
/// constructor that nothing could reach. The mechanism being right is not the same as each field
/// being given the right range, and a latitude quietly accepting 0–80 would have failed no test.
/// </para>
/// <para>
/// §10.6, verbatim: "lat degrees 0–90, lon degrees 0–180, minutes 0–59, seconds 0–59.999 (0.001
/// resolution), height −1000.00 to +18000.00 m (0.01 resolution)."
/// </para>
/// <para>
/// The resolutions are the <c>NumberBox</c> increments in the markup rather than range ends, so they
/// are not represented here; what this table fixes is the accept/reject boundary, which is what
/// "reject client-side rather than letting the device error" turns on.
/// </para>
/// </remarks>
public static class PositionFieldBounds
{
    /// <summary>0–90°, because 90 is the pole and there is no latitude beyond it.</summary>
    public static PositionFieldBound LatitudeDegrees { get; } = new("Latitude degrees", 0, 90, "°");

    /// <summary>0–59′.</summary>
    public static PositionFieldBound LatitudeMinutes { get; } = new("Latitude minutes", 0, 59, "′");

    /// <summary>0–59.999″, the resolution §10.6 gives the field.</summary>
    public static PositionFieldBound LatitudeSeconds { get; } = new("Latitude seconds", 0, 59.999, "″");

    /// <summary>0–180°, because 180 is the antimeridian and the hemisphere carries the sign.</summary>
    public static PositionFieldBound LongitudeDegrees { get; } = new("Longitude degrees", 0, 180, "°");

    /// <summary>0–59′.</summary>
    public static PositionFieldBound LongitudeMinutes { get; } = new("Longitude minutes", 0, 59, "′");

    /// <summary>0–59.999″.</summary>
    public static PositionFieldBound LongitudeSeconds { get; } = new("Longitude seconds", 0, 59.999, "″");

    /// <summary>
    /// −1000 to +18000 m, which spans the Dead Sea shore to well above any surveyed antenna.
    /// </summary>
    /// <remarks>
    /// The datum is deliberately not asserted here. §10.6 was amended on 21 Aug 2026 (#114) so the
    /// field states the datum the receiver said it was reporting rather than picking a side between
    /// the manual's two halves, and that is a display decision rather than a range one.
    /// </remarks>
    public static PositionFieldBound Height { get; } = new("Height", -1000, 18000, "m");

    /// <summary>Every field of the form, in the order §10.6's wireframe lays them out.</summary>
    public static IReadOnlyList<PositionFieldBound> All { get; } =
    [
        LatitudeDegrees,
        LatitudeMinutes,
        LatitudeSeconds,
        LongitudeDegrees,
        LongitudeMinutes,
        LongitudeSeconds,
        Height,
    ];
}
