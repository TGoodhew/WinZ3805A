namespace WinZ3805A.Device.Models;

/// <summary>Whether the receiver is holding a fixed position or surveying for one.</summary>
public enum PositionMode
{
    /// <summary>The screen carried no recognisable position mode.</summary>
    Unknown = 0,

    /// <summary>A fixed position is in use — the normal state for a stationary timing receiver.</summary>
    Hold,

    /// <summary>A position survey is in progress.</summary>
    Survey,
}

/// <summary>How much to trust the reported coordinates.</summary>
public enum PositionQualifier
{
    /// <summary>The screen carried no qualifier, which is the ordinary case on a held position.</summary>
    Unknown = 0,

    /// <summary>An initial estimate, not yet refined.</summary>
    Init,

    /// <summary>An average accumulated by a survey in progress.</summary>
    Average,

    /// <summary>A held fixed position.</summary>
    Held,
}

/// <summary>Which vertical datum the height is measured against.</summary>
/// <remarks>
/// Worth keeping distinct rather than normalising: the two differ by the geoid separation, which is
/// tens of metres in places, and a user checking a surveyed position against a map needs to know
/// which one the receiver printed.
/// </remarks>
public enum HeightDatum
{
    /// <summary>The screen did not say.</summary>
    Unknown = 0,

    /// <summary>Height above the WGS-84 reference ellipsoid.</summary>
    GpsEllipsoid,

    /// <summary>Height above mean sea level.</summary>
    Msl,
}

/// <summary>Why a position survey stopped making progress (§11.3).</summary>
/// <remarks>
/// An enum rather than free text because the UI branches on it: "fewer than four satellites" and
/// "poor geometry" want different advice, and matching on a display string would break the day a
/// firmware revision rewords one. §11.3 keeps no string form on the model for that reason — when
/// the text does not match the table the value is <see cref="Other"/> and the device's exact
/// wording goes to <see cref="ReceiverStatus.ParseWarnings"/>.
/// </remarks>
public enum SurveySuspendedReason
{
    /// <summary>The survey is not suspended.</summary>
    None = 0,

    /// <summary>Fewer than the four satellites a three-dimensional fix needs.</summary>
    TooFewSatellites,

    /// <summary>Enough satellites, but their geometry gives too weak a solution.</summary>
    PoorGeometry,

    /// <summary>No tracking data available at all.</summary>
    NoTrackData,

    /// <summary>
    /// Suspended for a reason this table does not cover. The device's wording is recorded in
    /// <see cref="ReceiverStatus.ParseWarnings"/>.
    /// </summary>
    Other,
}

/// <summary>A geodetic position as the receiver reports it.</summary>
/// <remarks>
/// The receiver prints degrees, minutes, and seconds with a hemisphere letter
/// (<c>N  47:31:18.822</c>). This record stores signed decimal degrees, which is what every
/// consumer — the position readout, the map link, the distance-from-survey calculation — actually
/// wants, while <see cref="ReceiverStatus.ParseWarnings"/> records anything that would not convert.
/// </remarks>
public sealed record GeoPosition
{
    /// <summary>Latitude in signed decimal degrees; positive north.</summary>
    public double? LatitudeDegrees { get; init; }

    /// <summary>Longitude in signed decimal degrees; positive east.</summary>
    public double? LongitudeDegrees { get; init; }

    /// <summary>Height in metres, measured against <see cref="ReceiverStatus.HeightDatum"/>.</summary>
    public double? HeightMetres { get; init; }

    /// <summary>
    /// Geoid separation in metres: the height of the geoid above the WGS-84 ellipsoid (#435).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what makes an MSL height convertible.</b> NMEA's <c>GGA</c> reports height above
    /// mean sea level and gives this alongside it, so ellipsoidal height is
    /// <see cref="HeightMetres"/> plus this figure. Without it an MSL height cannot be compared with
    /// anything a GNSS receiver computes natively, which is ellipsoidal.
    /// </para>
    /// <para>
    /// Negative over most of the world, and about −18.8 m on the bench — the sign is the geoid
    /// sitting <i>below</i> the ellipsoid, not an error.
    /// </para>
    /// </remarks>
    public double? GeoidSeparationMetres { get; init; }
}

/// <summary>
/// Dilution of precision: how much the satellite geometry multiplies ranging error (#435).
/// </summary>
/// <remarks>
/// <para>
/// A pure geometry figure, dimensionless, and smaller is better — roughly, 1 is ideal, under 2 is
/// excellent, over 20 is unusable. It says nothing about how good the ranging was, only how
/// favourably the satellites were placed to combine it, which is why it belongs beside a fix rather
/// than inside <see cref="GeoPosition"/>.
/// </para>
/// <para>
/// <b>Every constellation's <c>GSA</c> in a cycle carries the same values</b>, because the figure is
/// computed over the whole fix rather than per system. Reading one is reading all of them.
/// </para>
/// </remarks>
public sealed record DilutionOfPrecision
{
    /// <summary>Position (3D) dilution of precision.</summary>
    public double? Position { get; init; }

    /// <summary>Horizontal dilution of precision.</summary>
    public double? Horizontal { get; init; }

    /// <summary>Vertical dilution of precision.</summary>
    public double? Vertical { get; init; }

    /// <summary>True when nothing was parsed, so a consumer can treat it as absent.</summary>
    public bool IsEmpty => Position is null && Horizontal is null && Vertical is null;
}
