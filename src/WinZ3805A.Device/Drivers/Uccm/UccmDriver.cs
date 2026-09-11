using System.Globalization;

using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Models;
using WinZ3805A.Device.Transport;

namespace WinZ3805A.Device.Drivers.Uccm;

/// <summary>
/// The UCCM family — Symmetricom and Trimble telecom GPSDO modules speaking a SCPI dialect (#416).
/// </summary>
/// <remarks>
/// <para>
/// <b>THIS DRIVER HAS NEVER MET A RECEIVER.</b> It is written from Lady Heather's source
/// (heathgps.cpp, MIT licensed, © 2008-2016 Mark S. Sims) and carries the same caveat the NMEA
/// driver carries for the opposite reason (#310): every command, every timeout, every field meaning
/// and every state code here is a reading of somebody else's reverse engineering, not a measurement.
/// Heather's own comments hedge several of them with question marks. §11.1 asks for captured
/// fixtures and there are none. Treat everything below as a hypothesis with a source.
/// </para>
/// <para>
/// <b>Two protocol facts make this family unlike the SmartClock, and both need confirming first.</b>
/// The receiver echoes each command before answering it, and it emits unsolicited <c>C5</c> time
/// codes that can land in the middle of another reply. <see cref="UccmReply"/> holds both, with the
/// evidence. Our transport is line-and-prompt oriented, so how these interact with
/// <c>LineProtocol</c> is the first thing to check on real hardware.
/// </para>
/// <para>
/// <b>That check has now happened once, and it cost a connect (#470).</b> A Trimble UCCM-P was on
/// the bench on 10 Sep 2026, and three things came back. The module did <b>not</b> echo, in eight
/// sittings — so the echo claim is unsupported rather than confirmed, and
/// <c>LineProtocol</c>'s echo detection compares rather than assumes, which is why nothing depended
/// on it. The time codes did not land mid-reply, but they do land <i>between the reply and the
/// prompt</i>, which is enough to matter: see <see cref="Prompt"/>. And the prompt is not the
/// SmartClock's, which is what made every transaction time out with the answer already read.
/// Everything else below is still a hypothesis.
/// </para>
/// <para>
/// <b>Vendor and variant are two dimensions and this driver keeps them apart (#418).</b> One driver
/// with a vendor discriminator, not two drivers: the command set is shared and only the response
/// shapes differ. The vendor is established from <c>DIAG:LOOP?</c>'s shape rather than from
/// <c>*IDN?</c>, so there is a real "connected, vendor not yet known" state — and it renders as
/// honest emptiness rather than as a guess.
/// </para>
/// </remarks>
/// <param name="timeProvider">
/// Supplies the parse stamp. Injected because fixture tests pin the clock, and because the Device
/// library may not read the machine clock directly.
/// </param>
public sealed class UccmDriver(TimeProvider timeProvider) : IReceiverDriver
{
    /// <summary>What the driver currently believes it is talking to.</summary>
    /// <remarks>
    /// <b>Mutable session state, and the one piece of it this driver has.</b> The vendor cannot be
    /// known at connect time, so it is learned and remembered. It is deliberately not static: §12
    /// requires a session per device, and two UCCMs on two ports may be from two vendors.
    /// </remarks>
    private UccmProfile _profile = UccmProfile.Unknown;

    /// <inheritdoc />
    public string Family => "UCCM";

    /// <summary>What the driver has established about the receiver, for display and for tests.</summary>
    public UccmProfile Profile => _profile;

    /// <inheritdoc />
    public IReadOnlyList<ScpiCommand> Commands => UccmCommands.All;

    /// <summary>
    /// A second for the readings, ten for the status.
    /// </summary>
    /// <remarks>
    /// <b>Copied from the SmartClock's cadence and therefore a guess.</b> The interface's own
    /// remarks warn that timeouts and cadences are measurements rather than conventions. Heather
    /// alternates a time query with one parameter query and cycles through ten of them, which
    /// implies these modules are comfortable being asked something roughly every second, but the
    /// full status reply's wire time is unmeasured. Revisit with hardware.
    /// </remarks>
    public PollCadence Cadence { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));

    /// <summary>
    /// 9600-8-N-1 first, then 19200 and 57600.
    /// </summary>
    /// <remarks>
    /// A guess, and flagged as one. These modules are commonly reported at 9600 and at 57600
    /// depending on the host equipment they were pulled from; Heather sets no baud rate of its own
    /// for them. Auto-detect walks the union of every driver's sequence, so a wrong entry here
    /// costs time rather than correctness.
    /// </remarks>
    public IReadOnlyList<SerialSettings> AutoDetectSequence { get; } =
    [
        new() { BaudRate = 9600, DataBits = 8, Parity = System.IO.Ports.Parity.None, StopBits = System.IO.Ports.StopBits.One },
        new() { BaudRate = 19200, DataBits = 8, Parity = System.IO.Ports.Parity.None, StopBits = System.IO.Ports.StopBits.One },
        new() { BaudRate = 57600, DataBits = 8, Parity = System.IO.Ports.Parity.None, StopBits = System.IO.Ports.StopBits.One },
    ];

    /// <summary>
    /// <c>UCCM-P &gt;</c> — and this one is measured (#470).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The first fact in this driver that came from a receiver rather than from a source tree.</b>
    /// A Trimble UCCM-P, <c>TRIMBLE,57964-80,40896646,V2.0.1.6-01</c>, at 57600-8-N-1 on 10 Sep 2026,
    /// eight sittings with no variation. Until this member existed the prompt was the SmartClock's
    /// <c>scpi</c> and nothing else, so every transaction with one of these modules ran to its full
    /// timeout while holding the answer, and auto-detect reported that no receiver had answered.
    /// </para>
    /// <para>
    /// <b>There is no trailing space</b>, whatever §7.2 says about the invariant. The byte after
    /// <c>&gt;</c> is <c>C5</c>, the first byte of an unsolicited time code — which is also why
    /// <c>LineProtocol</c> had to stop requiring the prompt to be the whole of the tail.
    /// </para>
    /// <para>
    /// <b>Symmetricom's prompt is unmeasured and deliberately not guessed.</b> One word is claimed
    /// here because one word was seen. A Symmetricom module will fail to connect in exactly the way
    /// the Trimble did until somebody puts one on a bench and adds what it prints — and that failure
    /// will again say "no receiver answered", so read this remark before believing it.
    /// </para>
    /// </remarks>
    public PromptGrammar Prompt { get; } = new()
    {
        Words = ["UCCM-P"],

        // Not observed on this module, and a grammar that accepts a prompt the receiver never sends
        // would report an error status nobody can act on. The SmartClock's E-nnn form is measured;
        // this family's error reporting is not.
        AllowsErrorQueuePrompt = false,
    };

    /// <summary>
    /// The lock indicator first, then the readings; the status reply is the full tier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="UccmCommands.LockLed"/> leads because the plan's first entry must be the query
    /// whose answer discriminates a sweep from line noise, and it is the only one whose reply is a
    /// small closed set rather than an arbitrary number.
    /// </para>
    /// <para>
    /// <b>No refusable entry, which is a claim we cannot yet support.</b> §7.3.1's suppression
    /// exists because a query the receiver refuses in some states, re-asked every second, buries
    /// real faults in the error queue. It is entirely likely that <c>SYNC:TINT?</c> behaves that way
    /// here as it does on the SmartClock — but "likely" is not a measurement, and naming the wrong
    /// index suppresses a good reading silently. Left null until a receiver shows us which query it
    /// refuses and when.
    /// </para>
    /// </remarks>
    public PollPlan Plan { get; } = new(
        [UccmCommands.LockLed, UccmCommands.TimeInterval, UccmCommands.EfcRelative],
        RefusableIndex: null,
        FullStatus: UccmCommands.Status)
    {
        // TFOM, FFOM and the tracked count are on the status screen and nowhere else — this
        // family has no scalar query for any of them — so the sweep must not claim to answer
        // them. Claiming it wiped all three off the primary window ten times between screens
        // (#475).
        FastTierCarries = FastFields.SyncState | FastFields.TimeInterval | FastFields.OscillatorControl,
    };

    /// <summary>
    /// Claims a receiver whose identity names a UCCM module.
    /// </summary>
    /// <remarks>
    /// <b>Matches on the model rather than the manufacturer, deliberately.</b> Both Symmetricom and
    /// Trimble sell these and the manufacturer field therefore cannot identify the family — that is
    /// the whole of #418's first section. What is common is the product name. Nothing else is
    /// claimed: an identity this driver is unsure of is left to a driver that assumes less, which
    /// the interface's remarks make the always-safe answer.
    /// </remarks>
    public bool Recognises(DeviceIdentity? identity) =>
        identity?.Model is string model &&
        (model.Contains("UCCM", StringComparison.OrdinalIgnoreCase) ||
         MeasuredModels.Contains(model.Trim(), StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Models measured to be UCCMs whose <c>*IDN?</c> does not say so (#470).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The substring rule was the whole of this, and the first real module failed it.</b> The
    /// Trimble UCCM-P on the bench on 10 Sep 2026 answers
    /// <c>TRIMBLE,57964-80,40896646,V2.0.1.6-01</c> — a part number, with the word UCCM nowhere in
    /// it. Only the prompt says <c>UCCM-P</c>, and <c>Recognises</c> is not shown the prompt. So a
    /// module whose transaction now completes would have been handed to the first registered driver
    /// instead, and served as a SmartClock.
    /// </para>
    /// <para>
    /// <b>An exact model, never the manufacturer.</b> Trimble also makes the Thunderbolt, which is a
    /// different family this driver must keep its hands off — the selection tests use
    /// <c>TRIMBLE,THUNDERBOLT,…</c> as the identity nothing may claim. One measured part number
    /// claims one measured receiver and nothing else. Other UCCM-P part numbers exist and are not
    /// here, because they have not been seen; each one costs a line and a sitting.
    /// </para>
    /// </remarks>
    private static readonly string[] MeasuredModels = ["57964-80"];

    /// <inheritdoc />
    public ScpiCommand? Find(string? mnemonic) => UccmCommands.Find(mnemonic);

    /// <summary>
    /// The one reading this driver knows it cannot supply.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not a general audit of what a UCCM reports.</b> The interface defaults every reading to
    /// <c>true</c> and this driver has never met a receiver, so a full switch here would be twenty
    /// hypotheses wearing the costume of a measurement — exactly what the class remarks forbid.
    /// This is the narrow case where the answer is known without hardware: <c>:DIAG:IDEN:GPS?</c>
    /// is a SmartClock node, it is not in <see cref="UccmCommands"/>, and a driver that cannot ask
    /// the question may not let a card imply the value is merely unread (#304, #435).
    /// </para>
    /// <para>
    /// If a UCCM turns out to answer some equivalent, this becomes a real entry with a capture
    /// behind it. Until then <c>false</c> is the honest answer and the only one available.
    /// </para>
    /// </remarks>
    public bool Reports(ReceiverReading reading) => reading switch
    {
        ReceiverReading.GpsEngineIdentity => false,
        _ => true,
    };

    /// <summary>
    /// Nothing is excluded, because nothing here can do harm: the catalog holds queries only.
    /// </summary>
    /// <remarks>
    /// <b>This is not inherited from the SmartClock family and must never be.</b> The interface is
    /// explicit that exclusions are per-device and that borrowing another family's is not a
    /// conservative default — a command harmless on one receiver may be destructive on another, and
    /// the names need not even match. The honest answer for a read-only catalog is that there is
    /// nothing to exclude. <b>The moment a write is added to <see cref="UccmCommands"/>, this
    /// method needs a real §8.4 decision</b>, taken against that receiver rather than by analogy.
    /// </remarks>
    public bool IsBlocked(string? header) => false;

    /// <summary>
    /// Five seconds for the status reply, two for everything else.
    /// </summary>
    /// <remarks>
    /// <b>Guesses, and the interface warns specifically against exactly this.</b> The Z3805A's
    /// self-test reached 24 s against a 30 s class, so a figure carried over from another receiver
    /// may be wastefully long or short enough to fail healthy hardware. These are deliberately
    /// generous rather than tuned: a slow timeout costs a late reading, a short one costs a false
    /// fault. Measure them when a unit is available.
    /// </remarks>
    public TimeSpan TimeoutFor(string? mnemonic) =>
        string.Equals(mnemonic?.Trim(), UccmCommands.Status, StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromSeconds(5)
            : TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public ReceiverStatus Parse(string? response)
    {
        // A status reply can carry the loop header on some firmware, and the vendor is worth
        // taking from anywhere it can be had. Suggestion only: DIAG:LOOP? overrides it.
        if (!_profile.VendorKnown &&
            UccmReply.TimeCodes(response) is { Count: > 0 } codes &&
            codes[^1].SuggestedVendor is var suggested and not UccmVendor.Unknown)
        {
            _profile = _profile with { Vendor = suggested };
        }

        return UccmStatusParser.Parse(response, timeProvider.GetUtcNow(), _profile);
    }

    /// <summary>
    /// Reads a loop reply and settles the vendor from it, which is the authoritative signal (#418).
    /// </summary>
    /// <remarks>
    /// Structural rather than inferential: the two vendors' replies are different shapes, where
    /// <see cref="UccmTimeCode.SuggestedVendor"/> is a guess from five recorded observations. So
    /// this overrides whatever the status bytes suggested.
    /// </remarks>
    public UccmLoopReading ReadLoop(string? response)
    {
        UccmLoopReading reading = UccmLoopReading.Parse(response);
        if (reading.Vendor != UccmVendor.Unknown)
        {
            _profile = _profile with { Vendor = reading.Vendor };
        }

        return reading;
    }

    /// <summary>
    /// Records which variant the receiver is, once something has shown it.
    /// </summary>
    /// <remarks>
    /// Separate from the vendor because the two are orthogonal (#418). The signal is that a UCCM-P
    /// answers <see cref="UccmCommands.UccmPOnly"/> where a plain UCCM returns an undefined-header
    /// error, so the poller can settle it by asking once and reading the reply's kind.
    /// </remarks>
    public void NoteVariant(UccmVariant variant)
    {
        if (variant != UccmVariant.Unknown)
        {
            _profile = _profile with { Variant = variant };
        }
    }

    /// <inheritdoc />
    public SweepInterpretation InterpretSweep(IReadOnlyList<string?> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);

        string? led = At(answers, 0);
        string? tint = At(answers, 1);
        string? efc = At(answers, 2);

        // An error where the discriminator should be is somebody else's reply or a refusal, not a
        // reading. §11.1's rule about not inventing values, applied to a whole sweep.
        if (UccmReply.IsError(led, UccmCommands.LockLed))
        {
            return new SweepInterpretation(
                new FastReadings(null, null, null, null, null, null),
                $"The receiver answered '{UccmCommands.LockLed}' with an error rather than a lock state.");
        }

        string? lockState = UccmReply.FirstPayload(led, UccmCommands.LockLed);

        FastReadings readings = new(
            SyncState: lockState,
            Tfom: null,
            Ffom: null,
            TimeIntervalNanoseconds: Seconds(tint, UccmCommands.TimeInterval) * 1.0E9,
            EfcPercent: Number(efc, UccmCommands.EfcRelative),
            SatellitesTracked: null);

        return new SweepInterpretation(readings, Rejection: null);
    }

    /// <summary>
    /// Maps the lock indicator onto one of the application's modes (#304).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The lamp is a coarse signal and this mapping says so.</b> Heather reads
    /// <c>LED:GPSL?</c> as <c>0</c> initializing and <c>1</c> normal on a UCCM-P, and as free text
    /// on a plain UCCM. Two states is all it carries, so only two modes are reachable from it — the
    /// finer distinction between settling, holdover and locked lives in the status reply's lock
    /// byte, which <see cref="UccmStatusParser"/> reads.
    /// </para>
    /// <para>
    /// Anything unrecognised is <see cref="ReceiverMode.Disconnected"/> and never a guess, on the
    /// interface's own reasoning: showing "Locked to GPS" on a maybe is the worst available default.
    /// </para>
    /// </remarks>
    public ReceiverMode InterpretSyncState(string? syncState)
    {
        string token = syncState?.Trim() ?? string.Empty;
        if (token.Length == 0)
        {
            return ReceiverMode.Disconnected;
        }

        if (token.Contains("NORMAL", StringComparison.OrdinalIgnoreCase) || token == "1")
        {
            return ReceiverMode.Locked;
        }

        if (token.Contains("INIT", StringComparison.OrdinalIgnoreCase) || token == "0")
        {
            return ReceiverMode.PowerUp;
        }

        if (token.Contains("HOLD", StringComparison.OrdinalIgnoreCase))
        {
            return ReceiverMode.Holdover;
        }

        return ReceiverMode.Disconnected;
    }

    private static string? At(IReadOnlyList<string?> answers, int index) =>
        index < answers.Count ? answers[index] : null;

    private static double? Number(string? response, string sent)
    {
        string? payload = UccmReply.FirstPayload(response, sent);
        if (string.IsNullOrEmpty(payload))
        {
            return null;
        }

        foreach (string part in payload.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
                double.IsFinite(value))
            {
                return value;
            }
        }

        return null;
    }

    private static double? Seconds(string? response, string sent) => Number(response, sent);
}
