using System.IO.Pipelines;
using System.IO.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace WinZ3805A.Device.Transport;

/// <summary>
/// The real RS-232 link: a <see cref="SerialPort"/> whose base stream is pumped into a
/// <see cref="Pipe"/> that the transaction loop reads from.
/// </summary>
/// <remarks>
/// <para>
/// The pump is not decoration, and this is worth knowing before anyone simplifies it back to
/// <c>PipeReader.Create(port.BaseStream)</c>. <see cref="SerialPort"/>'s base stream <b>ignores the
/// CancellationToken</b> passed to <c>ReadAsync</c>: with <see cref="SerialPort.InfiniteTimeout"/>
/// the read completes when bytes arrive and at no other time. Reading the stream directly from the
/// transaction loop therefore makes §7.2's timeouts unenforceable — the await never returns, and
/// <c>CancelPendingRead</c> cannot help because the wait is inside the stream, not the pipe. Measured
/// on a Z3805A: one command that answered with an error prompt hung the process indefinitely.
/// </para>
/// <para>
/// With the pump, the uncancellable read belongs to a background loop that nobody waits on, and the
/// transaction loop waits on a real <see cref="Pipe"/>, where cancellation works. The pump also means
/// bytes are collected whether or not a transaction is in flight, which matters because this receiver
/// announces itself the moment DTR is asserted.
/// </para>
/// <para>
/// The three code mitigations of §6.4's surprise-removal list live here; the fourth is not code but
/// <c>docs/manual-qa.md</c> §1. In particular this type never subscribes to
/// <c>DataReceived</c>, <c>ErrorReceived</c> or <c>PinChanged</c>: those events raise on an internal
/// thread that can take the process down when a USB-serial adapter is pulled, which is the P0-14 case.
/// Nor is a read wrapped in <c>Task.Run</c> — the pump is a genuine async loop and burns no thread.
/// The one <c>Task.Run</c> here is around <see cref="SerialPort.Dispose(bool)"/>, which is a blocking
/// call that can hang forever on a removed device: a different problem with a different answer.
/// </para>
/// </remarks>
public sealed class SerialTransport : ITransport
{
    /// <summary>
    /// How long disposal waits for the port to close before walking away from it. Generous enough
    /// that an ordinary close always finishes, short enough that P0-14's ten-second budget survives.
    /// </summary>
    private static readonly TimeSpan s_closeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Read granularity. A status screen is ~1900 bytes; nothing is gained by asking for more at once.</summary>
    private const int ReadBufferSize = 1024;

    /// <summary>How long the pump pauses after a zero-byte read on an open port, so it cannot spin.</summary>
    private static readonly TimeSpan ZeroReadBackoff = TimeSpan.FromMilliseconds(10);

    private readonly string _portName;
    private readonly SerialSettings _settings;
    private readonly ILogger _logger;

    private SerialPort? _port;
    private Pipe? _pipe;
    private Task? _pump;
    private bool _disposed;

    public SerialTransport(string portName, SerialSettings settings, ILogger<SerialTransport>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        ArgumentNullException.ThrowIfNull(settings);

        _portName = portName;
        _settings = settings;
        _logger = logger ?? NullLogger<SerialTransport>.Instance;
    }

    /// <inheritdoc />
    public string Description => $"{_portName} @ {_settings}";

    /// <inheritdoc />
    public bool IsOpen => !_disposed && _port?.IsOpen == true;

    /// <inheritdoc />
    public PipeReader Input => _pipe?.Reader ?? throw new TransportException(TransportFault.NotOpen, $"{Description} is not open.");

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <see cref="SerialPort.Open"/> is synchronous and has no async form. It is quick on a healthy
    /// port and slow on a sick one — a driver that has to enumerate a device which is not answering
    /// can take seconds — so it runs on the thread pool rather than on whichever thread awaited
    /// this (#319). Before that it ran on the caller's, and nothing in the connect chain moves off
    /// the UI thread: the connection dialog's first open, and every step of an auto-detect walk,
    /// froze the window for as long as the driver took.
    /// </para>
    /// <para>
    /// <b>This is not the <c>Task.Run</c> §6.4 forbids.</b> That rule is about <i>reads</i>, and
    /// exists because <see cref="SerialPort.BaseStream"/> offers genuine async I/O to use instead;
    /// here there is no async form to prefer, so a pool thread is the only way not to block the
    /// caller. The token cannot interrupt an open once it has started — nothing can — so it only
    /// prevents one starting.
    /// </para>
    /// </remarks>
    public async ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (IsOpen)
        {
            return;
        }

        SerialPort port = new(_portName, _settings.BaudRate, _settings.Parity, _settings.DataBits, _settings.StopBits)
        {
            // §7.1: handshake is None only, and both modem lines are asserted on open because the
            // receiver's line driver will not transmit to a dead DTR on some cable assemblies.
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,

            // The pump owns the read and never wants it to fail on a quiet line — a TimeoutException
            // from the stream would complete the pipe and end the session. Transaction timeouts are
            // enforced on the pipe instead (§7.2). The write is bounded, because a write that cannot
            // complete is a broken link rather than an idle one.
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 2000,
        };

        try
        {
            await Task.Run(port.Open, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (TransportFaults.IsTransportFault(ex) || ex is ArgumentException)
        {
            TransportFault fault = TransportFaults.Classify(ex);
            TransportLog.PortOpenFailed(_logger, Description, fault, ex);
            port.Dispose();
            throw new TransportException(fault, $"Could not open {Description}: {ex.Message}", ex);
        }
        catch (OperationCanceledException)
        {
            // Cancelled before the open ran. The port was constructed here and never handed over,
            // so this method still owns it.
            port.Dispose();
            throw;
        }

        if (_disposed)
        {
            // Disposed while the open was in flight — a window that did not exist while the open
            // was synchronous, and one that matters here: DisposeAsync has already exchanged
            // _port away and found nothing, so assigning it below would leave an open port that
            // nothing will ever close, holding the COM port against the next connect.
            //
            // Closed off this thread for the reason DisposeAsync gives: Dispose blocks, and on a
            // port that has just failed to open properly it can block for a long time.
            _ = Task.Run(() =>
            {
                try
                {
                    port.Dispose();
                }
                catch (Exception ex)
                {
                    TransportLog.PortCloseFailed(_logger, Description, ex);
                }
            });

            throw new ObjectDisposedException(nameof(SerialTransport));
        }

        _port = port;
        _pipe = new Pipe();
        _pump = PumpAsync(port, _pipe.Writer);

        TransportLog.PortOpened(_logger, Description);
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        SerialPort port = _port ?? throw new TransportException(TransportFault.NotOpen, $"{Description} is not open.");

        try
        {
            Stream stream = port.BaseStream;
            await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (TransportFaults.IsTransportFault(ex))
        {
            TransportFault fault = TransportFaults.Classify(ex);
            throw new TransportException(fault, $"Write to {Description} failed: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public void DiscardInput()
    {
        try
        {
            _port?.DiscardInBuffer();
        }
        catch (Exception ex) when (TransportFaults.IsTransportFault(ex))
        {
            // Nothing here is worth failing a transaction over: the driver buffer is being emptied
            // precisely because its contents are already known to be stale.
            TransportLog.InputDiscardFailed(_logger, Description, ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // §6.4 mitigation 3: a dedicated path that tolerates an already-faulted port. Dispose blocks,
        // and on a device that has been unplugged mid-transaction it can block for a long time, so it
        // runs off this thread and is given a deadline rather than being trusted. It also has to come
        // first: disposing the port is what ends the pump's otherwise uncancellable read.
        SerialPort? port = Interlocked.Exchange(ref _port, null);
        if (port is not null)
        {
            Task closing = Task.Run(() =>
            {
                try
                {
                    port.Dispose();
                }
                catch (Exception ex)
                {
                    TransportLog.PortCloseFailed(_logger, Description, ex);
                }
            });

            try
            {
                await closing.WaitAsync(s_closeTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                TransportLog.PortCloseTimedOut(_logger, Description, s_closeTimeout.TotalMilliseconds);
            }
        }

        Task? pump = _pump;
        _pump = null;
        if (pump is not null)
        {
            try
            {
                await pump.WaitAsync(s_closeTimeout).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException || TransportFaults.IsTransportFault(ex))
            {
                TransportLog.PortCloseFailed(_logger, Description, ex);
            }
        }

        Pipe? pipe = _pipe;
        _pipe = null;
        if (pipe is not null)
        {
            await pipe.Reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the port into the pipe until the link ends, then completes the pipe with whatever ended
    /// it so a waiting transaction sees a fault rather than silence.
    /// </summary>
    private async Task PumpAsync(SerialPort port, PipeWriter writer)
    {
        Exception? failure = null;
        Stream stream = port.BaseStream;

        try
        {
            while (true)
            {
                Memory<byte> buffer = writer.GetMemory(ReadBufferSize);

                // Deliberately no CancellationToken: SerialPort's stream ignores it, so passing one
                // would only suggest a cancellation guarantee that does not exist. Disposing the port
                // is what ends this read.
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Not the end of anything. DiscardInput calls DiscardInBuffer, and purging the
                    // driver buffer aborts the read already in flight — measured on a Prolific
                    // adapter, where every transaction after the first one failed as DeviceRemoved
                    // because the pipe had been completed by this. The port, not the read, decides
                    // whether the link is over.
                    read = 0;
                }

                if (read == 0)
                {
                    // Zero bytes does not always mean end of stream either: the same purge completes
                    // a pending read empty-handed on some drivers. Only a closed port ends the pump.
                    if (!port.IsOpen)
                    {
                        break;
                    }

                    await Task.Delay(ZeroReadBackoff).ConfigureAwait(false);
                    continue;
                }

                writer.Advance(read);

                FlushResult flush = await writer.FlushAsync().ConfigureAwait(false);
                if (flush.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // The ordinary end of this loop: the port was disposed underneath the read. Nothing is
            // waiting on the pipe by then, and a disposal is not a fault worth reporting upwards.
        }
        catch (Exception ex) when (TransportFaults.IsTransportFault(ex))
        {
            // The other end on Windows: the adapter was pulled and the handle went with it. That one
            // travels up the pipe, because a transaction in flight has to report it (P0-14).
            failure = ex;
        }

        await writer.CompleteAsync(failure).ConfigureAwait(false);
    }
}
