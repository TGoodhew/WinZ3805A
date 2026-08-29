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
/// <b>Adding one is documented in <c>README.md</c>, under "Adding a receiver".</b> That walkthrough
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
}
