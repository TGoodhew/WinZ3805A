namespace WinZ3805A.Device.Transport;

/// <summary>How a transaction ended.</summary>
public enum TransactionOutcome
{
    /// <summary>The prompt sentinel arrived. Whatever is in <see cref="Transaction.Lines"/> is the complete response.</summary>
    Completed = 0,

    /// <summary>No prompt arrived within the timeout. Any lines received are kept, for diagnostics only.</summary>
    TimedOut,

    /// <summary>The link failed. <see cref="Transaction.Fault"/> says how.</summary>
    Faulted,
}

/// <summary>
/// The result of one command-and-response exchange with the receiver (§7.2).
/// </summary>
/// <remarks>
/// A transaction reports rather than throws. Timeouts and dropped links are ordinary events in a
/// lab on the end of a serial cable — §7.2 counts three consecutive timeouts before reconnecting —
/// so they are outcomes the caller inspects, not exceptions it has to catch. Cancellation by the
/// caller is the one exception to that, because there is no result to report.
/// </remarks>
public sealed record Transaction
{
    /// <summary>The command as sent, without its terminator.</summary>
    public required string Command { get; init; }

    /// <summary>How the transaction ended.</summary>
    public required TransactionOutcome Outcome { get; init; }

    /// <summary>
    /// The response lines, echo removed and line terminators stripped. Empty for a setter, which
    /// §7.2 says answers with the prompt alone.
    /// </summary>
    public required IReadOnlyList<string> Lines { get; init; }

    /// <summary>
    /// True when the first line received was the receiver echoing the command back, as it does under
    /// <c>FDUPlex ON</c>. Detected per transaction, never assumed either way (§7.2).
    /// </summary>
    public required bool EchoDiscarded { get; init; }

    /// <summary>Wall time from writing the command to the prompt, the timeout, or the fault.</summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>The link failure, when <see cref="Outcome"/> is <see cref="TransactionOutcome.Faulted"/>.</summary>
    public TransportFault Fault { get; init; } = TransportFault.None;

    /// <summary>The failure text, when <see cref="Outcome"/> is <see cref="TransactionOutcome.Faulted"/>.</summary>
    public string? FaultMessage { get; init; }

    /// <summary>True only for <see cref="TransactionOutcome.Completed"/>.</summary>
    public bool Succeeded => Outcome == TransactionOutcome.Completed;

    /// <summary>The response as one string, newline-separated. The status-screen parser works on this.</summary>
    public string Text => string.Join('\n', Lines);

    /// <summary>The first response line, or null if there was none. Scalar queries answer on one line.</summary>
    public string? FirstLine => Lines.Count > 0 ? Lines[0] : null;
}
