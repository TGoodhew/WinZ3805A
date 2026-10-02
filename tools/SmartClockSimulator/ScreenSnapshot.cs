namespace WinZ3805A.Simulation.SmartClock;

/// <summary>Everything one <c>:SYST:STAT?</c> screen shows, as values rather than text.</summary>
/// <remarks>
/// <para>
/// <see cref="StatusScreenWriter"/> turns this into the 27 lines the receiver prints, and
/// the simulated receiver produces one from its state. Keeping the two apart is what lets
/// the writer be checked against every captured screen on its own: a test builds the snapshot each
/// capture describes and requires the writer to reproduce the capture's bytes exactly.
/// </para>
/// <para>
/// Text the receiver chooses from a fixed vocabulary (the mode detail, the 1 PPS advisory) is kept
/// as the receiver's own words, because the vocabulary is the manual's and the simulator should be
/// able to say anything the receiver can, including the phrasings no capture has shown yet.
/// </para>
/// </remarks>
public sealed record ScreenSnapshot
{
    /// <summary>The SYNCHRONIZATION summary.</summary>
    public OutputsSummary Outputs { get; init; }

    /// <summary>Which of the four SmartClock modes carries the <c>&gt;&gt;</c> marker.</summary>
    public ClockMode Mode { get; init; }

    /// <summary>What follows the active mode's label after a colon, or null for nothing.</summary>
    /// <remarks>
    /// Captured: <c>stabilizing frequency</c>, <c>fine freq adj</c>, <c>GPS acquisition</c>,
    /// <c>GPS 1PPS invalid</c>. The manual adds <c>manually initiated</c>,
    /// <c>1 PPS TI exceeds hold threshold</c> and <c>internal hardware problem</c> for holdover.
    /// </remarks>
    public string? ModeDetail { get; init; }

    /// <summary>The bracketed <c>[TI …]</c> after the detail, in nanoseconds, or null for none.</summary>
    public double? ModeTimeIntervalNanoseconds { get; init; }

    /// <summary>Time figure of merit, 3 to 9.</summary>
    public int Tfom { get; init; }

    /// <summary>Frequency figure of merit, 0 to 3.</summary>
    public int Ffom { get; init; }

    /// <summary>The 1PPS TI reading in nanoseconds, or null for <c>--</c>.</summary>
    public double? TimeIntervalNanoseconds { get; init; }

    /// <summary>The holdover threshold, in microseconds.</summary>
    public double HoldThresholdMicroseconds { get; init; } = 1.0;

    /// <summary>The predicted holdover uncertainty over 24 hours, in microseconds, or null for <c>--</c>.</summary>
    public double? PredictMicroseconds { get; init; }

    /// <summary>The present holdover uncertainty in microseconds, or null when not shown.</summary>
    public double? PresentMicroseconds { get; init; }

    /// <summary>How long holdover (and the recovery after it) has lasted, or null when not shown.</summary>
    public TimeSpan? HoldoverDuration { get; init; }

    /// <summary>The ACQUISITION summary: whether the GPS 1 PPS is valid.</summary>
    public bool GpsOnePpsValid { get; init; }

    /// <summary>Satellites being tracked, in the order printed (ascending PRN on the bench unit).</summary>
    public IReadOnlyList<TrackedSatellite> Tracked { get; init; } = [];

    /// <summary>Satellites predicted visible but not tracked, in the order printed.</summary>
    public IReadOnlyList<UntrackedSatellite> NotTracked { get; init; } = [];

    /// <summary>The time scale label: <c>UTC</c>, <c>GPS</c>, <c>LOCL GPS</c> or <c>LOCAL</c>.</summary>
    public string TimeScale { get; init; } = "UTC";

    /// <summary>The time and date the receiver prints, already 1024 weeks behind where it is.</summary>
    public DateTime Time { get; init; }

    /// <summary>Whether the time carries the <c>(?)</c> of a default power-up setting.</summary>
    public bool TimeProvisional { get; init; }

    /// <summary>The words after <c>GPS 1PPS</c>, e.g. <c>Synchronized to UTC</c>.</summary>
    public string ClockAdvisory { get; init; } = "Synchronized to UTC";

    /// <summary>The antenna delay the screen shows, in whole nanoseconds.</summary>
    public int AntennaDelayNanoseconds { get; init; }

    /// <summary>The position panel.</summary>
    public PositionPanel Position { get; init; } = new();

    /// <summary>The elevation mask, in whole degrees.</summary>
    public int ElevationMaskDegrees { get; init; } = 10;

    /// <summary>The six health monitor items.</summary>
    public HealthPanel Health { get; init; } = new();
}

/// <summary>The three SYNCHRONIZATION summaries the manual lists (p. 3-12).</summary>
public enum OutputsSummary
{
    /// <summary><c>Outputs Invalid</c>, while warming up.</summary>
    Invalid = 0,

    /// <summary><c>Outputs Valid/Reduced Accuracy</c>, in holdover or before steady state.</summary>
    ValidReducedAccuracy,

    /// <summary><c>Outputs Valid</c>, in steady state.</summary>
    Valid,
}

/// <summary>The four SmartClock modes, in the order the screen lists them.</summary>
public enum ClockMode
{
    /// <summary>Locked to GPS.</summary>
    Locked = 0,

    /// <summary>Recovering from holdover.</summary>
    Recovery,

    /// <summary>Holdover.</summary>
    Holdover,

    /// <summary>Power-up.</summary>
    PowerUp,
}

/// <summary>One row of the Tracking table.</summary>
/// <param name="Prn">The satellite.</param>
/// <param name="Elevation">Degrees above the horizon.</param>
/// <param name="Azimuth">Degrees from true north.</param>
/// <param name="CarrierToNoise">C/N in dB-Hz.</param>
/// <remarks>
/// Any of the three can be unknown, printed <c>--</c>, <c>---</c> and <c>--</c>: a satellite tracked
/// before the almanac places it (seen on a power-up on 2 Oct 2026).
/// </remarks>
public sealed record TrackedSatellite(int Prn, int? Elevation, int? Azimuth, int? CarrierToNoise);

/// <summary>One row of the Not Tracking table.</summary>
/// <param name="Prn">The satellite.</param>
/// <param name="Elevation">Degrees above the horizon.</param>
/// <param name="Azimuth">Degrees from true north.</param>
/// <param name="Attempting">Whether the row carries the <c>*</c> of a satellite the receiver is trying to track.</param>
public sealed record UntrackedSatellite(int Prn, int? Elevation, int? Azimuth, bool Attempting = false);

/// <summary>The position panel: the mode and the coordinates it shows.</summary>
public sealed record PositionPanel
{
    /// <summary>Null for <c>Hold</c>; otherwise the survey's percentage complete.</summary>
    public double? SurveyPercent { get; init; }

    /// <summary>
    /// Why a survey is stalled, printed on the line under <c>MODE</c>, e.g. <c>track &lt;4 sats</c>;
    /// null when it is not.
    /// </summary>
    public string? SurveySuspended { get; init; }

    /// <summary>
    /// Whether the coordinates are the initial estimate (<c>INIT LAT</c>) rather than a held or
    /// averaged position: what a receiver shows before its first fix.
    /// </summary>
    public bool Initial { get; init; }

    /// <summary>The latitude, in degrees, positive north.</summary>
    public double Latitude { get; init; } = 47 + (31 / 60.0) + (18.822 / 3600);

    /// <summary>The longitude, in degrees, positive east.</summary>
    public double Longitude { get; init; } = -(122 + (12 / 60.0) + (22.152 / 3600));

    /// <summary>The height, in metres, on <see cref="HeightDatum"/>.</summary>
    public double Height { get; init; } = 38.0;

    /// <summary>The datum the height is printed on, <c>MSL</c> on the bench unit.</summary>
    public string HeightDatum { get; init; } = "MSL";
}

/// <summary>The HEALTH MONITOR section.</summary>
/// <remarks>
/// Every capture shows all six <c>OK</c>. The failing form is the manual's (p. 3-18): <c>Err</c> on the
/// item, <c>Error</c> on the summary line. It has never been seen on a wire.
/// </remarks>
public sealed record HealthPanel
{
    /// <summary>The last self test passed.</summary>
    public bool SelfTest { get; init; } = true;

    /// <summary>The internal supplies are in tolerance.</summary>
    public bool InternalPower { get; init; } = true;

    /// <summary>The oven supply is in tolerance.</summary>
    public bool OvenPower { get; init; } = true;

    /// <summary>The oscillator output is present.</summary>
    public bool Ocxo { get; init; } = true;

    /// <summary>The control voltage is clear of full scale.</summary>
    public bool Efc { get; init; } = true;

    /// <summary>The GPS engine is talking and its 1 PPS is present.</summary>
    public bool GpsReceiver { get; init; } = true;

    /// <summary>True when every item is OK.</summary>
    public bool AllOk => SelfTest && InternalPower && OvenPower && Ocxo && Efc && GpsReceiver;
}
