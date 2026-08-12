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
}
