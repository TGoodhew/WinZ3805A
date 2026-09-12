using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Models;
using WinZ3805A.Device.Transport;

namespace WinZ3805A.Device.Drivers;

/// <summary>
/// How often a receiver is polled, which is a property of the device rather than of the app.
/// </summary>
/// <param name="Fast">
/// The scalar sweep — a handful of short queries. §7.3's cadence for the readings that move.
/// </param>
/// <param name="Full">
/// The whole status screen. Far more expensive: the Z3805A's takes 3521 ms of wire time measured,
/// which is why it is not simply the fast interval with more in it.
/// </param>
public readonly record struct PollCadence(TimeSpan Fast, TimeSpan Full);

/// <summary>
/// What each poll sweep sends, which is a property of the device rather than of the app (#287).
/// </summary>
/// <param name="FastTier">
/// The scalar queries of the fast sweep, in the order their answers are handed to
/// <see cref="IReceiverDriver.InterpretSweep"/>. <b>The first entry must be the query whose answer
/// discriminates a sweep from line noise</b> — the sync state, for the SmartClock family — because
/// the poller reads it on its own, ahead of the rest, and keys its refusal suppression on it.
/// Every entry must be in <see cref="IReceiverDriver.Commands"/>.
/// </param>
/// <param name="RefusableIndex">
/// The index of the one query the receiver may legitimately <i>refuse</i> in some of its states, or
/// <see langword="null"/> when there is none. §7.3.1's lesson: a refused query re-asked every second
/// overflows the error queue and buries real faults, so the poller stops asking it until the
/// discriminator's answer changes. One index rather than a set, deliberately — no known receiver
/// needs more, and a wider contract would be a guess with nothing to check it against.
/// </param>
/// <param name="FullStatus">
/// The query whose answer <see cref="IReceiverDriver.Parse"/> reads — the full status screen, for
/// receivers that have one. Must be in <see cref="IReceiverDriver.Commands"/>. A
/// <see cref="LinkStyle.Broadcast"/> driver may name <see cref="PollPlan.WholeCycle"/> here, in
/// which case <see cref="IReceiverDriver.Parse"/> receives every line of the last complete cycle.
/// </param>
public sealed record PollPlan(
    IReadOnlyList<string> FastTier,
    int? RefusableIndex,
    string FullStatus)
{
    /// <summary>
    /// The key that answers with the whole of the last complete broadcast cycle rather than one
    /// kind of line (#310).
    /// </summary>
    /// <remarks>
    /// A talker's status is spread across its sentences — position in one, satellites in several,
    /// time in another — and <see cref="IReceiverDriver.Parse"/> takes one response. Naming this as
    /// the plan's full-status query hands it the cycle entire. It has to be in the driver's catalog
    /// like any other plan entry, so the session's point-of-send allowlist check and the console
    /// picker see it as the read it is.
    /// </remarks>
    public const string WholeCycle = "*";

    /// <summary>
    /// Which of the common currency's fields <see cref="FastTier"/> actually answers (#475).
    /// </summary>
    /// <remarks>
    /// <b>Required, because a default would be a guess about somebody else's receiver.</b> The
    /// obvious default is <see cref="FastFields.All"/> — true of the SmartClock, whose sweep asks
    /// all six — and it is exactly the assumption that cost the UCCM driver three readings. A new
    /// driver must answer this for its own family; there is no answer that is safe to assume on
    /// its behalf. Fields left out here are the full screen's to supply.
    /// </remarks>
    public required FastFields FastTierCarries { get; init; }

    /// <summary>
    /// Which of the common currency's fields this family can <b>ever</b> report, from any tier (#456).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not the same question as <see cref="FastTierCarries"/>, and the difference is the whole
    /// point.</b> That one says which tier answers a field; this says whether the receiver has the
    /// thing at all. A UCCM-P reports TFOM on its screen rather than its sweep — carried, just not
    /// there. An NMEA talker has no disciplined oscillator, so TFOM, FFOM and the 1 PPS interval are
    /// not late, not missing and not on another tier: they do not exist.
    /// </para>
    /// <para>
    /// <b>What it is for.</b> §9.11's em dash means "this has not arrived yet" — a statement about a
    /// reading in flight. For a field the family cannot produce that is never true and never becomes
    /// true, so the primary window showed a talker three dashes that would never fill, on a surface
    /// §9.1 designs to be left up for weeks (#456). This is the read-side counterpart of the command
    /// catalog: the catalog answers "what may I send", and nothing answered "what may I ever know"
    /// (#435).
    /// </para>
    /// <para>
    /// <b>Declared, never inferred from nulls.</b> Inferring would mean a temporarily silent
    /// SmartClock losing its readouts, which is #475's lesson in a new place: a null cannot
    /// distinguish "not answered yet" from "never will be".
    /// </para>
    /// <para>
    /// Defaults to <see cref="FastFields.All"/>, so a driver that says nothing claims everything and
    /// behaves exactly as it did before. That is the safe default here — the failure mode is a dash
    /// that could have been hidden, not a reading that vanishes.
    /// </para>
    /// </remarks>
    public FastFields Supplies { get; init; } = FastFields.All;
}

/// <summary>
/// Which of the common currency's fields a driver's fast tier can answer (#475).
/// </summary>
/// <remarks>
/// <para>
/// <b>A null in <see cref="FastReadings"/> means "asked, and the receiver did not answer" — it
/// cannot also mean "this family never asks here", and the difference decides whether the display
/// blanks.</b> The store overwrites every fast-tier field on every sweep, so that a reading the
/// receiver has stopped giving goes to an em dash rather than standing as a fabrication. That is
/// right for a field the tier asks about. For a field carried only by the full screen it wipes a
/// good value ten times between reads, and the reading becomes unreachable — present in the state
/// and never on screen, which is what a locked UCCM-P did with its TFOM, FFOM and satellite count
/// (#475).
/// </para>
/// <para>
/// <b>Declared rather than inferred.</b> Nothing reads intent out of the mnemonics in
/// <see cref="PollPlan.FastTier"/>: a query's spelling is the driver's business, and guessing
/// what it answers from how it is spelled is how one family's shape became every family's. A
/// driver says what its sweep carries, and is wrong in a way review can see.
/// </para>
/// </remarks>
[Flags]
public enum FastFields
{
    /// <summary>The sweep answers none of these — a driver whose readings all come from the screen.</summary>
    None = 0,

    /// <summary>The discriminator's state token.</summary>
    SyncState = 1 << 0,

    /// <summary>Time Figure of Merit.</summary>
    Tfom = 1 << 1,

    /// <summary>Frequency Figure of Merit.</summary>
    Ffom = 1 << 2,

    /// <summary>The 1 PPS offset against GPS.</summary>
    TimeInterval = 1 << 3,

    /// <summary>Oscillator control, as a percentage of full scale.</summary>
    OscillatorControl = 1 << 4,

    /// <summary>How many satellites are being tracked.</summary>
    SatellitesTracked = 1 << 5,

    /// <summary>
    /// The oscillator's fractional frequency offset, in parts per billion (#512).
    /// </summary>
    /// <remarks>
    /// <b>Not the same quantity as <see cref="OscillatorControl"/>, and the difference matters.</b>
    /// That one is the control voltage — what the loop is <i>doing</i> to the oscillator — while this
    /// is how far off frequency the oscillator is <i>measured</i> to be. A family can report either,
    /// both or neither.
    /// </remarks>
    OscillatorOffset = 1 << 6,

    /// <summary>The oscillator's temperature correction (#512).</summary>
    /// <remarks>
    /// Reported by Symmetricom UCCMs and by nothing else met so far. §11.1's rule is what makes this
    /// worth a flag of its own: a family that cannot report it must show nothing, not a zero.
    /// </remarks>
    OscillatorTemperature = 1 << 7,

    /// <summary>Whether the oscillator is being disciplined (#512).</summary>
    DiscipliningState = 1 << 8,

    /// <summary>
    /// The original six — the SmartClock's sweep, which is the shape the common currency was taken
    /// from. <b>Deliberately not widened by #512</b>: a family that answered all six before does not
    /// start answering three more because the enum grew, and this is what the existing drivers
    /// declare.
    /// </summary>
    All = SyncState | Tfom | Ffom | TimeInterval | OscillatorControl | SatellitesTracked,
}

/// <summary>
/// One fast sweep's answers, read into the common currency's fields (#287).
/// </summary>
/// <remarks>
/// The fields mirror what <c>ReceiverStateStore.UpdateFast</c> takes, and they are HP's concepts —
/// TFOM, FFOM — because the common currency is SmartClock-shaped and acknowledged as such (see
/// #287's item 4). A driver whose receiver has no equivalent of a field leaves it
/// <see langword="null"/>, exactly as <see cref="ReceiverStatus"/> requires of the full parse; it
/// never invents a value to fill a shape.
/// </remarks>
/// <param name="SyncState">The discriminator's answer as a bare token, or null.</param>
/// <param name="Tfom">Time Figure of Merit, 0 best to 9 worst.</param>
/// <param name="Ffom">Frequency Figure of Merit, 0 best to 3 worst.</param>
/// <param name="TimeIntervalNanoseconds">The 1 PPS offset against GPS, in nanoseconds.</param>
/// <param name="EfcPercent">Oscillator control, as a percentage of full scale.</param>
/// <param name="SatellitesTracked">How many satellites are being tracked.</param>
public sealed record FastReadings(
    string? SyncState,
    int? Tfom,
    int? Ffom,
    double? TimeIntervalNanoseconds,
    double? EfcPercent,
    int? SatellitesTracked)
{
    /// <summary>
    /// The oscillator's fractional frequency offset in parts per billion, or null (#512).
    /// </summary>
    /// <remarks>
    /// Added as an init-only property rather than a seventh positional parameter so that the six
    /// that were the common currency stay the shape every driver already writes, and a family that
    /// has nothing to say here says nothing by omission.
    /// </remarks>
    public double? OscillatorOffsetPpb { get; init; }

    /// <summary>The oscillator's temperature correction, or null (#512).</summary>
    public double? OscillatorTemperature { get; init; }

    /// <summary>
    /// Whether the oscillator is being disciplined, or null when it cannot be told (#512).
    /// </summary>
    /// <remarks>
    /// <b>Three states, and the third is not a formality.</b> On the one UCCM measured this is null
    /// far more often than it is true: the field it is inferred from reads zero on a locked,
    /// disciplined module, so zero was measured to mean "cannot tell" rather than "no" (#418).
    /// Whatever renders this must have somewhere to put "unknown" that is not "off".
    /// </remarks>
    public bool? Disciplining { get; init; }
}

/// <summary>
/// What one fast sweep's answers turned out to be (#287).
/// </summary>
/// <remarks>
/// <see cref="Readings"/> is always present — a rejected sweep still carries what was read, because
/// the poller's state-change log records what it saw whether or not it stores it. A non-null
/// <see cref="Rejection"/> means the sweep must not reach the store or the trend: the answers are
/// somebody else's reply, not a reading.
/// </remarks>
/// <param name="Readings">What the answers said, field by field, absent where unreadable.</param>
/// <param name="Rejection">
/// Why this sweep cannot be a reading — a sentence fit for a log, naming what was seen — or
/// <see langword="null"/> when nothing rules it out. A guard that drops readings silently is worse
/// than no guard (#209), so the sentence is part of the contract, not a courtesy.
/// </param>
public sealed record SweepInterpretation(FastReadings Readings, string? Rejection);

/// <summary>
/// Everything this application needs to know about one family of receiver (#122).
/// </summary>
/// <remarks>
/// <para>
/// <b>The seam is the device, not the transport.</b> <see cref="ITransport"/> already abstracts the
/// wire; this abstracts what is said over it and how the answers are read. A driver owns the command
/// vocabulary, the safety exclusions, the timeouts, the poll cadence and the parse — the five things
/// that were previously static and silently meant "Z3805A".
/// </para>
/// <para>
/// <b>Adding one is documented in <c>docs/adding-a-receiver.md</c>.</b> That walkthrough
/// is the intended entry point; this interface is the contract it describes.
/// </para>
/// <para>
/// <b><see cref="ReceiverStatus"/> is the common currency, and a driver that cannot fill a field
/// leaves it null.</b> §11.1 already requires exactly that of the parser and the UI already renders
/// null as <c>—</c>, so a receiver with no equivalent of TFOM is a receiver whose TFOM reads as
/// absent rather than as zero. Do not invent a value to fill a shape.
/// </para>
/// </remarks>
public interface IReceiverDriver
{
    /// <summary>A short name for the family, for logs and diagnostics — e.g. <c>SmartClock</c>.</summary>
    string Family { get; }

    /// <summary>
    /// Whether this driver handles the receiver that returned <paramref name="identity"/>.
    /// </summary>
    /// <remarks>
    /// Answering <see langword="false"/> for an identity you are unsure of is always safe: the
    /// caller falls back to a driver that assumes less. Claiming an unfamiliar receiver is not,
    /// because every timeout and command below then applies to hardware they were not measured on.
    /// </remarks>
    bool Recognises(DeviceIdentity? identity);

    /// <summary>Every command this receiver may be sent, as an allowlist (§8.1).</summary>
    /// <remarks>
    /// An allowlist, never a denylist. A command absent here cannot be sent, which is the property
    /// §8.4 depends on — see <see cref="IsBlocked"/> for the separate, stronger rule.
    /// </remarks>
    IReadOnlyList<ScpiCommand> Commands { get; }

    /// <summary>Finds a command by mnemonic, or null when this receiver has no such command.</summary>
    ScpiCommand? Find(string? mnemonic);

    /// <summary>
    /// Whether a typed header is one of §8.4's exclusions for this receiver.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This returns a verdict and never the patterns.</b> §8.4 requires that excluded commands do
    /// not exist as data a view can enumerate, so a driver must expose a predicate and nothing that
    /// could be bound to, logged wholesale, or iterated. A driver returning a list here would
    /// re-introduce exactly what the rule forbids.
    /// </para>
    /// <para>
    /// <b>Exclusions are per-device and a wrong answer here is a safety bug rather than a missing
    /// feature.</b> A new driver must decide its own; inheriting another family's is not a
    /// conservative default, because a command harmless on one receiver may be destructive on
    /// another and the names need not even match.
    /// </para>
    /// </remarks>
    bool IsBlocked(string? header);

    /// <summary>How long to wait for a given command, per §7.2's classes.</summary>
    /// <remarks>
    /// Per-device by nature. These are measurements, not conventions: the Z3805A's GPS self-test
    /// reached 24.0 s against a 30 s class, so a figure copied from another receiver may be either
    /// wastefully long or short enough to fail healthy hardware.
    /// </remarks>
    TimeSpan TimeoutFor(string? mnemonic);

    /// <summary>How often to poll, fast and full.</summary>
    PollCadence Cadence { get; }

    /// <summary>What each sweep sends — §7.3's schedule, as this receiver's own (#287).</summary>
    /// <remarks>
    /// Must be stable: the poller reads it every sweep and keys its refusal suppression on the
    /// plan's shape. Every mnemonic it names must resolve through <see cref="Find"/>.
    /// </remarks>
    PollPlan Plan { get; }

    /// <summary>The serial configurations auto-detect walks, most-likely-first.</summary>
    IReadOnlyList<SerialSettings> AutoDetectSequence { get; }

    /// <summary>
    /// Turns a status response into a <see cref="ReceiverStatus"/>.
    /// </summary>
    /// <remarks>
    /// <b>It must never throw</b> (§11.1). An unreadable field becomes null and the reason goes into
    /// <see cref="ReceiverStatus.ParseWarnings"/>; an unrecognisable response yields a status whose
    /// fields are all absent and whose warnings say so. A driver that throws takes down the poll
    /// loop, which is the one failure the parser contract exists to prevent.
    /// </remarks>
    ReceiverStatus Parse(string? response);

    /// <summary>
    /// Reads one fast sweep's answers — <see cref="PollPlan.FastTier"/>'s, in its order — into the
    /// common currency, or rejects the sweep with a reason (#287).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It must never throw</b>, on the same §11.1 rule as <see cref="Parse"/> — and that includes
    /// an <paramref name="answers"/> list of any length, null entries throughout, and answers that
    /// are another command's reply. An unreadable field becomes null; a sweep that cannot be a
    /// reading at all comes back with <see cref="SweepInterpretation.Rejection"/> saying why.
    /// </para>
    /// <para>
    /// <b>Rejection is the driver's call because only the driver knows its own dialect.</b> The
    /// poller separately bounds-checks what is accepted here against the common currency's
    /// documented ranges, so this method owns "is this mine?" and the app owns "is this possible?".
    /// </para>
    /// </remarks>
    SweepInterpretation InterpretSweep(IReadOnlyList<string?> answers);

    /// <summary>
    /// Which of the application's modes this family's sync token means (#304).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The token is the driver's word; the mode is the application's.</b> §10.3 tabulates six
    /// modes against the SmartClock's <c>:SYNC:STAT?</c> answers, and until this existed that table
    /// lived app-side in <c>Controls/ReceiverMode.cs</c> — so a family whose receiver says anything
    /// else rendered as <see cref="ReceiverMode.Disconnected"/> on the medallion, the tray icon and
    /// the taskbar badge while its sweep was being stored and trended perfectly well. Asking the
    /// driver puts the one piece of receiver-specific knowledge in the interpretation where §12
    /// says every receiver-specific fact belongs.
    /// </para>
    /// <para>
    /// <b>The modes are a closed set and this method does not widen it.</b> A family whose states do
    /// not map cleanly picks the nearest honest member and documents the choice — the NMEA driver
    /// calls a fix <see cref="ReceiverMode.Locked"/> and no fix <see cref="ReceiverMode.PowerUp"/>,
    /// because a GPS receiver with a fix is locked to GPS in the only sense it has. Adding a seventh
    /// mode means a severity, a glyph and a label, which is §9's decision.
    /// </para>
    /// <para>
    /// <b>Unrecognised means <see cref="ReceiverMode.Disconnected"/>, never a guess</b>, on §11.1's
    /// reasoning: a mode the driver cannot name is one it cannot describe honestly, and showing
    /// "Locked to GPS" on a maybe is the worst available default. It must never throw.
    /// </para>
    /// <para>
    /// It answers about a bare token and not about the live link, so it is safe over a stored
    /// reading: the trend charts colour history by calling it on rows read hours ago.
    /// </para>
    /// </remarks>
    ReceiverMode InterpretSyncState(string? syncState);

    // ---- Added by #310, when the second family turned out not to speak when spoken to ----------
    //
    // The three members below have defaults, so a query/response driver written before them is
    // still complete: the SmartClock and the test project's fictional family implement none of
    // them. A broadcast driver implements all three.

    /// <summary>How this family's link carries answers (#310). Query/response unless the driver says otherwise.</summary>
    LinkStyle Link => LinkStyle.QueryResponse;

    /// <summary>
    /// The receiver spoke before it was asked anything: is it one of yours, and which one? (#310)
    /// </summary>
    /// <remarks>
    /// <para>
    /// The connect sequence listens before it probes (§7.2's synchronise step), and hands every
    /// driver what it heard. A talker announces itself by talking, so this is where a broadcast
    /// family is recognised — <c>*IDN?</c> is never sent to a receiver a driver has claimed here.
    /// Return an identity to claim the receiver, <see langword="null"/> to pass. The same caution
    /// as <see cref="Recognises"/> applies: claim only what your figures were measured against.
    /// </para>
    /// <para>
    /// <b>Never throw</b>; the session guards it as it guards <see cref="Recognises"/>, but a
    /// driver that throws here is a driver that failed to connect for no reason it logged.
    /// </para>
    /// </remarks>
    DeviceIdentity? Overhear(IReadOnlyList<string> lines) => null;

    /// <summary>
    /// The receiver just printed its prompt, and it was this word (#513).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>For a family whose prompt names the model rather than the protocol.</b> The SmartClock
    /// says <c>scpi</c> whatever it is, and learns nothing from this. A UCCM says <c>UCCM-P</c> or
    /// <c>UCCM</c>, which is the difference between two variants that answer different sets of
    /// queries — so for that family the prompt is a free measurement on every transaction, and it
    /// arrives before any question has been asked.
    /// </para>
    /// <para>
    /// <b>Worth preferring to a probe, because the probe does not work.</b> The three queries Lady
    /// Heather describes as UCCM-P-only were put to a real UCCM-P on 12 and 13 Sep 2026 and it
    /// answered none of them — two with <c>Undefined header</c>, the same reply a deliberately
    /// nonsensical header gets — so a driver that identified the variant by asking would have
    /// called that UCCM-P a plain UCCM.
    /// </para>
    /// <para>
    /// <see langword="null"/> for an error prompt, where the token is in
    /// <c>Transaction.PromptStatus</c> instead. Default is to ignore it. <b>Never throw.</b>
    /// </para>
    /// </remarks>
    /// <param name="word">The grammar word the prompt matched, or <see langword="null"/>.</param>
    void NotePrompt(string? word)
    {
        // Most families learn nothing from their own prompt.
    }

    /// <summary>
    /// For a <see cref="LinkStyle.Broadcast"/> family: the plan key a heard line belongs to, or
    /// <see langword="null"/> for a line that is not one of yours (#310).
    /// </summary>
    /// <remarks>
    /// The session's listener sorts every line the talker sends by this key and answers a plan
    /// entry with the latest lines of that key. A key must be one of <see cref="Plan"/>'s entries
    /// or a name the driver's <see cref="Parse"/> reads out of <see cref="PollPlan.WholeCycle"/>;
    /// the first entry of the fast tier is the <b>cycle boundary</b> — its arrival starts a new
    /// cycle — so it must be a line the talker sends exactly once per cycle. Never throw.
    /// </remarks>
    string? ClassifyLine(string line) => null;

    // ---- Added by #435, when the audit found the interface unable to say "never" -----------------

    /// <summary>
    /// Can this family ever supply this reading, whatever the receiver happens to be doing? (#435)
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The read-side counterpart of the command catalog.</b> <see cref="Commands"/> answers
    /// "what may I send", and §9.11 has said since #304 that a command a receiver lacks is disabled
    /// and explained rather than hidden. Nothing answered "what may I ever <i>know</i>", so a
    /// reading the family cannot carry was drawn as an em dash — which §9.11 defines under
    /// <i>Partial / streaming</i> as a field that has not arrived <i>yet</i>. A user could not tell
    /// a receiver that will never say from one that has not said so far.
    /// </para>
    /// <para>
    /// <b>Answer about the family, never about this moment.</b> A receiver that is merely
    /// disconnected, warming up, or failing to parse still <i>reports</i> a reading in this sense:
    /// §11.1's rule already covers a value that could not be read, and it renders as an em dash on
    /// purpose. Returning <see langword="false"/> is the stronger claim that no amount of waiting,
    /// reconnecting or asking differently will ever produce one — so it must be a fact about the
    /// protocol, not about the link.
    /// </para>
    /// <para>
    /// <b>The default is <see langword="true"/>, and that is the safe direction.</b> A driver that
    /// has not thought about a reading claims nothing: the interface goes on showing an em dash,
    /// which is what it did before this member existed. The dangerous answer is a wrong
    /// <see langword="false"/> — telling a user their receiver can never report something it
    /// reports perfectly well is worse than a blank field, because it stops them looking.
    /// </para>
    /// </remarks>
    bool Reports(ReceiverReading reading) => true;

    // ---- Added by #470, when a second prompted family turned out not to prompt the same way ------

    /// <summary>
    /// What ends a transaction on this family's link (§7.2, #470).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only a <see cref="LinkStyle.QueryResponse"/> family has one.</b> A broadcast family is
    /// never written to after recognition, so nothing waits for a prompt on its link and the default
    /// is harmless there.
    /// </para>
    /// <para>
    /// The default is the SmartClock's, so a driver written before this member existed still says
    /// what it meant. Override it only from a measurement: a grammar naming a prompt the receiver
    /// does not send costs nothing, but one that <i>omits</i> the prompt it does send makes every
    /// transaction run to its full timeout with the answer already read — which is exactly how #470
    /// presented, and it reports as "no receiver answered" rather than as anything to do with a
    /// prompt.
    /// </para>
    /// </remarks>
    PromptGrammar Prompt => PromptGrammar.SmartClock;

    /// <summary>
    /// The unsolicited binary frames this family broadcasts between replies (#481).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Declared, never inferred.</b> A frame is removed from the byte stream before it is split
    /// into lines, because it carries no line terminator and the reader would otherwise glue it onto
    /// its neighbour — and, when its payload happens to hold <c>0x0D</c> or <c>0x0A</c>, cut it in
    /// half beyond any hope of reassembly. That has to happen before decoding, which is why it is
    /// the transport's job and why the driver has to say so in advance.
    /// </para>
    /// <para>
    /// The default is <see cref="BinaryFrameGrammar.None"/>, and for a family that speaks only when
    /// spoken to it is not merely a safe default but the correct one: the byte path is then bit for
    /// bit what it was, and nothing is scanned for a marker that cannot arrive.
    /// </para>
    /// <para>
    /// <b>Override it only from a capture.</b> A grammar whose length is wrong is worse than none:
    /// it would swallow the wrong 44 bytes out of the middle of a genuine reply, and the reply would
    /// come back subtly short rather than obviously broken.
    /// </para>
    /// </remarks>
    BinaryFrameGrammar BinaryFrames => BinaryFrameGrammar.None;

    /// <summary>
    /// Hands the driver the unsolicited frames that arrived during a transaction (#481).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Separate from <see cref="Parse"/> because a broadcast is not an answer.</b> A frame is not
    /// the reply to the command that happened to be in flight when it arrived — it is the receiver
    /// talking on its own schedule, and folding it into that command's response would make a reading
    /// look like something it was asked for.
    /// </para>
    /// <para>
    /// Called between transactions on the session's single consumer, so an implementation may store
    /// what it learns without locking. It must not throw: §11.1 applies to a broadcast exactly as it
    /// does to a reply, and a malformed frame is "no reading".
    /// </para>
    /// <para>
    /// The default ignores them, which is right for every family that broadcasts nothing and, with
    /// <see cref="BinaryFrames"/> left at its own default, is never called at all.
    /// </para>
    /// </remarks>
    /// <param name="frames">Whole frames, in arrival order. Never null; often empty.</param>
    void Observe(IReadOnlyList<byte[]> frames)
    {
        // Nothing to learn from a family that does not broadcast.
    }
}
