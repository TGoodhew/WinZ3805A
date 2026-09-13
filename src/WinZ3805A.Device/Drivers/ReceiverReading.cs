namespace WinZ3805A.Device.Drivers;

/// <summary>
/// A reading the interface shows, which a receiver family may have no way of ever supplying (#435).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the read-side counterpart of the command catalog.</b> The catalog answers "what may I
/// send", and §9.11's rule for a command a receiver lacks — disabled and explained, never hidden —
/// has worked since #304. Nothing answered "what may I ever <i>know</i>", so a field the family
/// cannot carry rendered as an em dash, which §9.11 defines under <i>Partial / streaming</i> as a
/// field that has <b>not arrived yet</b>. Structural absence and a slow read were therefore drawn
/// identically, and the user could not tell "this receiver will never tell you" from "this has not
/// come in".
/// </para>
/// <para>
/// <b>The audit that produced this list found worse than dashes.</b> With a talker connected the
/// Time page said <i>"The receiver did not answer the leap-second queries"</i> about queries never
/// sent, Diagnostics reported <i>"The last status screen parsed completely"</i> for a receiver that
/// has no status screen, and both Diagnostics and Status Registers showed an <b>error icon</b> for
/// what is not a fault at all. Every one of those is a sentence that can only be replaced once
/// something knows the difference between "unread" and "unknowable".
/// </para>
/// <para>
/// <b>An entry here is a question a page asks, not a field of the status model.</b> Several model
/// fields answer to one entry — <see cref="OnePpsTimeInterval"/> covers the reading, its σ, the
/// trend, the Allan deviation and the drift fit, because a family that cannot measure the interval
/// cannot produce any of them — and that keeps the enum to what the interface actually annotates
/// rather than to every property that exists.
/// </para>
/// </remarks>
public enum ReceiverReading
{
    /// <summary>Time figure of merit (§10.4). A quality number for the receiver's own time.</summary>
    Tfom,

    /// <summary>Frequency figure of merit (§10.4). A quality number for its frequency.</summary>
    Ffom,

    /// <summary>
    /// The 1 PPS time interval against GPS (§10.7), and everything derived from it: σ, the trend,
    /// the Allan deviation and the oscillator drift fit.
    /// </summary>
    OnePpsTimeInterval,

    /// <summary>The oscillator's electronic frequency control, as a percentage, and its trend (§10.4).</summary>
    OscillatorControl,

    /// <summary>
    /// Holdover in every form (§10.8): whether the receiver is in it, for how long, the predicted
    /// and present uncertainty, and the threshold.
    /// </summary>
    /// <remarks>
    /// A receiver with no disciplined oscillator does not merely fail to report holdover — it has no
    /// oscillator to hold over, so "not in holdover" is a claim rather than a reading.
    /// </remarks>
    Holdover,

    /// <summary>The antenna cable delay the receiver currently has in use (§10.7).</summary>
    AntennaDelay,

    /// <summary>Whether the receiver's outputs are valid (§10.4).</summary>
    OutputValidity,

    /// <summary>The receiver's serial number and firmware revision (§10.4).</summary>
    /// <remarks>
    /// Model and manufacturer are not here: a driver always supplies those, even where it has
    /// invented them from what it overheard.
    /// </remarks>
    DeviceIdentity,

    /// <summary>The accumulated GPS − UTC offset and any announced leap second (§10.11).</summary>
    LeapSecond,

    /// <summary>The time-code output format the receiver is set to (§10.11).</summary>
    TimeCodeFormat,

    /// <summary>How long the receiver has been powered in total (§10.9).</summary>
    PowerOnHours,

    /// <summary>
    /// The internal GPS receiver's own model and firmware identity (§10.9).
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="DeviceIdentity"/>, which is the instrument's. A SmartClock is a
    /// disciplining chassis wrapped around somebody else's GPS engine, and the two revise on
    /// separate schedules — the bench Z3805A reports firmware <c>1.01.03-A</c> while the engine
    /// inside it reports a Furuno GT-80 running software version 005.
    /// </remarks>
    GpsEngineIdentity,

    /// <summary>The health monitor and its per-subsystem items (§10.4, §10.9).</summary>
    HealthMonitor,

    /// <summary>The SCPI status registers and their masks (§10.10).</summary>
    StatusRegisters,

    /// <summary>The receiver's own stored diagnostic log (§10.9).</summary>
    DiagnosticLog,

    /// <summary>The receiver's error queue (§10.9).</summary>
    ErrorQueue,

    /// <summary>
    /// A whole status screen parsed in one read, which is what §11.1's parse-health line reports on.
    /// </summary>
    /// <remarks>
    /// A broadcast talker has no status screen at all, so reporting that the last one "parsed
    /// completely" is not a good outcome — it is a statement about something that did not happen.
    /// </remarks>
    StatusScreen,

    /// <summary>The elevation mask the receiver is applying (§10.5).</summary>
    /// <remarks>
    /// Separate from the command that sets it: a family could report a mask it will not let anyone
    /// change, and §9.11 wants those annotated differently.
    /// </remarks>
    ElevationMask,

    /// <summary>Whether the position is surveyed, held or unknown, and any survey in progress (§10.6).</summary>
    PositionHold,

    // ---- The fix-quality readings NMEA carries and a status screen does not (#435) ---------------

    /// <summary>
    /// Dilution of precision — how favourably the satellites were placed (§10.6).
    /// </summary>
    /// <remarks>
    /// Geometry alone. A GNSS receiver computes it for every fix and NMEA's <c>GSA</c> broadcasts
    /// it; a SmartClock status screen has no such field, which is a reading of the captured screens
    /// rather than an assumption.
    /// </remarks>
    DilutionOfPrecision,

    /// <summary>
    /// The geoid's height above the WGS-84 ellipsoid, which makes an MSL height convertible (§10.6).
    /// </summary>
    /// <remarks>
    /// A SmartClock prints <c>HGT … (MSL)</c> and stops there, so the other datum is unreachable on
    /// that family however long you wait.
    /// </remarks>
    GeoidSeparation,

    /// <summary>
    /// How many satellites went into the fix, as distinct from how many are tracked (§10.6).
    /// </summary>
    /// <remarks>
    /// <b>Not the count on the main window.</b> A status screen's <c>Tracking:</c> and
    /// <c>Not Tracking:</c> are what the receiver can hear; this is what the solution kept, and only
    /// a family that states it separately can report it.
    /// </remarks>
    SatellitesUsed,

    // ---- What the fix's own error statistics say, once a receiver is asked for them (#516) -------

    /// <summary>
    /// The receiver's estimate of its position error, in metres (§10.6).
    /// </summary>
    /// <remarks>
    /// <b>Not <see cref="DilutionOfPrecision"/> in different units.</b> Dilution is geometry and says
    /// nothing about how noisy the ranging was; this is the error the receiver believes it actually
    /// has. NMEA carries it in <c>GST</c>; a SmartClock status screen has no field for it, which is
    /// a reading of the captured screens rather than an assumption.
    /// </remarks>
    PositionUncertainty,

    /// <summary>
    /// Whether a satellite in the solution is believed faulty — RAIM integrity (§10.6).
    /// </summary>
    /// <remarks>
    /// The nearest thing a talker has to the SmartClock's health monitor, and it is about the
    /// constellation rather than about the receiver's own hardware, so the two are not
    /// interchangeable. NMEA carries it in <c>GBS</c>.
    /// </remarks>
    FixIntegrity,
}
