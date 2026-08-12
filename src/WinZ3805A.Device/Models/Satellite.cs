namespace WinZ3805A.Device.Models;

/// <summary>
/// A satellite the receiver is currently tracking, as it appears in the left-hand column group of
/// the status screen's acquisition table.
/// </summary>
/// <remarks>
/// Elevation, azimuth, and signal strength have no individual SCPI query — they exist only inside
/// <c>:SYST:STAT?</c> — which is why §11.1 makes the status screen the sole source for the
/// Satellites page. Every field except <see cref="Prn"/> is nullable because §11.1 forbids the
/// parser from throwing: a firmware revision that widens a column or prints a dash must degrade to
/// a missing value rather than a crash.
/// </remarks>
public sealed record TrackedSatellite
{
    /// <summary>The satellite's PRN number. The row would not exist without one, so this is required.</summary>
    public required int Prn { get; init; }

    /// <summary>Elevation above the horizon in degrees, or <see langword="null"/> if the column did not parse.</summary>
    public int? ElevationDegrees { get; init; }

    /// <summary>Azimuth in degrees clockwise from true north, or <see langword="null"/> if the column did not parse.</summary>
    public int? AzimuthDegrees { get; init; }

    /// <summary>
    /// The signal-strength reading, on whichever scale
    /// <see cref="ReceiverStatus.SignalStrengthKind"/> names.
    /// </summary>
    /// <remarks>
    /// Deliberately a bare number with no unit attached. §11.1 warns that the two scales are not
    /// interchangeable — 26–55 with ≥ 35 good on 58503B-class units, 0–255 with 20–30 weak on
    /// 59551A-class units — so anything that renders this value must read the kind first.
    /// </remarks>
    public int? SignalStrength { get; init; }
}

/// <summary>
/// A satellite the receiver expects to be visible but is not tracking, from the "Not Tracking"
/// column group of the acquisition table.
/// </summary>
/// <remarks>
/// The not-tracking table carries no signal-strength column — there is no signal to report — which
/// is the structural difference that lets the parser tell the two groups apart even when a firmware
/// revision reorders them.
/// </remarks>
public sealed record PredictedSatellite
{
    /// <summary>The satellite's PRN number.</summary>
    public required int Prn { get; init; }

    /// <summary>Predicted elevation in degrees, or <see langword="null"/> if the column did not parse.</summary>
    public int? ElevationDegrees { get; init; }

    /// <summary>Predicted azimuth in degrees clockwise from true north, or <see langword="null"/> if the column did not parse.</summary>
    public int? AzimuthDegrees { get; init; }
}
