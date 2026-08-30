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
    int? SatellitesTracked);

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
}
