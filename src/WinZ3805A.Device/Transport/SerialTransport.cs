using System.IO.Pipelines;
using System.IO.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace WinZ3805A.Device.Transport;

/// <summary>
/// The real RS-232 link, a <see cref="SerialPort"/> with a <see cref="PipeReader"/> over its base
/// stream.
/// </summary>
/// <remarks>
/// <para>
/// All four §6.4 surprise-removal mitigations live here. In particular this type never subscribes to
/// <c>DataReceived</c>, <c>ErrorReceived</c> or <c>PinChanged</c>: those events raise on an internal
/// thread that can take the process down when a USB-serial adapter is pulled, which is precisely the
/// P0-14 case. Reading <see cref="SerialPort.BaseStream"/> asynchronously keeps every failure on a
/// thread that has a <c>try</c> around it.
/// </para>
/// <para>
/// Nor is a read ever wrapped in <c>Task.Run</c>. The one <c>Task.Run</c> in this file is around
/// <see cref="SerialPort.Dispose(bool)"/>, which is a blocking call that can hang indefinitely on a
/// removed device — a different problem with a different answer.
/// </para>
/// </remarks>
public sealed class SerialTransport : ITransport
{
    /// <summary>
    /// How long disposal waits for the port to close before walking away from it. Generous enough
    /// that an ordinary close always finishes, short enough that P0-14's ten-second budget survives.
    /// </summary>
    private static readonly TimeSpan s_closeTimeout = TimeSpan.FromSeconds(2);

    private readonly string _portName;
    private readonly SerialSettings _settings;
    private readonly ILogger _logger;

    private SerialPort? _port;
    private PipeReader? _input;
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
    public PipeReader Input => _input ?? throw new TransportException(TransportFault.NotOpen, $"{Description} is not open.");

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="SerialPort.Open"/> is synchronous and has no async form. It is quick on a healthy
    /// port and slow on a sick one, so callers open off the UI thread; wrapping it here would only
    /// hide that.
    /// </remarks>
    public ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (IsOpen)
        {
            return ValueTask.CompletedTask;
        }

        SerialPort port = new(_portName, _settings.BaudRate, _settings.Parity, _settings.DataBits, _settings.StopBits)
        {
            // §7.1: handshake is None only, and both modem lines are asserted on open because the
            // receiver's line driver will not transmit to a dead DTR on some cable assemblies.
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,

            // The transaction loop owns every timeout (§7.2). Leaving these at their 500 ms default
            // would abort the 15 s status-screen read from underneath it.
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = SerialPort.InfiniteTimeout,
        };

        try
        {
            port.Open();
        }
        catch (Exception ex) when (TransportFaults.IsTransportFault(ex) || ex is ArgumentException)
        {
            TransportFault fault = TransportFaults.Classify(ex);
            TransportLog.PortOpenFailed(_logger, Description, fault, ex);
            port.Dispose();
            throw new TransportException(fault, $"Could not open {Description}: {ex.Message}", ex);
        }

        _port = port;
        _input = PipeReader.Create(port.BaseStream, new StreamPipeReaderOptions(leaveOpen: true));
        TransportLog.PortOpened(_logger, Description);
        return ValueTask.CompletedTask;
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

        PipeReader? input = _input;
        _input = null;
        try
        {
            input?.Complete();
        }
        catch (Exception ex) when (TransportFaults.IsTransportFault(ex))
        {
            TransportLog.PortCloseFailed(_logger, Description, ex);
        }

        SerialPort? port = Interlocked.Exchange(ref _port, null);
        if (port is null)
        {
            return;
        }

        // §6.4 mitigation 3: a dedicated path that tolerates an already-faulted port. Dispose blocks,
        // and on a device that has been unplugged mid-transaction it can block for a long time, so it
        // runs off this thread and is given a deadline rather than being trusted.
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
}
