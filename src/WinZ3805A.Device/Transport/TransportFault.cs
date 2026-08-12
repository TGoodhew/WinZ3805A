namespace WinZ3805A.Device.Transport;

/// <summary>
/// Why a transport operation failed, in the terms the UI and the reconnect policy care about.
/// </summary>
/// <remarks>
/// §6.4 requires every read and write to catch <see cref="IOException"/>,
/// <see cref="UnauthorizedAccessException"/>, <see cref="InvalidOperationException"/> and
/// <see cref="ObjectDisposedException"/>, all four of which are reachable when a USB-serial adapter
/// is pulled while the port is open. This enum is what those four collapse into once caught, so no
/// caller has to re-derive the meaning from an exception type.
/// </remarks>
public enum TransportFault
{
    /// <summary>No fault.</summary>
    None = 0,

    /// <summary>The named port does not exist. On ARM64 this is often a missing driver rather than a missing device (§6.1).</summary>
    PortNotFound,

    /// <summary>The port exists but is held by another process — usually a terminal emulator (§9.11, "No permission").</summary>
    AccessDenied,

    /// <summary>The port went away underneath an open handle. The unplug case P0-14 has to survive.</summary>
    DeviceRemoved,

    /// <summary>An I/O error that is not obviously a removal. Treated as recoverable by the §7.2 reconnect policy.</summary>
    Io,

    /// <summary>The transport was used before <see cref="ITransport.OpenAsync"/> succeeded, or after it closed.</summary>
    NotOpen,

    /// <summary>Something the classifier does not recognise.</summary>
    Unknown,
}

/// <summary>Maps the exceptions <c>SerialPort</c> can raise onto <see cref="TransportFault"/>.</summary>
/// <remarks>
/// Public rather than internal because §6.4's rule — that these four exception types are all
/// reachable when a USB-serial adapter is pulled and every one must be survived — applies to
/// anything that owns a transport, not only to the transport itself. <c>DeviceSessionService</c>
/// needs the same predicate to decide when to start reconnecting, and a second hand-written copy
/// of the list is precisely the drift this type exists to prevent.
/// </remarks>
public static class TransportFaults
{
    /// <summary>True for every exception type §6.4 requires the read and write paths to survive.</summary>
    public static bool IsTransportFault(Exception exception) => exception is
        TransportException or
        IOException or
        UnauthorizedAccessException or
        InvalidOperationException or
        ObjectDisposedException;

    /// <summary>Classifies an exception raised by the port, the base stream, or the pipe over it.</summary>
    public static TransportFault Classify(Exception exception) => exception switch
    {
        TransportException transport => transport.Fault,

        // Surprise removal surfaces as a disposed or invalid-state port far more often than as an
        // IOException, because the port object outlives the hardware behind it.
        ObjectDisposedException => TransportFault.DeviceRemoved,
        InvalidOperationException => TransportFault.DeviceRemoved,

        UnauthorizedAccessException => TransportFault.AccessDenied,
        FileNotFoundException => TransportFault.PortNotFound,

        // SerialPort.Open reports a non-existent port as a bare IOException carrying the port name,
        // which is the only thing that distinguishes it from a mid-transaction I/O error.
        IOException io when io.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            => TransportFault.PortNotFound,
        IOException => TransportFault.Io,

        ArgumentException => TransportFault.PortNotFound,
        _ => TransportFault.Unknown,
    };
}
