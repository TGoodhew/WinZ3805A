using System.IO.Pipelines;

namespace WinZ3805A.Device.Transport;

/// <summary>
/// A byte-level, full-duplex link to a receiver. <see cref="SerialTransport"/> is the real one;
/// <c>FakeTransport</c> replays captured bytes through the identical pipe so the §7.2 transaction
/// loop can be proved with no hardware attached (§15 step 1).
/// </summary>
/// <remarks>
/// The read side is exposed as a <see cref="PipeReader"/> rather than a <see cref="Stream"/> on
/// purpose. §6.4 requires the prompt-sentinel scan to run over <see cref="System.Buffers.ReadOnlySequence{T}"/>
/// so that a sentinel straddling two reads costs nothing; handing callers a stream would invite
/// exactly the hand-rolled buffer compaction §6.4 forbids.
/// </remarks>
public interface ITransport : IAsyncDisposable
{
    /// <summary>Human-readable identification for logs and the connection UI, e.g. <c>COM3 @ 9600-8-N-1</c>.</summary>
    string Description { get; }

    /// <summary>True between a successful <see cref="OpenAsync"/> and disposal.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// The inbound byte stream. Valid only while <see cref="IsOpen"/>; reading a closed transport
    /// throws <see cref="TransportException"/>.
    /// </summary>
    PipeReader Input { get; }

    /// <summary>Opens the link.</summary>
    /// <exception cref="TransportException">The port is missing, held by another process, or otherwise unopenable.</exception>
    ValueTask OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes bytes to the device and flushes them.</summary>
    /// <exception cref="TransportException">The write failed; <see cref="TransportException.Fault"/> says how.</exception>
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Throws away anything the device has already sent but nobody has read.
    /// </summary>
    /// <remarks>
    /// Called at the start of every transaction. Because the receiver only speaks when spoken to and
    /// serves one transaction at a time (§7.2), any bytes sitting in the buffer beforehand are the
    /// late tail of a transaction that timed out, and letting them run into the next response is how
    /// a single timeout turns into a permanently misaligned session.
    /// </remarks>
    void DiscardInput();
}
