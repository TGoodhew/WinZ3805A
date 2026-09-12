using System.Globalization;

namespace WinZ3805A.Device.Models;

/// <summary>
/// Which constellation a satellite belongs to (#424).
/// </summary>
/// <remarks>
/// <para>
/// A satellite number is only unique <i>within</i> a constellation. NMEA 4.10 gives each one its own
/// range — GPS 1–32, SBAS 33–64, GLONASS 65–96 — so a conforming receiver never collides, but
/// receivers exist that number per constellation and rely on the talker to disambiguate. That is
/// legal in earlier revisions, and it is what the forM8N M8130 in
/// <c>tests/WinZ3805A.Tests/Nmea/Captures/</c> does out of the box: its BeiDou satellites arrive as
/// 6, 20, 23, 24 and 25, every one of them inside the GPS range.
/// </para>
/// <para>
/// <b><see cref="Unknown"/> is the normal value for most of this application, not an error.</b> The
/// SmartClock and UCCM families are single-constellation by nature — their status screens print a
/// bare PRN and their receiver commands take one — so nothing outside the NMEA driver sets this, and
/// every surface renders a satellite exactly as it did before when it is <see cref="Unknown"/>. It
/// is also the honest answer for a <c>$GNGSV</c> page, where the talker itself declines to say.
/// </para>
/// </remarks>
public enum SatelliteConstellation
{
    /// <summary>The receiver did not say, or had no way to. Rendered as a bare number.</summary>
    Unknown = 0,

    /// <summary>The United States' GPS. NMEA talker <c>GP</c>.</summary>
    Gps,

    /// <summary>A satellite-based augmentation system — WAAS, EGNOS, MSAS. NMEA numbers these 33–64 inside the GPS talker.</summary>
    Sbas,

    /// <summary>Russia's GLONASS. NMEA talker <c>GL</c>.</summary>
    Glonass,

    /// <summary>Europe's Galileo. NMEA talker <c>GA</c>.</summary>
    Galileo,

    /// <summary>China's BeiDou. NMEA talker <c>GB</c>, and <c>BD</c> on some older firmware.</summary>
    BeiDou,

    /// <summary>Japan's QZSS. NMEA talker <c>GQ</c>.</summary>
    Qzss,

    /// <summary>India's NavIC, formerly IRNSS. NMEA talker <c>GI</c>.</summary>
    NavIC,
}

/// <summary>
/// A satellite's identity: its number, and the constellation that number belongs to (#424).
/// </summary>
/// <remarks>
/// <para>
/// This type exists because a number alone is not an identity. <c>NmeaStatusParser</c> deduped a
/// cycle's satellites with a plain <c>HashSet&lt;int&gt;</c> of numbers, so against a receiver that
/// numbers per constellation the second claimant of a number was silently discarded — measured at
/// <b>1,217 of 1,800 cycles</b> in <c>form8n-gps-beidou-outdoors.nmea</c>, where the sky plot and the
/// satellite count therefore under-reported in 68% of cycles. That reads as poor reception or a bad
/// antenna, which is a plausible wrong explanation that sends someone onto the roof.
/// </para>
/// <para>
/// <b>The alternative was rejected on the evidence.</b> Keeping the number as the identity and
/// deduping per talker puts two satellites numbered 4 into the model and draws two markers that say
/// 4 — against that receiver, 1,217 times a sitting. Widening the identity is the only remedy that
/// lets the surface tell them apart.
/// </para>
/// </remarks>
/// <param name="Constellation">Which constellation the number belongs to.</param>
/// <param name="Prn">The satellite's number within that constellation.</param>
public readonly record struct SatelliteId(SatelliteConstellation Constellation, int Prn)
    : IComparable<SatelliteId>
{
    /// <summary>
    /// How the satellite is written: the RINEX system letter and a two-digit number — <c>G04</c>,
    /// <c>C04</c> — or the bare number when the constellation is <see cref="SatelliteConstellation.Unknown"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The letters are RINEX's, which is the convention u-center, the IGS products and Lady Heather
    /// all use, so a user who knows one knows this. They are not invented here.
    /// </para>
    /// <para>
    /// Formatted invariantly on purpose: this is an identifier the receiver assigned, not a reading,
    /// and a satellite's name should not change with the machine's regional settings. For the values
    /// in question — one to a few hundred — the invariant and current-culture forms are identical
    /// anyway, so nothing a SmartClock shows moves.
    /// </para>
    /// </remarks>
    public string Designation => Constellation is SatelliteConstellation.Unknown
        ? Prn.ToString(CultureInfo.InvariantCulture)
        : SatelliteConstellations.Code(Constellation) + Prn.ToString("00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Orders by number first and constellation second, so §9.10.2's keyboard order stays
    /// number-ascending and two claimants of one number land next to each other.
    /// </summary>
    public int CompareTo(SatelliteId other)
    {
        int byPrn = Prn.CompareTo(other.Prn);
        return byPrn != 0 ? byPrn : Constellation.CompareTo(other.Constellation);
    }

    /// <summary>Orders two identities by number, then constellation.</summary>
    public static bool operator <(SatelliteId left, SatelliteId right) => left.CompareTo(right) < 0;

    /// <summary>Orders two identities by number, then constellation.</summary>
    public static bool operator <=(SatelliteId left, SatelliteId right) => left.CompareTo(right) <= 0;

    /// <summary>Orders two identities by number, then constellation.</summary>
    public static bool operator >(SatelliteId left, SatelliteId right) => left.CompareTo(right) > 0;

    /// <summary>Orders two identities by number, then constellation.</summary>
    public static bool operator >=(SatelliteId left, SatelliteId right) => left.CompareTo(right) >= 0;
}

/// <summary>The words and letters for a <see cref="SatelliteConstellation"/>.</summary>
/// <remarks>
/// Kept beside the enum rather than in the UI so the driver, the sky plot and the tables all say the
/// same thing about the same satellite — the property that <c>TrackedSatelliteRow.Description</c>
/// exists to hold between the plot and the list.
/// </remarks>
public static class SatelliteConstellations
{
    /// <summary>
    /// The RINEX system letter — <c>G</c>, <c>S</c>, <c>R</c>, <c>E</c>, <c>C</c>, <c>J</c>,
    /// <c>I</c> — or the empty string when the constellation is not known.
    /// </summary>
    public static string Code(SatelliteConstellation constellation) => constellation switch
    {
        SatelliteConstellation.Gps => "G",
        SatelliteConstellation.Sbas => "S",
        SatelliteConstellation.Glonass => "R",
        SatelliteConstellation.Galileo => "E",
        SatelliteConstellation.BeiDou => "C",
        SatelliteConstellation.Qzss => "J",
        SatelliteConstellation.NavIC => "I",
        _ => string.Empty,
    };

    /// <summary>
    /// The constellation's name as a reader knows it, for the sentence a screen reader speaks and
    /// the tooltip a pointer shows. Empty when it is not known, so a caller can leave it unsaid.
    /// </summary>
    public static string Name(SatelliteConstellation constellation) => constellation switch
    {
        SatelliteConstellation.Gps => "GPS",
        SatelliteConstellation.Sbas => "SBAS",
        SatelliteConstellation.Glonass => "GLONASS",
        SatelliteConstellation.Galileo => "Galileo",
        SatelliteConstellation.BeiDou => "BeiDou",
        SatelliteConstellation.Qzss => "QZSS",
        SatelliteConstellation.NavIC => "NavIC",
        _ => string.Empty,
    };
}

/// <summary>
/// A satellite the receiver is currently tracking, as it appears in the left-hand column group of
/// the status screen's acquisition table.
/// </summary>
/// <remarks>
/// Elevation, azimuth, and signal strength have no individual SCPI query — they exist only inside
/// <c>:SYST:STAT?</c> — which is why §7.3 makes the status screen the sole source for the
/// Satellites page. Every field except <see cref="Prn"/> is nullable because §11.1 forbids the
/// parser from throwing: a firmware revision that widens a column or prints a dash must degrade to
/// a missing value rather than a crash.
/// </remarks>
public sealed record TrackedSatellite
{
    /// <summary>The satellite's PRN number. The row would not exist without one, so this is required.</summary>
    public required int Prn { get; init; }

    /// <summary>
    /// Which constellation <see cref="Prn"/> belongs to, or
    /// <see cref="SatelliteConstellation.Unknown"/> when the receiver had no way to say (#424).
    /// </summary>
    /// <remarks>
    /// Optional, and <see cref="SatelliteConstellation.Unknown"/> by default, because only the NMEA
    /// driver can answer it: a SmartClock's acquisition table and a UCCM's status screen both print
    /// a bare number for a receiver that has one constellation. See <see cref="Id"/> for what
    /// actually identifies a satellite.
    /// </remarks>
    public SatelliteConstellation Constellation { get; init; }

    /// <summary>The satellite's identity — its number <i>and</i> its constellation (#424).</summary>
    public SatelliteId Id => new(Constellation, Prn);

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

    /// <summary>
    /// Which constellation <see cref="Prn"/> belongs to, or
    /// <see cref="SatelliteConstellation.Unknown"/> when the receiver had no way to say (#424).
    /// </summary>
    /// <remarks>
    /// Set only by the NMEA driver, for the reason given on
    /// <see cref="TrackedSatellite.Constellation"/>.
    /// </remarks>
    public SatelliteConstellation Constellation { get; init; }

    /// <summary>The satellite's identity — its number <i>and</i> its constellation (#424).</summary>
    public SatelliteId Id => new(Constellation, Prn);

    /// <summary>Predicted elevation in degrees, or <see langword="null"/> if the column did not parse.</summary>
    public int? ElevationDegrees { get; init; }

    /// <summary>Predicted azimuth in degrees clockwise from true north, or <see langword="null"/> if the column did not parse.</summary>
    public int? AzimuthDegrees { get; init; }

    /// <summary>Whether the receiver marked this satellite as one it is attempting to track.</summary>
    /// <remarks>
    /// The screen prints an asterisk before the PRN and explains it in its own legend —
    /// <c>*attempting to track</c>. It is only seen while acquiring, which is why nothing had met it
    /// until a receiver was power-cycled with a clear sky (#4).
    /// </remarks>
    public bool AttemptingToTrack { get; init; }
}
