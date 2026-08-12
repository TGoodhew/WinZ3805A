using Microsoft.Extensions.Logging;

namespace WinZ3805A.Device.Transport;

/// <summary>
/// Log messages for the transport and the transaction loop, defined through the
/// <c>LoggerMessage</c> source generator as §6.4 requires — no reflection, no boxing, and the event
/// IDs stay stable as the text is edited.
/// </summary>
internal static partial class TransportLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "Opened {Description}.")]
    internal static partial void PortOpened(ILogger logger, string description);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "Could not open {Description}: {Fault}.")]
    internal static partial void PortOpenFailed(ILogger logger, string description, TransportFault fault, Exception exception);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning, Message = "Closing {Description} did not complete cleanly.")]
    internal static partial void PortCloseFailed(ILogger logger, string description, Exception exception);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning,
        Message = "Closing {Description} did not return within {TimeoutMs} ms; abandoning the port object.")]
    internal static partial void PortCloseTimedOut(ILogger logger, string description, double timeoutMs);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Debug, Message = "Flushing the driver buffer on {Description} failed.")]
    internal static partial void InputDiscardFailed(ILogger logger, string description, Exception exception);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Trace, Message = "-> {Command}")]
    internal static partial void CommandSent(ILogger logger, string command);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Trace, Message = "<- {LineCount} line(s) in {ElapsedMs:F0} ms (echo discarded: {EchoDiscarded})")]
    internal static partial void TransactionCompleted(ILogger logger, int lineCount, double elapsedMs, bool echoDiscarded);

    [LoggerMessage(EventId = 1012, Level = LogLevel.Warning,
        Message = "{Command} timed out after {TimeoutMs:F0} ms with {LineCount} line(s) received and no prompt.")]
    internal static partial void TransactionTimedOut(ILogger logger, string command, double timeoutMs, int lineCount);

    [LoggerMessage(EventId = 1013, Level = LogLevel.Warning, Message = "{Command} failed: {Fault}.")]
    internal static partial void TransactionFaulted(ILogger logger, string command, TransportFault fault, Exception exception);

    [LoggerMessage(EventId = 1014, Level = LogLevel.Debug,
        Message = "Discarded {ByteCount} unread byte(s) left over from an earlier transaction.")]
    internal static partial void StaleInputDiscarded(ILogger logger, long byteCount);
}
