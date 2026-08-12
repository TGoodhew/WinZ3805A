namespace WinZ3805A.Device.Transport;

/// <summary>
/// A serial-port failure, classified. Thrown only by <see cref="ITransport.OpenAsync"/> and
/// <see cref="ITransport.WriteAsync"/>; the read path reports faults through
/// <see cref="Transaction.Outcome"/> instead, because a transaction that dies mid-read still has a
/// result to report.
/// </summary>
public sealed class TransportException : Exception
{
    public TransportException(TransportFault fault, string message)
        : base(message) => Fault = fault;

    public TransportException(TransportFault fault, string message, Exception? innerException)
        : base(message, innerException) => Fault = fault;

    /// <summary>What went wrong, in the terms the reconnect policy and the connection dialog use.</summary>
    public TransportFault Fault { get; }
}
