namespace WinZ3805A.Device.Models;

/// <summary>
/// How uncertain the computed position actually is, in metres (#516).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the half <see cref="DilutionOfPrecision"/> cannot supply.</b> DOP is geometry alone: it
/// says how favourably the satellites were placed to combine the ranging error, and nothing at all
/// about how large that error was. A receiver with superb geometry and a badly multipathed signal
/// reports an excellent DOP and a poor position, and from DOP there is no way to tell. These figures
/// are the receiver's own estimate of the error it ended up with, which is what a surveyor reads.
/// </para>
/// <para>
/// It comes from <c>GST</c>, which no receiver on the bench emits by default. Both u-blox units will
/// send it when asked with <c>UBX-CFG-MSG</c>, which is how the capture behind these tests was
/// taken; <c>build/Capture-Talker.ps1 -EnableSentences GST</c> does it. So a device that reports
/// nothing here is the ordinary case rather than a fault, and the UI must say "not reported" rather
/// than implying a value is on its way.
/// </para>
/// <para>
/// <b>The error ellipse is usually absent, and that is the receiver rather than the parser.</b>
/// u-blox leaves <see cref="SemiMajorMetres"/>, <see cref="SemiMinorMetres"/> and
/// <see cref="OrientationDegrees"/> empty and fills only the RMS and the three per-axis deviations —
/// measured on the bench forM8N, 12 Sep 2026. Every field is independently nullable for that reason:
/// a partly filled sentence is normal, not damage.
/// </para>
/// </remarks>
public sealed record PositionUncertainty
{
    /// <summary>
    /// Root mean square of the standard deviation of the range inputs to the solution, in metres.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A summary of how noisy the pseudoranges were, before geometry is applied. It describes the
    /// raw ranging rather than the position the solver extracted from it, so it is legitimately
    /// larger than the per-axis deviations below and must never be presented as a position error.
    /// </para>
    /// <para>
    /// <b>On the bench hardware this field is not trustworthy, and the capture says so.</b> Across
    /// the 300 <c>GST</c> sentences in <c>form8n-gst-gbs.nmea</c> it ranges from 17 to
    /// <b>3,179,277</b> — six orders of magnitude, in consecutive seconds, from a stationary
    /// receiver holding one fix — while the three deviations beside it stay inside 1.7–2.1 m and
    /// 3.6–4.1 m through the very same cycles. 34 of the 300 are in the millions. Every one of those
    /// sentences passes its checksum, so this is what the receiver sends rather than something the
    /// parser did.
    /// </para>
    /// <para>
    /// It is carried because it is in the sentence and discarding data at the parser is not this
    /// layer's decision to make. <b>Nothing displays it</b>, and that is deliberate: the readout is
    /// built from <see cref="HorizontalDrmsMetres"/> and <see cref="AltitudeSigmaMetres"/>, which
    /// are the figures this hardware computes credibly. Anything minded to surface this one should
    /// read the numbers above first.
    /// </para>
    /// </remarks>
    public double? RangeResidualRmsMetres { get; init; }

    /// <summary>Standard deviation of the error ellipse's semi-major axis, in metres.</summary>
    public double? SemiMajorMetres { get; init; }

    /// <summary>Standard deviation of the error ellipse's semi-minor axis, in metres.</summary>
    public double? SemiMinorMetres { get; init; }

    /// <summary>Orientation of the error ellipse's semi-major axis, degrees from true north.</summary>
    public double? OrientationDegrees { get; init; }

    /// <summary>Standard deviation of the latitude error, in metres.</summary>
    public double? LatitudeSigmaMetres { get; init; }

    /// <summary>Standard deviation of the longitude error, in metres.</summary>
    public double? LongitudeSigmaMetres { get; init; }

    /// <summary>Standard deviation of the altitude error, in metres.</summary>
    public double? AltitudeSigmaMetres { get; init; }

    /// <summary>
    /// The two horizontal deviations combined — distance root mean square — or
    /// <see langword="null"/> if either is missing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One number for a horizontal position error, which is what a glanceable readout wants and what
    /// the two axes separately are not. It is the ordinary DRMS: the root of the sum of the squares,
    /// which is the standard deviation of the horizontal error when the two axes are uncorrelated.
    /// </para>
    /// <para>
    /// <b>Roughly 65% confidence, not 95%.</b> One DRMS is about a 65% circle at typical axis
    /// ratios, and doubling it gives about 95%. Anything presenting this to a user has to say which,
    /// because a figure quoted at the wrong confidence is wrong by a factor of two and looks
    /// entirely plausible.
    /// </para>
    /// </remarks>
    public double? HorizontalDrmsMetres =>
        LatitudeSigmaMetres is double lat && LongitudeSigmaMetres is double lon
            ? Math.Sqrt((lat * lat) + (lon * lon))
            : null;

    /// <summary>True when nothing was parsed, so a consumer can treat it as absent.</summary>
    public bool IsEmpty =>
        RangeResidualRmsMetres is null &&
        SemiMajorMetres is null &&
        SemiMinorMetres is null &&
        OrientationDegrees is null &&
        LatitudeSigmaMetres is null &&
        LongitudeSigmaMetres is null &&
        AltitudeSigmaMetres is null;
}

/// <summary>
/// Whether the receiver believes one of the satellites in its solution is lying (#516).
/// </summary>
/// <remarks>
/// <para>
/// RAIM — receiver autonomous integrity monitoring — is a consistency check the receiver runs on its
/// own solution. With more satellites than the fix strictly needs, the surplus measurements can be
/// checked against each other, and one that does not agree can be identified and named. This is the
/// closest thing NMEA 0183 has to the SmartClock's health monitor.
/// </para>
/// <para>
/// <b>No fault named is the good state, and it is the overwhelmingly common one.</b> On the bench
/// the whole back half of <c>GBS</c> is empty every cycle while the three expected errors are
/// filled — so <see cref="SuspectSatelliteId"/> being <see langword="null"/> must read as "nothing
/// flagged", never as "not reported". The distinction is <see cref="IsEmpty"/>: an absent sentence
/// says nothing about integrity, while a present one with no satellite named is a positive result.
/// </para>
/// <para>
/// It comes from <c>GBS</c>, which like <c>GST</c> is switched on rather than sent by default. #516
/// expected an indoor fix might be too sparse to produce anything useful; it was not — a 12-satellite
/// indoor fix filled the error estimates on the first sitting.
/// </para>
/// </remarks>
public sealed record IntegrityReport
{
    /// <summary>Expected error in latitude, in metres.</summary>
    public double? LatitudeErrorMetres { get; init; }

    /// <summary>Expected error in longitude, in metres.</summary>
    public double? LongitudeErrorMetres { get; init; }

    /// <summary>Expected error in altitude, in metres.</summary>
    public double? AltitudeErrorMetres { get; init; }

    /// <summary>
    /// The satellite the receiver believes is faulty, or <see langword="null"/> when none is.
    /// </summary>
    /// <remarks>
    /// An NMEA satellite number, in the same numbering the <c>GSV</c> pages use, so it can be matched
    /// against the sky plot. Null is the normal, healthy answer — see the remarks on the record.
    /// </remarks>
    public int? SuspectSatelliteId { get; init; }

    /// <summary>Probability that a fault present in the solution went undetected.</summary>
    public double? MissedDetectionProbability { get; init; }

    /// <summary>Estimated range bias on the suspect satellite, in metres.</summary>
    public double? BiasMetres { get; init; }

    /// <summary>Standard deviation of <see cref="BiasMetres"/>, in metres.</summary>
    public double? BiasSigmaMetres { get; init; }

    /// <summary>
    /// True when the receiver has named a satellite it believes is faulty.
    /// </summary>
    /// <remarks>
    /// Deliberately reads off the satellite id alone. The bias fields are filled only alongside it,
    /// and a rule needing all of them to agree would report healthy on a receiver that names a
    /// satellite without estimating its bias.
    /// </remarks>
    public bool FaultSuspected => SuspectSatelliteId is not null;

    /// <summary>True when nothing was parsed, so a consumer can treat it as absent.</summary>
    /// <remarks>
    /// <b>Not the same as healthy.</b> This is "the receiver did not tell us", which is what every
    /// receiver that has not been asked for <c>GBS</c> reports. A present report naming no satellite
    /// is the healthy state and is not empty.
    /// </remarks>
    public bool IsEmpty =>
        LatitudeErrorMetres is null &&
        LongitudeErrorMetres is null &&
        AltitudeErrorMetres is null &&
        SuspectSatelliteId is null &&
        MissedDetectionProbability is null &&
        BiasMetres is null &&
        BiasSigmaMetres is null;
}
