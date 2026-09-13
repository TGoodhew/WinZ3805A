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
/// on it. The time codes did not land mid-reply, but they do land <i>after the prompt</i>, which is
/// enough to matter: see <see cref="Prompt"/>. And the prompt is not the SmartClock's, which is what
/// made every transaction time out with the answer already read.
/// </para>
/// <para>
/// <b>Two of the three are now settled for this module, and the interleaving one is settled the
/// other way (#481).</b> Fifty <c>SYST:STAT?</c> reads on 12 Sep 2026, with a reply on the wire 38%
/// of the time, produced 25 codes where ~25 were due and <b>none</b> mid-reply against ~9 expected
/// by chance. Nothing was dropped and nothing collided, so the module <i>defers</i> a broadcast to
/// the end of a reply. Heather's claim names <i>Symmetricom</i> units, so it stands untested rather
/// than refuted, and the tolerance costs nothing and stays. Everything else below is still a
/// hypothesis.
/// </para>
/// <para>
/// <b>Vendor and variant are two dimensions and this driver keeps them apart (#418).</b> One driver
/// with a vendor discriminator, not two drivers: the command set is shared and only the response
/// shapes differ. The vendor is established from <c>DIAG:LOOP?</c>'s shape rather than from
/// <c>*IDN?</c>, so there is a real "connected, vendor not yet known" state — and it renders as
/// honest emptiness rather than as a guess.
/// </para>
/// <para>
/// <b>Trimble is measured; Symmetricom is read from a description, and the two halves of this
/// driver are not equally trustworthy.</b> Everything vendor-specific for Trimble has now been
/// checked against <c>TRIMBLE,57964-80,40896646,V2.0.1.6-01</c>: the loop reply's shape and its
/// leading <c>LINK0:</c> line, and the status bytes in the locked state — lock <c>0x45</c>, date
/// <c>0x80</c>, PPS <c>0x60</c>, antenna <c>0x04</c>, cross-checked against <c>LED:GPSL?</c>. The
/// GPS-to-UTC conversion is confirmed to about two seconds by comparing the receiver's own printed
/// time against the time code in the same reply. <b>No Symmetricom module has ever been on the
/// bench</b>, so its loop format, its lock codes, its temperature field and that field's unit are
/// all Heather's description and nothing more. §418's acceptance asked for this to be said out loud
/// rather than assumed, in the way the NMEA driver says it has never met a real talker.
/// </para>
/// <para>
/// <b>What is measured is only the locked state.</b> Every sitting so far has been a module that
/// was locked and settled throughout, so none of Heather's transition sequences — the power-up runs
/// through <c>41</c> and <c>4F</c>, the antenna-disconnect moves — has been watched happening. The
/// tables are right about one point on the path and untested along it.
/// </para>
/// <para>
/// <b>The variant comes from the prompt, because asking does not work (#513).</b> A UCCM-P prints
/// <c>UCCM-P &gt;</c> and a plain UCCM prints <c>UCCM &gt;</c>, so <see cref="NotePrompt"/> settles
/// it on every transaction without asking anything. §418 section 7 proposed probing the three
/// queries Heather calls UCCM-P-only instead; put to a real UCCM-P on 12 and 13 Sep 2026 it answered
/// <b>none</b> of them, two with <c>Undefined header</c> — the same reply a deliberately nonsensical
/// header gets — so a probe would have called that module a plain UCCM.
/// </para>
/// <para>
/// <b>The three UCCM-P-only queries are still never asked, and now there is a measurement saying
/// they should not be.</b> This firmware does not have them: <see cref="UccmCommands.UccmPOnly"/>
/// stays in the catalog because a different UCCM-P may, and because §8.1's allowlist governs what
/// <i>may</i> be sent rather than what is. Holdover duration and survey progress reach the UI from
/// the status screen, which is where this module actually reports them.
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
        // Both variants, because until 13 Sep 2026 only the first was here and a plain UCCM could
        // therefore not connect at all: its prompt would not have matched, every transaction would
        // have run to its timeout holding the answer, and auto-detect would have reported that no
        // receiver answered - exactly the failure #470 found for the UCCM-P itself.
        //
        // One is a prefix of the other, so PromptGrammar matches the LONGEST word rather than the
        // first; see its remarks. Heather has the same pair and relies on testing order instead.
        Words = ["UCCM-P", "UCCM"],

        // Not observed on this module, and a grammar that accepts a prompt the receiver never sends
        // would report an error status nobody can act on. The SmartClock's E-nnn form is measured;
        // this family's error reporting is not.
        AllowsErrorQueuePrompt = false,
    };

    /// <summary>
    /// The <c>C5</c> time code this family broadcasts, taken out of the stream before line splitting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This one is measured, unlike most of this driver.</b> Seven frames across three sittings
    /// against a Trimble UCCM-P — 10, 11 and 12 Sep 2026, captures under
    /// <c>tests/WinZ3805A.Tests/Uccm/Captures/</c> — every one 44 bytes, every one opening
    /// <c>0xC5</c> and closing <c>0xCA</c>, broadcast about every two seconds.
    /// </para>
    /// <para>
    /// <b>They trail a reply; they have never been seen inside one.</b> All seven arrived after the
    /// <c>UCCM-P &gt;</c> prompt, appended with no terminator. Heather's <c>uccm_time_line()</c>
    /// exists because the codes are said to arrive "in the middle of another message's response",
    /// and that has not been observed here — but the grammar is applied to the whole stream anyway,
    /// because the difference costs nothing and the hypothesis is not refuted by seven frames.
    /// </para>
    /// <para>
    /// <b>A trailing frame is what corrupts the *next* reply.</b> It sits in the buffer until the
    /// following read, which is why <c>*IDN?</c> came back with binary in front of the identity in
    /// roughly 3 sends out of 34 through the Advanced Console on 12 Sep 2026, once losing the
    /// identity altogether. Lifting it out here is what stops that.
    /// </para>
    /// </remarks>
    public BinaryFrameGrammar BinaryFrames { get; } = BinaryFrameGrammar.UccmTimeCode;

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
    /// <b><see cref="UccmCommands.Loop"/> is here to settle the vendor, not for its numbers (#418).</b>
    /// Its reply <i>shape</i> is the only structural signal of who made the module — seven
    /// positional floats from Trimble, labelled lines from Symmetricom — and the lock byte means
    /// different things to each, so a driver that never asks it is left reading state bytes by a
    /// hypothesis drawn from five of Heather's observations. It was absent from this plan until
    /// 13 Sep 2026, which made every vendor-specific path in this driver unreachable in the
    /// running application: <see cref="InterpretSweep"/> is the hook that feeds it back, and
    /// nothing was feeding it.
    /// </para>
    /// <para>
    /// <b>Since #512 it also carries three readings of its own</b> — the oscillator's frequency
    /// offset, its temperature correction and whether it is being disciplined. The offset is a
    /// different quantity from <see cref="FastReadings.EfcPercent"/>: that is the control voltage,
    /// what the loop is doing, and this is how far off frequency the oscillator is measured to be.
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
        [UccmCommands.LockLed, UccmCommands.TimeInterval, UccmCommands.EfcRelative, UccmCommands.Loop],
        RefusableIndex: null,
        FullStatus: UccmCommands.Status)
    {
        // TFOM, FFOM and the tracked count are on the status screen and nowhere else — this
        // family has no scalar query for any of them — so the sweep must not claim to answer
        // them. Claiming it wiped all three off the primary window ten times between screens
        // (#475).
        FastTierCarries = FastFields.SyncState
            | FastFields.TimeInterval
            | FastFields.OscillatorControl
            | FastFields.OscillatorOffset
            | FastFields.OscillatorTemperature
            | FastFields.DiscipliningState,

        // What this family can EVER report, which is the other question (#456). The default is the
        // original six, so the three #512 added have to be claimed explicitly or the UI would treat
        // them as readings a UCCM cannot produce and show nothing at all.
        //
        // The temperature is claimed even though a Trimble never sends one: the FAMILY reports it,
        // Symmetricom modules do, and "supplied by this family" is not "present in this reply".
        // A Trimble's is absent, which §11.1 renders as an em dash - the reading has not arrived -
        // rather than as a field that does not exist.
        Supplies = FastFields.All
            | FastFields.OscillatorOffset
            | FastFields.OscillatorTemperature
            | FastFields.DiscipliningState,
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
    /// What this family does not report, each entry with a sitting behind it (#435, #483).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every <c>false</c> here is a measurement, and the ones that are not are absent.</b> This
    /// was a single entry until 13 Sep 2026, under remarks saying the driver had never met a
    /// receiver — true when written, and the reason the rest was left alone. It has met one since:
    /// <c>TRIMBLE,57964-80,40896646,V2.0.1.6-01</c>, across five sittings whose captures are in
    /// <c>tests/WinZ3805A.Tests/Uccm/Captures/</c>. The four added below are the ones that never
    /// answered in any of them.
    /// </para>
    /// <para>
    /// <b>Why this matters more than a tidy switch.</b> The interface defaults every reading to
    /// <c>true</c>, which means "asked and not yet answered" — §9.11's em dash, a reading in flight.
    /// For something the receiver will never answer that promise never comes true, and the Status
    /// Registers page went further and drew a <b>red error</b>, reporting a working receiver as
    /// broken because it had not answered a question it has no way to answer.
    /// </para>
    /// <para>
    /// <b><see cref="ReceiverReading.Holdover"/> is deliberately not here.</b> Every field of the
    /// Holdover page reads as absent on this module, which looks like the same case and is not:
    /// <c>:ROSC:HOLD:DUR?</c> answers <c>Command error</c> rather than <c>Undefined header</c>, so
    /// the node exists and is refused for some other reason — state being the obvious candidate,
    /// since the unit has been locked throughout every sitting. The test that separates "unsupported"
    /// from "not valid now" is re-asking it <i>in holdover</i>, and until that is done, declaring it
    /// <c>false</c> would be the hypothesis-in-costume this method exists to keep out.
    /// </para>
    /// </remarks>
    public bool Reports(ReceiverReading reading) => reading switch
    {
        // A SmartClock node. :DIAG:IDEN:GPS? is not in UccmCommands, so this driver cannot ask the
        // question and may not let a card imply the value is merely unread (#304, #435).
        ReceiverReading.GpsEngineIdentity => false,

        // SCPI apparatus this family does not carry. Measured: no register field ever answered.
        ReceiverReading.StatusRegisters => false,

        // The leap offset is real on this family and arrives in the C5 time code (#481), but not
        // from any query — it answers none of the :PTIM:LEAP nodes. This reading is about the
        // queries, and those are the ones the Time page would otherwise wait on forever.
        ReceiverReading.LeapSecond => false,
        ReceiverReading.TimeCodeFormat => false,

        // No health-monitor apparatus. The Overview card said "No health data" indefinitely.
        ReceiverReading.HealthMonitor => false,

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

    /// <summary>
    /// The word this driver adds to the sync token when the time code says the receiver is coasting.
    /// </summary>
    /// <remarks>
    /// <b>The sync token is this driver's own vocabulary.</b> It produces it in
    /// <see cref="InterpretSweep"/> and reads it back in <see cref="InterpretSyncState"/>; nothing
    /// between the two interprets it, so adding a word costs no one else anything. It is a constant
    /// rather than a literal in two places because the two places must agree, and a typo in either
    /// would silently restore the defect it exists to fix.
    /// </remarks>
    internal const string HoldoverMarker = "HOLDOVER";

    /// <summary>The most recent time code the module broadcast, or null if none has arrived.</summary>
    /// <remarks>
    /// <b>Last one wins.</b> The codes arrive about every two seconds carrying a counter, so the
    /// later one is simply the truer one — the same rule <see cref="UccmStatusParser"/> applies when
    /// a single reply contains two.
    /// </remarks>
    private UccmTimeCode? _lastTimeCode;

    /// <inheritdoc />
    public void Observe(IReadOnlyList<byte[]> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);

        foreach (byte[] frame in frames)
        {
            // Null for anything that is not a whole, well-formed code. §11.1: a broadcast nobody can
            // read is no reading, never an error, and never a partial one - a time assembled from
            // half a counter is wrong rather than absent, and wrong is what a clock must not be.
            if (UccmTimeCode.TryParse(frame) is UccmTimeCode code)
            {
                _lastTimeCode = code;

                if (!_profile.VendorKnown && code.SuggestedVendor is var suggested and not UccmVendor.Unknown)
                {
                    _profile = _profile with { Vendor = suggested };
                }
            }
        }
    }

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

        return UccmStatusParser.Parse(response, timeProvider.GetUtcNow(), _profile, _lastTimeCode);
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
    /// Settles the variant from the prompt the receiver just printed (#513).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The receiver naming itself, which beats anything it can be asked.</b> A UCCM-P prints
    /// <c>UCCM-P &gt;</c> and a plain UCCM prints <c>UCCM &gt;</c>, on every transaction, before any
    /// question. Heather reads the same signal, testing <c>UCCM-P</c> before <c>UCCM</c>.
    /// </para>
    /// <para>
    /// <b>The obvious alternative was measured and does not work.</b> §418 section 7 proposed
    /// settling the variant by asking the three queries Heather calls UCCM-P-only and reading
    /// whether they were answered. Put to a real UCCM-P on 12 and 13 Sep 2026, it answered
    /// <b>none</b> of them: <c>:GPS:POS:SURV:STAT?</c> and <c>:GPS:POS:SURV:PROG?</c> both returned
    /// <c>Undefined header</c> — the same reply a deliberately nonsensical header gets, which is the
    /// control that makes it conclusive — and <c>:ROSC:HOLD:DUR?</c> returned <c>Command error</c>,
    /// a node that exists and is refused for some other reason. A probe would have called that
    /// module a plain UCCM.
    /// </para>
    /// <para>
    /// <b>Only the UCCM-P half is measured.</b> No plain UCCM has been on a bench here, so that its
    /// prompt reads <c>UCCM</c> is Heather's claim and not ours. It is the safe direction to be
    /// wrong in: a module whose prompt says neither leaves the variant
    /// <see cref="UccmVariant.Unknown"/>, which is what it was before this existed.
    /// </para>
    /// </remarks>
    public void NotePrompt(string? word)
    {
        UccmVariant variant = word switch
        {
            "UCCM-P" => UccmVariant.UccmP,
            "UCCM" => UccmVariant.Uccm,
            _ => UccmVariant.Unknown,
        };

        NoteVariant(variant);
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
        string? loop = At(answers, 3);

        // The vendor is settled from the loop reply's shape, which is why it is in the plan at all
        // (#418). Done before the rejection test below: an error on the discriminator throws the
        // sweep's readings away, but anything the loop already told us about who made this module
        // is still true and is worth keeping.
        UccmLoopReading? loopReading = loop is not null && !UccmReply.IsError(loop, UccmCommands.Loop)
            ? ReadLoop(loop)
            : null;

        // An error where the discriminator should be is somebody else's reply or a refusal, not a
        // reading. §11.1's rule about not inventing values, applied to a whole sweep.
        if (UccmReply.IsError(led, UccmCommands.LockLed))
        {
            return new SweepInterpretation(
                new FastReadings(null, null, null, null, null, null),
                $"The receiver answered '{UccmCommands.LockLed}' with an error rather than a lock state.");
        }

        string? lockState = UccmReply.FirstPayload(led, UccmCommands.LockLed);

        // The lamp says 1 - locked - all the way through holdover, so it cannot be the only thing
        // in the token. The overheard time code can tell the difference and the lamp cannot, so
        // where they disagree the frame wins and says so in the token itself. See InterpretSyncState.
        if (_lastTimeCode?.InHoldover is true)
        {
            lockState = string.IsNullOrWhiteSpace(lockState)
                ? HoldoverMarker
                : $"{lockState} {HoldoverMarker}";
        }

        FastReadings readings = new(
            SyncState: lockState,
            Tfom: null,
            Ffom: null,
            TimeIntervalNanoseconds: Seconds(tint, UccmCommands.TimeInterval) * 1.0E9,
            EfcPercent: Number(efc, UccmCommands.EfcRelative),
            SatellitesTracked: null)
        {
            // Null rather than absent-as-zero throughout (§11.1). A reading the sanity band threw
            // away is null here too: the band exists so that a value Heather measured as spurious
            // on Trimble never reaches the trend store, and this is the last place it could.
            OscillatorOffsetPpb = loopReading?.OscillatorOffsetPpb,
            OscillatorTemperature = loopReading?.TemperatureCorrection,
            Disciplining = loopReading?.Disciplining,
        };

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
    /// <b>So the lamp is no longer the only thing in the token.</b> The lamp answers <c>1</c>
    /// throughout genuine holdover — measured on 13 Sep 2026, through fourteen minutes with the
    /// antenna physically disconnected and nothing tracked — and <c>1</c> means locked, so the
    /// largest element on the primary window said "Locked to GPS" about a free-running oscillator.
    /// <see cref="InterpretSweep"/> now appends <see cref="HoldoverMarker"/> when the overheard time
    /// code says otherwise, and the holdover branch below is what reads it. That branch existed
    /// before and had never once fired, because nothing produced a token it could match.
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

        // BEFORE the lamp, and the order is load-bearing. A plain UCCM answers the lamp as free
        // text, so a marked token can read "NORMAL HOLDOVER" - and testing NORMAL first would
        // return Locked for a receiver the frame says is coasting, which is this whole defect
        // reintroduced through the back door. Holdover is the more specific claim, so it wins.
        if (token.Contains("HOLD", StringComparison.OrdinalIgnoreCase))
        {
            return ReceiverMode.Holdover;
        }

        if (token.Contains("NORMAL", StringComparison.OrdinalIgnoreCase) || token == "1")
        {
            return ReceiverMode.Locked;
        }

        if (token.Contains("INIT", StringComparison.OrdinalIgnoreCase) || token == "0")
        {
            return ReceiverMode.PowerUp;
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
