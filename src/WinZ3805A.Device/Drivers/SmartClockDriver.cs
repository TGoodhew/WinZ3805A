using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Models;
using WinZ3805A.Device.Parsing;
using WinZ3805A.Device.Transport;

namespace WinZ3805A.Device.Drivers;

/// <summary>
/// The HP/Symmetricom SmartClock family — Z3805A, Z3801A, Z3816A, 58503A/B, 59551A (#122).
/// </summary>
/// <remarks>
/// <para>
/// <b>The first of the shipped drivers <c>docs/adding-a-receiver.md</c> points at — the worked
/// example is <c>docs/tutorial-nmea-driver.md</c>.</b> Every piece this one implements already existed and
/// was static; the driver is where "which receiver" stopped being implied and started being named.
/// Nothing here changes behaviour — the acceptance criterion for #122 is that the existing tests
/// pass unmodified, because a refactor whose tests had to be rewritten has not preserved anything.
/// </para>
/// <para>
/// It is one driver for the whole family rather than one per model. The models differ in hardware
/// rather than in dialect: they share the status screen, the command tree and the prompt, and where
/// they diverge it is over which optional subsystems exist, which is
/// <see cref="ModelProfile"/>'s job (#64). A Trimble Thunderbolt is a different <i>driver</i>; a
/// 59551A is the same driver with a different profile.
/// </para>
/// </remarks>
/// <param name="timeProvider">
/// Supplies "now" for the parser's capture stamp and §7.4's rollover comparison. Injected because
/// fixture tests pin the clock, and the rollover arithmetic is meaningless against a moving one.
/// </param>
public sealed class SmartClockDriver(TimeProvider timeProvider) : IReceiverDriver
{
    private readonly StatusScreenParser _parser = new(timeProvider);

    /// <inheritdoc />
    public string Family => "SmartClock";

    /// <inheritdoc />
    public IReadOnlyList<ScpiCommand> Commands => CommandCatalog.All;

    /// <inheritdoc />
    public PollCadence Cadence { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));

    /// <inheritdoc />
    public IReadOnlyList<SerialSettings> AutoDetectSequence => SerialSettings.AutoDetectSequence;

    /// <summary>
    /// The three fix-quality readings this family does not print (#435).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the first thing this driver has ever declined, and the interface warns that a
    /// wrong <see langword="false"/> is worse than a blank</b> — it tells a user their receiver can
    /// never report something, which stops them looking. So each of these is read off the captured
    /// status screens rather than assumed: the screen prints <c>Tracking:</c> and
    /// <c>Not Tracking:</c> counts, and <c>HGT +38.00 m (MSL)</c>, and nothing anywhere resembling a
    /// dilution figure or a geoid separation.
    /// </para>
    /// <para>
    /// <c>Tracking:</c> is deliberately not read as satellites <i>used</i>. It is what the receiver
    /// can hear; the fix keeps a subset, and a family that does not state the subset cannot report
    /// it however the two numbers are juggled.
    /// </para>
    /// <para>
    /// Everything else stays at the interface's default of <see langword="true"/>. Declining only
    /// what has been checked is the point: silence means "not thought about", which still shows an
    /// em dash, and that is the safe answer.
    /// </para>
    /// </remarks>
    public bool Reports(ReceiverReading reading) => reading switch
    {
        ReceiverReading.DilutionOfPrecision => false,
        ReceiverReading.GeoidSeparation => false,
        ReceiverReading.SatellitesUsed => false,

        // Neither appears on any captured screen, and both are per-fix GNSS statistics rather than
        // anything a disciplined oscillator publishes about itself (#516).
        ReceiverReading.PositionUncertainty => false,
        ReceiverReading.FixIntegrity => false,
        _ => true,
    };

    /// <summary>
    /// Recognises any model whose identity this family covers.
    /// </summary>
    /// <remarks>
    /// <b>Null is no longer claimed (#287).</b> It was, while this was the only driver — the probe
    /// had to run under <i>some</i> driver before an identity existed. The probe phase now belongs
    /// to no driver: <c>DeviceSessionService</c> reads the identity neutrally and only then asks
    /// each registered driver, falling back to the first-registered one when nothing claims the
    /// answer. A driver claiming null today would be claiming every receiver whose identity could
    /// not be read, which is exactly the over-claim the remarks on the interface warn against.
    /// </remarks>
    public bool Recognises(DeviceIdentity? identity) =>
        identity is not null && identity.Receiver != ReceiverModel.Unknown;

    /// <inheritdoc />
    public ScpiCommand? Find(string? mnemonic) => CommandCatalog.Find(mnemonic);

    /// <inheritdoc />
    /// <remarks>
    /// Delegates to the one place in the repository permitted to hold those patterns. The verdict
    /// crosses this boundary; the patterns do not.
    /// </remarks>
    public bool IsBlocked(string? header) =>
        !string.IsNullOrWhiteSpace(header) && CommandCatalog.IsBlocked(header);

    /// <inheritdoc />
    public TimeSpan TimeoutFor(string? mnemonic) =>
        string.IsNullOrWhiteSpace(mnemonic) ? TransactionTimeouts.Default : TransactionTimeouts.For(mnemonic);

    /// <inheritdoc />
    /// <remarks>
    /// §7.3's schedule, in §7.3's order. The refusable entry is <c>:SYNC:TINT?</c>, which has no
    /// answer while the receiver is unlocked (§7.3.1) — its index is derived rather than written as
    /// a literal, because an index that drifted from the list would suppress the wrong reading,
    /// silently, and only while the receiver was unlocked.
    /// </remarks>
    public PollPlan Plan { get; } = new(
        FastTierOrder,
        Array.IndexOf(FastTierOrder, ":SYNC:TINT?"),
        FullStatus: ":SYST:STAT?")
    {
        // §7.3's sweep asks all six, which is why the common currency has exactly these
        // fields: this family is the shape the others are measured against (#475).
        FastTierCarries = FastFields.All,
    };

    /// <inheritdoc />
    public ReceiverStatus Parse(string? response) => _parser.Parse(response);

    /// <inheritdoc />
    /// <remarks>
    /// The closed set of <c>:SYNC:STAT?</c> answers, per the 58503A/59551A guide, and §10.3's table
    /// read from the driver's side. It is also the sweep's acceptance test — see
    /// <see cref="InterpretSweep"/> — so there is one list rather than two that can drift.
    /// </remarks>
    public ReceiverMode InterpretSyncState(string? syncState) =>
        syncState?.Trim().ToUpperInvariant() switch
        {
            "LOCK" => ReceiverMode.Locked,
            "REC" => ReceiverMode.Recovering,
            "WAIT" => ReceiverMode.Waiting,
            "HOLD" => ReceiverMode.Holdover,
            "POW" => ReceiverMode.PowerUp,
            "OFF" => ReceiverMode.Off,
            _ => ReceiverMode.Disconnected,
        };

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The rejection rule is #209's: the sync state is the one field with a closed set of legal
    /// values — <c>LOCK</c>, <c>REC</c>, <c>WAIT</c>, <c>HOLD</c>, <c>POW</c>, <c>OFF</c> — so an
    /// answer outside it did not come from <c>:SYNC:STAT?</c>, and the whole sweep is the tail of
    /// somebody else's reply rather than a reading. The whole sweep, not the offending field: when
    /// the link misaligned on 24 Aug the same sweep carried an EFC of +2 %, inside the control range
    /// and indistinguishable from a real reading by magnitude. What identifies it is the company it
    /// keeps.
    /// </para>
    /// <para>
    /// The readings come back even when rejected, because the poller's state-change log records
    /// what was seen either way — a guard that drops readings while looking healthy is worse than
    /// the defect it prevents.
    /// </para>
    /// </remarks>
    public SweepInterpretation InterpretSweep(IReadOnlyList<string?> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);

        string? syncState = ScalarParsers.ParseKeyword(At(answers, 0));

        FastReadings readings = new(
            syncState,
            Tfom: ScalarParsers.ParseInteger(At(answers, 1)),
            Ffom: ScalarParsers.ParseInteger(At(answers, 2)),
            TimeIntervalNanoseconds: ScalarParsers.ParseSecondsAsNanoseconds(At(answers, 3)),
            EfcPercent: ScalarParsers.ParseDecimal(At(answers, 4)),
            SatellitesTracked: ScalarParsers.ParseInteger(At(answers, 5)));

        // One source for "is this a state?" and "which state is it?" (#304): a token the mapping
        // does not know is exactly a token the sweep must reject, and keeping two lists is how the
        // two answers drift apart.
        return InterpretSyncState(syncState) != ReceiverMode.Disconnected
            ? new SweepInterpretation(readings, Rejection: null)
            : new SweepInterpretation(
                readings,
                $"the sync state read \"{Summarise(syncState)}\", which is not a state this receiver reports");
    }

    /// <summary>§7.3's fast tier. <see cref="Plan"/> wraps it; the array exists so the refusable index can be derived from it.</summary>
    private static readonly string[] FastTierOrder =
    [
        ":SYNC:STAT?",
        ":SYNC:TFOM?",
        ":SYNC:FFOM?",
        ":SYNC:TINT?",
        ":DIAG:ROSC:EFC:REL?",
        ":GPS:SAT:TRAC:COUN?",
    ];

    /// <summary>The answer at <paramref name="index"/>, or null when the sweep was shorter.</summary>
    private static string? At(IReadOnlyList<string?> answers, int index) =>
        index < answers.Count ? answers[index] : null;

    /// <summary>A rejected sync state, short enough to log and long enough to recognise.</summary>
    private static string Summarise(string? syncState)
    {
        if (string.IsNullOrWhiteSpace(syncState))
        {
            return "(empty)";
        }

        string oneLine = syncState.ReplaceLineEndings(" ").Trim();

        return oneLine.Length <= 60 ? oneLine : oneLine[..60] + "…";
    }
}
