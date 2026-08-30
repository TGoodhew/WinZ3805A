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
    /// The response lines, echo removed and line terminators stripped. Empty for a setter, and for
    /// a command the receiver rejected — §7.2 says both answer with the prompt alone.
    /// </summary>
    public required IReadOnlyList<string> Lines { get; init; }

    /// <summary>
    /// True when the first line received was the receiver echoing the command back, as it does under
    /// <c>FDUPlex ON</c>. Detected per transaction, never assumed either way (§7.2).
    /// </summary>
    public required bool EchoDiscarded { get; init; }

    /// <summary>Wall time from writing the command to the prompt, the timeout, or the fault.</summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>
    /// The error token the receiver put in the prompt, such as <c>E-230</c>, or null for the
    /// ordinary prompt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This reports the receiver's error queue, not this command.</b> §7.2 records the
    /// measurement: with a single error queued, three successive commands that each succeeded and
    /// returned correct data all carried an <c>E-113</c> prompt. The prompt names the <i>newest</i>
    /// queued error while <c>:SYST:ERR?</c> returns the oldest first, and it reverts to the ordinary
    /// prompt only once the queue is fully drained.
    /// </para>
    /// <para>
    /// The token is kept verbatim rather than parsed to a signed number: SCPI's standard codes are
    /// negative and the prompt prints no sign, and inventing one would put a guess into the
    /// Diagnostics page.
    /// </para>
    /// </remarks>
    public string? PromptStatus { get; init; }

    /// <summary>
    /// True when the receiver's error queue was not empty as of the end of this transaction.
    /// </summary>
    /// <remarks>
    /// <b>This says nothing about whether this command succeeded</b>, and the name says so because
    /// the previous one — <c>HasDeviceError</c> — did not, and three call sites read it as a verdict
    /// on the command they had just sent (#173). Something queued by an earlier poll makes this true
    /// for a command that worked perfectly.
    /// </remarks>
    public bool ErrorQueueNotEmpty => PromptStatus is not null;

    /// <summary>
    /// True when the receiver answered with an error prompt and <b>no response body</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the honest test for "the receiver rejected this <i>query</i>", and it is sound
    /// because §7.2 establishes that a rejected command answers with the prompt and nothing else.
    /// A query that came back with lines came back with an answer, whatever is sitting in the queue.
    /// </para>
    /// <para>
    /// <b>It is not sound for a setter</b>, which answers with the prompt alone whether it worked or
    /// not — there is no body to distinguish the two. Nothing about the prompt can tell a caller
    /// whether a setter succeeded; that needs the queue drained beforehand or <c>:SYST:ERR?</c>
    /// afterwards, which §7.2 gives to tier C alone.
    /// </para>
    /// </remarks>
    public bool WasRejected => PromptStatus is not null && Lines.Count == 0;

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
