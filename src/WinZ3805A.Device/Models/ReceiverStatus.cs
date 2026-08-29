namespace WinZ3805A.Device.Models;

/// <summary>How far the receiver's 10 MHz and 1 PPS outputs can be trusted.</summary>
/// <remarks>
/// Read from the bracketed annotation on the <c>SYNCHRONIZATION</c> banner. The middle value
/// exists because a receiver that has lost GPS keeps driving its outputs from the oscillator, and
/// the distinction between "usable but drifting" and "do not use" is the single most important
/// thing the main window has to convey.
/// </remarks>
public enum OutputValidity
{
    /// <summary>The banner carried no recognisable annotation.</summary>
    Unknown = 0,

    /// <summary>Outputs are not to be trusted.</summary>
    Invalid,

    /// <summary>Outputs are usable but the accuracy specification no longer holds.</summary>
    ValidReduced,

    /// <summary>Outputs are within specification.</summary>
    Valid,
}

/// <summary>
/// The receiver's SmartClock mode — HP's own term for the disciplining state machine, kept
/// verbatim per Appendix B.
/// </summary>
/// <remarks>
/// The status screen prints all four modes as a menu and marks the active one with <c>&gt;&gt;</c>,
/// so the parser looks for the marker rather than for any particular mode word.
/// </remarks>
public enum SmartClockMode
{
    /// <summary>No mode line carried the <c>&gt;&gt;</c> marker.</summary>
    Unknown = 0,

    /// <summary>Locked to GPS and disciplining the oscillator.</summary>
    Locked,

    /// <summary>Reacquiring after a loss of GPS.</summary>
    Recovery,

    /// <summary>Running on the oscillator alone, with no GPS discipline.</summary>
    Holdover,

    /// <summary>Warming up after power was applied.</summary>
    PowerUp,
}

/// <summary>Which signal-strength scale the acquisition table is printed on.</summary>
/// <remarks>
/// §11.1 is emphatic that the two are not interchangeable: <c>C/N</c> on 58503B-class units runs
/// 26–55 with 35 and above good, while <c>SS</c> on 59551A-class units runs 0–255 with 20–30 weak.
/// A strength bar scaled to the wrong one is not merely mislabelled, it is wrong by a factor of
/// five, so this is recorded from the header the receiver actually printed rather than inferred
/// from the model number.
/// </remarks>
public enum SignalStrengthKind
{
    /// <summary>No signal-strength column was found, which is normal when nothing is tracked.</summary>
    Unknown = 0,

    /// <summary>Carrier-to-noise ratio, printed as <c>C/N</c>.</summary>
    CarrierToNoise,

    /// <summary>Raw signal strength, printed as <c>SS</c>.</summary>
    SignalStrength,
}

/// <summary>The time scale the receiver's clock display is referenced to.</summary>
public enum TimeScale
{
    /// <summary>The time row carried no recognisable scale.</summary>
    Unknown = 0,

    /// <summary>GPS time, which does not include leap seconds.</summary>
    Gps,

    /// <summary>Coordinated Universal Time.</summary>
    Utc,

    /// <summary>Local time derived from GPS time.</summary>
    LocalGps,

    /// <summary>Local time derived from UTC.</summary>
    Local,
}

/// <summary>Whether a leap second is scheduled at the end of the current UTC month.</summary>
public enum LeapSecondPending
{
    /// <summary>No leap second is pending.</summary>
    None = 0,

    /// <summary>A second will be inserted.</summary>
    Plus,

    /// <summary>A second will be removed.</summary>
    Minus,
}

/// <summary>
/// The <c>1PPS CLK</c> advisory as one of the §11.3 values.
/// </summary>
/// <remarks>
/// <para>
/// §11.3 requires an enum here "because the UI branches on them", and keeps no string form of the
/// advisory on the model at all: the mapping from the device's text lives entirely in the parser,
/// so no view is able to branch on a display string even by accident.
/// </para>
/// <para>
/// <c>Assessing stability</c> is the case that shows why. It arrives with nought to three trailing
/// dots, which animate on the device's own screen — four spellings of one state to a string
/// comparison. The dots carry no information and are stripped before matching.
/// </para>
/// </remarks>
public enum ClockAdvisory
{
    /// <summary>No advisory was printed.</summary>
    None = 0,

    /// <summary>Locked and referenced to UTC.</summary>
    SynchronizedToUtc,

    /// <summary>Locked and referenced to GPS time.</summary>
    SynchronizedToGpsTime,

    /// <summary>Hysteresis is being applied before the receiver commits to a lock.</summary>
    AssessingStability,

    /// <summary>A 1 PPS is present but is not trusted.</summary>
    QuestionableAccuracy,

    /// <summary>Inaccurate because no satellites are being tracked.</summary>
    InaccurateNotTracking,

    /// <summary>Inaccurate because the position is not yet known.</summary>
    InaccurateInaccuratePosition,

    /// <summary>No 1 PPS at all, or the GPS engine is idle.</summary>
    AbsentOrFrequencyError,

    /// <summary>The GPS receiver engine reported an error.</summary>
    InvalidGpsReceiverError,

    /// <summary>
    /// An advisory this table does not cover. The device's wording is recorded in
    /// <see cref="ReceiverStatus.ParseWarnings"/>.
    /// </summary>
    Other,
}

/// <summary>
/// One decoded <c>:SYST:STAT?</c> status screen — the receiver's entire visible state.
/// </summary>
/// <remarks>
/// <para>
/// The shape follows §11.2. Almost every member is nullable, and that is the type system carrying
/// §11.1's central rule: the parser never throws, an unparseable field becomes <see langword="null"/>,
/// and the UI renders it as an em dash. With nullable reference types and warnings-as-errors, a
/// consumer that forgets a field cannot compile.
/// </para>
/// <para>
/// A <see langword="record"/> with <c>init</c> accessors per §6.4 — one screen is one immutable
/// value, and the polling loop replaces it rather than mutating it, which is what makes it safe to
/// hand to the UI thread without copying.
/// </para>
/// </remarks>
public sealed record ReceiverStatus
{
    // ---- SYNCHRONIZATION ----------------------------------------------------------------------

    /// <summary>How far the outputs can be trusted.</summary>
    public OutputValidity Outputs { get; init; }

    /// <summary>The active SmartClock mode.</summary>
    public SmartClockMode Mode { get; init; }

    /// <summary>The text after the mode name, such as <c>stabilizing frequency</c>.</summary>
    public string? ModeDetail { get; init; }

    /// <summary>Time figure of merit, lower being better.</summary>
    public int? Tfom { get; init; }

    /// <summary>Frequency figure of merit, lower being better.</summary>
    public int? Ffom { get; init; }

    /// <summary>The 1 PPS time interval against GPS, in nanoseconds.</summary>
    public double? OnePpsTiNanoseconds { get; init; }

    /// <summary>The holdover threshold, in seconds.</summary>
    public double? HoldThresholdSeconds { get; init; }

    /// <summary>Predicted holdover uncertainty over the stated initial interval, in seconds.</summary>
    public double? HoldoverPredictedSeconds { get; init; }

    /// <summary>Present holdover uncertainty, in seconds.</summary>
    public double? HoldoverPresentSeconds { get; init; }

    /// <summary>How long the receiver has been in holdover <i>and recovery</i>, together.</summary>
    /// <remarks>
    /// Not "how long since the signal was lost", though it is easy to read it that way. The Z3801A
    /// guide states twice that this is "the cumulative duration of holdover and recovery
    /// operations", so the counter keeps running after the antenna is reconnected and only stops
    /// when lock is regained. What it measures is how long the outputs have been degraded, which is
    /// the question a user actually has.
    /// </remarks>
    public TimeSpan? HoldoverDuration { get; init; }

    // ---- ACQUISITION --------------------------------------------------------------------------

    /// <summary>Whether the GPS engine's own 1 PPS is valid, from the acquisition banner.</summary>
    public bool GpsOnePpsValid { get; init; }

    /// <summary>Satellites currently being tracked.</summary>
    public IReadOnlyList<TrackedSatellite> Tracked { get; init; } = [];

    /// <summary>Satellites expected to be visible but not tracked.</summary>
    public IReadOnlyList<PredictedSatellite> NotTracked { get; init; } = [];

    /// <summary>The elevation mask below which satellites are ignored, in degrees.</summary>
    public int? ElevationMaskDegrees { get; init; }

    /// <summary>Which scale <see cref="TrackedSatellite.SignalStrength"/> is expressed on.</summary>
    public SignalStrengthKind SignalStrengthKind { get; init; }

    // ---- TIME ---------------------------------------------------------------------------------

    /// <summary>The time scale the clock row is referenced to.</summary>
    public TimeScale TimeScale { get; init; }

    /// <summary>The date and time exactly as the device reported it, uncorrected.</summary>
    public DateTimeOffset? DeviceDateTime { get; init; }

    /// <summary>
    /// Whether the clock row carried the power-up marker, meaning the time has not yet been
    /// corrected from GPS (§11.2, #245).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The receiver prints <c>(?)</c> between the time and the date — <c>[?]</c> in the Z3801A user
    /// guide, Figure 3-1 — and the guide says the value is *"the default power-up setting … corrected
    /// when the first satellite is tracked"*.
    /// </para>
    /// <para>
    /// <b>This flag is why the marker is not simply tolerated in the pattern.</b> The two known
    /// examples show how far apart a marked time can be from the truth: the screen captured from
    /// this unit read <c>05:10:04 (?) 12 Jan 2007</c> and was right to the minute, because the
    /// oscillator held time across the power cycle, while the manual's <c>12:00:00[?] 01 JAN 1996</c>
    /// is a placeholder that is arbitrarily wrong. <b>The marker is the only thing that distinguishes
    /// them.</b> Parsing the value and dropping the marker would convert a knowable caveat into a
    /// silent inaccuracy — worse than the old behaviour of refusing the row, not better.
    /// </para>
    /// <para>
    /// Distinct from <see cref="OnePpsClockAdvisory"/>, which describes the 1 PPS <i>signal</i> and is
    /// read from the <c>GPS 1PPS …</c> line two rows below. This is a property of the time-of-day
    /// reading itself.
    /// </para>
    /// </remarks>
    public bool DeviceTimeIsProvisional { get; init; }

    /// <summary>
    /// How many 1024-week GPS epochs the device's date is behind, per §7.4. Zero on a receiver
    /// whose firmware has not rolled over.
    /// </summary>
    public int WeekRolloverEpochs { get; init; }

    /// <summary>
    /// <see cref="DeviceDateTime"/> advanced by <see cref="WeekRolloverEpochs"/> epochs, or
    /// <see langword="null"/> when there is no date to correct.
    /// </summary>
    /// <remarks>
    /// §7.4 forbids silently substituting this for the raw value: the UI shows the corrected date
    /// with a badge and keeps the device's own date in the tooltip, because a user who sees the
    /// wrong year and no explanation reasonably assumes the hardware has failed.
    /// </remarks>
    public DateTimeOffset? CorrectedDateTime { get; init; }

    /// <summary>The <c>1PPS CLK</c> advisory, decoded (§11.3).</summary>
    public ClockAdvisory OnePpsClockAdvisory { get; init; }

    /// <summary>The configured antenna cable delay, in nanoseconds.</summary>
    public double? AntennaDelayNanoseconds { get; init; }

    /// <summary>Whether a leap second is scheduled.</summary>
    public LeapSecondPending LeapPending { get; init; }

    // ---- POSITION -----------------------------------------------------------------------------

    /// <summary>Whether the receiver is holding a position or surveying for one.</summary>
    public PositionMode PositionMode { get; init; }

    /// <summary>Survey progress, 0 to 100, when a survey is running.</summary>
    public double? SurveyPercentComplete { get; init; }

    /// <summary>Why a survey is suspended, decoded (§11.3).</summary>
    public SurveySuspendedReason SurveySuspendedReason { get; init; }

    /// <summary>The reported position.</summary>
    public GeoPosition? Position { get; init; }

    /// <summary>How much to trust the reported position.</summary>
    public PositionQualifier PositionQualifier { get; init; }

    /// <summary>Which datum <see cref="GeoPosition.HeightMetres"/> is measured against.</summary>
    public HeightDatum HeightDatum { get; init; }

    // ---- HEALTH -------------------------------------------------------------------------------

    /// <summary>Whether the health monitor banner read <c>OK</c>.</summary>
    public bool HealthOk { get; init; }

    /// <summary>Each health item the receiver listed, in screen order, against whether it passed.</summary>
    /// <remarks>
    /// A dictionary keyed by the device's own label rather than a fixed set of properties, because
    /// the item list differs across the family and an unrecognised item must still reach the
    /// Diagnostics page rather than being dropped.
    /// </remarks>
    public IReadOnlyDictionary<string, bool> HealthItems { get; init; } =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    // ---- PROVENANCE ---------------------------------------------------------------------------

    /// <summary>When this screen was parsed, from the injected <see cref="TimeProvider"/>.</summary>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// Everything the parser could not make sense of, in the order it was met.
    /// </summary>
    /// <remarks>
    /// Surfaced on the Diagnostics page so that a field report about an odd firmware revision is
    /// actionable — "it shows dashes" is not a bug report, "unrecognised health item 'Xtal Pwr'" is.
    /// </remarks>
    public IReadOnlyList<string> ParseWarnings { get; init; } = [];
}
