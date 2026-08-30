using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace WinZ3805A.Device.Transport;

/// <summary>
/// The receiver's line protocol: write a command, discard the echo, read until the prompt (§7.2).
/// </summary>
/// <remarks>
/// <para>
/// Three things make this harder than it looks, and all three are why §15 puts it first.
/// </para>
/// <para>
/// <b>The device may echo.</b> The manual's default is <c>FDUPlex ON</c>, sending every character
/// back; the bench unit echoes nothing (§7.2). The echo is *detected* by comparing the first line
/// received to the line transmitted, never assumed — a session that assumes echo-on eats the first
/// line of every response the day it meets a unit with echo off, and one that assumes echo-off reads
/// its own command back as the answer.
/// </para>
/// <para>
/// <b>The terminator is a prompt, not a newline.</b> A transaction ends at the prompt —
/// <c>scpi &gt; </c> or <c>E-nnn&gt; </c> — which is what makes a setter (prompt only) and a
/// multi-line block (~1900 bytes for the status screen) the same shape of read. <c>ReadLine</c>
/// cannot express that.
/// </para>
/// <para>
/// <b>The prompt straddles reads.</b> At 9600 baud a status screen arrives in dozens of chunks and
/// the sentinel will land across a boundary. Scanning a <see cref="ReadOnlySequence{T}"/> with a
/// <see cref="SequenceReader{T}"/> and telling the pipe what was *consumed* versus merely *examined*
/// makes that case cost nothing, which is the whole reason §6.4 mandates Pipelines here.
/// </para>
/// <para>
/// This type does not own the transport and does not dispose it. It also does not serialise callers:
/// the receiver is strictly one transaction at a time and §7.2 puts that duty on
/// <c>DeviceSessionService</c>'s single-consumer channel.
/// </para>
/// </remarks>
public sealed class LineProtocol
{
    private const byte Cr = (byte)'\r';
    private const byte Lf = (byte)'\n';

    /// <summary>The word the ordinary prompt is built from.</summary>
    private const string PromptWord = "scpi";

    /// <summary>What the prompt shows instead of <see cref="PromptWord"/> while the error queue is not empty (§7.2).</summary>
    private const string ErrorPromptPrefix = "E-";

    /// <summary>
    /// The longest tail worth testing against the prompt grammar. Anything longer is a response line
    /// that has not finished arriving, and testing it would only waste the decode.
    /// </summary>
    private const int MaxPromptLength = 32;

    /// <summary>Stands in for a command in the <see cref="Transaction"/> returned by <see cref="SynchroniseAsync"/>.</summary>
    private const string ConnectLabel = "(connect)";

    /// <summary>Tier S (§8.2), clears the status registers, and answers with nothing worth keeping.</summary>
    private const string ClearStatusCommand = "*CLS";

    /// <summary>
    /// CR and LF. §6.4 once nominated <see cref="SearchValues{T}"/> for this scan; its row was struck
    /// on 21 Aug 2026 (#78) because .NET 10 ships no <see cref="SequenceReader{T}"/> overload that
    /// accepts one, and §7.2 mandates <see cref="SequenceReader{T}"/> for sentinel detection. The span
    /// overloads are what exists, and a two-value <c>IndexOfAny</c> is already vectorised, so
    /// <see cref="SearchValues{T}"/> — which earns its keep by amortising set preprocessing — would
    /// buy nothing here even if it fitted.
    /// </summary>
    private static ReadOnlySpan<byte> LineDelimiters => "\r\n"u8;

    private readonly ITransport _transport;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// How long the next command may spend realigning the stream, or null when it is already
    /// aligned (#209). See <see cref="ResynchroniseAsync"/>.
    /// </summary>
    private TimeSpan? _resynchroniseWithin;
    private readonly ILogger _logger;

    public LineProtocol(ITransport transport, TimeProvider timeProvider, ILogger<LineProtocol>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _transport = transport;
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger<LineProtocol>.Instance;
    }

    /// <summary>Runs one transaction with the timeout class §7.2 assigns to the command.</summary>
    public Task<Transaction> ExecuteAsync(string command, CancellationToken cancellationToken = default)
        => ExecuteAsync(command, TransactionTimeouts.For(command), cancellationToken);

    /// <summary>Runs one transaction with an explicit timeout.</summary>
    /// <remarks>
    /// Returns a <see cref="Transaction"/> for every outcome except caller cancellation, which throws
    /// <see cref="OperationCanceledException"/> because there is nothing to report.
    /// </remarks>
    public async Task<Transaction> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        string sent = command.Trim();

        // Realigning happens before this command's clock starts, and that is not a detail: the
        // budget for clearing somebody else's abandoned reply can be sixty seconds, while a scalar
        // query's own deadline is three. Charged to this command, one cancelled diagnostic-log read
        // would time out the next poll and the two after it — and §7.2 reconnects on three
        // consecutive timeouts, so navigating away from a page would drop the link (#209).
        await ResynchroniseAsync().ConfigureAwait(false);

        long startedAt = _timeProvider.GetTimestamp();
        List<string> lines = [];
        bool echoDiscarded;

        // The timeout is driven by the injected TimeProvider, so a fixture test pins it instead of
        // waiting fifteen real seconds for the status-screen case (§6.4, §12).
        using CancellationTokenSource timeoutSource = new(timeout, _timeProvider);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            DiscardStaleInput();

            await _transport.WriteAsync(Encoding.ASCII.GetBytes($"{sent}\r\n"), linked.Token).ConfigureAwait(false);
            TransportLog.CommandSent(_logger, sent);

            string? promptStatus = await ReadUntilPromptAsync(lines, linked.Token).ConfigureAwait(false);
            echoDiscarded = TryDiscardEcho(sent, lines);

            TimeSpan elapsed = _timeProvider.GetElapsedTime(startedAt);
            TransportLog.TransactionCompleted(_logger, lines.Count, elapsed.TotalMilliseconds, echoDiscarded);

            return new Transaction
            {
                Command = sent,
                Outcome = TransactionOutcome.Completed,
                Lines = lines,
                EchoDiscarded = echoDiscarded,
                Elapsed = elapsed,
                PromptStatus = promptStatus,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The linked source fired but the caller did not cancel, so this is the timeout. Whatever
            // arrived is kept: a truncated response is the most useful thing Diagnostics can show.
            echoDiscarded = TryDiscardEcho(sent, lines);
            TransportLog.TransactionTimedOut(_logger, sent, timeout.TotalMilliseconds, lines.Count);

            NeedsResynchronising(lines.Count, timeout);

            return new Transaction
            {
                Command = sent,
                Outcome = TransactionOutcome.TimedOut,
                Lines = lines,
                EchoDiscarded = echoDiscarded,
                Elapsed = _timeProvider.GetElapsedTime(startedAt),
            };
        }
        catch (OperationCanceledException)
        {
            // The caller gave up waiting. The receiver did not give up sending — it answers one
            // command at a time and finishes what it started — so the rest of this reply is still
            // coming, and would be read as the next command's answer (#209).
            NeedsResynchronising(lines.Count, timeout);
            throw;
        }
        catch (Exception ex) when (TransportFaults.IsTransportFault(ex))
        {
            // §6.4 mitigation 2: IOException, UnauthorizedAccessException, InvalidOperationException
            // and ObjectDisposedException are all reachable when the adapter is pulled mid-transaction,
            // and P0-14 requires the app to report Disconnected rather than fall over.
            TransportFault fault = TransportFaults.Classify(ex);
            TransportLog.TransactionFaulted(_logger, sent, fault, ex);

            return new Transaction
            {
                Command = sent,
                Outcome = TransactionOutcome.Faulted,
                Lines = lines,
                EchoDiscarded = TryDiscardEcho(sent, lines),
                Elapsed = _timeProvider.GetElapsedTime(startedAt),
                Fault = fault,
                FaultMessage = ex.Message,
            };
        }
    }

    /// <summary>
    /// Listens, without sending anything, until the receiver's first prompt or the timeout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call once after opening the port and before the first command. Asserting DTR makes this
    /// receiver announce itself — a Z3805A emits its identity string and a prompt with nothing asked
    /// of it — and the announcement takes long enough to arrive that it lands *after* the first
    /// command has gone out. The first transaction then reads the banner as its own response, and
    /// every reply after that is one behind: <c>*IDN?</c> answers with the banner, the next query
    /// answers with the identity, and nothing ever reports an error because every transaction does
    /// complete. Absorbing the banner first is what keeps the session aligned.
    /// </para>
    /// <para>
    /// The returned <see cref="Transaction"/> carries the banner text, which is worth keeping: it
    /// names the model and firmware revision before a single command has been sent, and §8.6 needs
    /// the model to decide which commands exist. A receiver that says nothing costs one timeout here
    /// and nothing afterwards, so keep the timeout short.
    /// </para>
    /// <para>
    /// Since #310 this is also the one step a broadcast family passes through: the lines heard here
    /// feed every driver's <c>Overhear</c>, and the <c>*CLS</c> sent at the end goes out regardless
    /// of who was talking — the one write such a family ever receives (§7.2's scope note).
    /// </para>
    /// </remarks>
    public async Task<Transaction> SynchroniseAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        long startedAt = _timeProvider.GetTimestamp();
        List<string> lines = [];

        using CancellationTokenSource timeoutSource = new(timeout, _timeProvider);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            string? promptStatus = await ReadUntilPromptAsync(lines, linked.Token).ConfigureAwait(false);
            await ClearStatusAsync(cancellationToken).ConfigureAwait(false);

            return new Transaction
            {
                Command = ConnectLabel,
                Outcome = TransactionOutcome.Completed,
                Lines = lines,
                EchoDiscarded = false,
                Elapsed = _timeProvider.GetElapsedTime(startedAt),
                PromptStatus = promptStatus,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Silence is a perfectly good answer: this receiver announces itself, a sibling model
            // may not, and neither case is a failure to connect.
            await ClearStatusAsync(cancellationToken).ConfigureAwait(false);

            return new Transaction
            {
                Command = ConnectLabel,
                Outcome = TransactionOutcome.TimedOut,
                Lines = lines,
                EchoDiscarded = false,
                Elapsed = _timeProvider.GetElapsedTime(startedAt),
            };
        }
        catch (Exception ex) when (TransportFaults.IsTransportFault(ex))
        {
            TransportFault fault = TransportFaults.Classify(ex);
            TransportLog.TransactionFaulted(_logger, ConnectLabel, fault, ex);

            return new Transaction
            {
                Command = ConnectLabel,
                Outcome = TransactionOutcome.Faulted,
                Lines = lines,
                EchoDiscarded = false,
                Elapsed = _timeProvider.GetElapsedTime(startedAt),
                Fault = fault,
                FaultMessage = ex.Message,
            };
        }
    }

    /// <summary>
    /// Sends the status-clear command and throws the answer away, twice if the first one is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first command after the port opens is unreliable on this hardware. Asserting DTR and RTS
    /// puts a glitch on the line that the receiver reads as a character, and it answers the next
    /// thing it is asked with <c>E-362&gt;</c> — SCPI's framing error — having discarded that command
    /// unexecuted. Left alone, the cost is a mystifying failure on whatever the app happens to send
    /// first, which during auto-detect is the identity query that decides whether a receiver is
    /// there at all.
    /// </para>
    /// <para>
    /// So the connect sequence spends the glitch deliberately, on the one command in tier S (§8.2)
    /// whose whole purpose is to clear status and whose response nobody wants. Twice, because the
    /// first attempt is the one being sacrificed.
    /// </para>
    /// </remarks>
    private async Task ClearStatusAsync(CancellationToken cancellationToken)
    {
        Transaction cleared = await ExecuteAsync(ClearStatusCommand, TransactionTimeouts.AutoDetectProbe, cancellationToken)
            .ConfigureAwait(false);

        // ErrorQueueNotEmpty is the right test here, unusually: this wants "not clean yet", not
        // "that command failed". Spending the glitch is done when the queue is empty.
        if (!cleared.Succeeded || cleared.ErrorQueueNotEmpty)
        {
            await ExecuteAsync(ClearStatusCommand, TransactionTimeouts.AutoDetectProbe, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Notes that a reply was abandoned part-read, so the next command realigns before it is sent.
    /// </summary>
    /// <remarks>
    /// <b>Only when something had already arrived.</b> A transaction that received nothing at all
    /// was talking to a device that is silent or gone, and there is no tail to drain — waiting for
    /// a prompt that was never coming would add a second timeout to every one of §7.2's three
    /// consecutive failures before it reconnects, which is exactly the wrong place to spend time.
    /// </remarks>
    private void NeedsResynchronising(int linesReceived, TimeSpan budget)
    {
        if (linesReceived > 0)
        {
            _resynchroniseWithin = budget;
        }
    }

    /// <summary>
    /// Reads and discards up to the next prompt, so a half-read reply cannot be mistaken for the
    /// next answer (#209).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why <see cref="DiscardStaleInput"/> is not enough.</b> That drains what has already
    /// arrived. The bytes that cause this are the ones that have <i>not</i> — a 15 kB diagnostic log
    /// is sixteen seconds of wire at 9600 baud, so a read abandoned three seconds in leaves thirteen
    /// seconds of reply still to come. Discarding at the moment the next command is written clears
    /// none of it; it arrives afterwards and is read as that command's answer.
    /// </para>
    /// <para>
    /// That is what #209 was. Navigating away from the Diagnostics page cancels its log read, and
    /// the next three polls parsed the tail: one stored an EFC reading as a sync state, and two
    /// wrote time intervals of two and three <i>seconds</i> into <c>trend.db</c>, where three bad
    /// samples out of 12,488 made every chart over a seven-day window unreadable.
    /// </para>
    /// <para>
    /// <b>The caller's token is deliberately not honoured here.</b> Cancelling meant "stop waiting
    /// for my answer", not "leave the link misaligned for whoever asks next" — and a resynchronise
    /// that could itself be cancelled would fix nothing. It is bounded instead by the budget of the
    /// transaction that was abandoned, which is the longest its remaining reply can take, and a link
    /// that has actually died surfaces as a transport fault rather than as a wait.
    /// </para>
    /// <para>
    /// The cost lands on the next command rather than on the cancel, which is the right place: the
    /// wire is busy and nothing can be sent until it is clear. Doing it inline would make cancelling
    /// a read take as long as not cancelling it.
    /// </para>
    /// </remarks>
    private async Task ResynchroniseAsync()
    {
        if (_resynchroniseWithin is not TimeSpan budget)
        {
            return;
        }

        // Cleared first: a resynchronise that faults must not leave the flag set and repeat itself
        // on every subsequent command.
        _resynchroniseWithin = null;

        List<string> discarded = [];

        using CancellationTokenSource deadline = new(budget, _timeProvider);

        try
        {
            await ReadUntilPromptAsync(discarded, deadline.Token).ConfigureAwait(false);
            TransportLog.Resynchronised(_logger, discarded.Count);
        }
        catch (OperationCanceledException)
        {
            // The tail never ended. Nothing more can be done here and the next transaction's own
            // timeout is the backstop — but say so, because a link that reaches this is one whose
            // answers are not to be trusted.
            TransportLog.ResynchroniseTimedOut(_logger, budget.TotalMilliseconds, discarded.Count);
        }
        catch (Exception exception) when (TransportFaults.IsTransportFault(exception))
        {
            // The link died while realigning. Swallowed on purpose: the command that follows is
            // about to touch the same port and will report it as a Faulted transaction, which is
            // the shape every caller already handles. Throwing a raw transport exception from here
            // would make one path report failure differently from all the others.
            TransportLog.ResynchroniseTimedOut(_logger, budget.TotalMilliseconds, discarded.Count);
        }
    }

    /// <summary>
    /// Empties the driver buffer and the pipe before writing.
    /// </summary>
    /// <remarks>
    /// On §7.2's query/response link the receiver only speaks when spoken to, so anything already
    /// waiting is the late tail of a transaction that timed out. Leaving it in place would prepend
    /// one dead response to every subsequent one — a single timeout silently misaligning the session
    /// for as long as it stays up.
    /// </remarks>
    private void DiscardStaleInput()
    {
        _transport.DiscardInput();

        PipeReader reader = _transport.Input;
        long discarded = 0;

        while (reader.TryRead(out ReadResult result))
        {
            discarded += result.Buffer.Length;
            reader.AdvanceTo(result.Buffer.End);

            if (result.IsCompleted || result.IsCanceled || result.Buffer.IsEmpty)
            {
                break;
            }
        }

        if (discarded > 0)
        {
            TransportLog.StaleInputDiscarded(_logger, discarded);
        }
    }

    /// <summary>
    /// Reads until the prompt, appending each complete line to <paramref name="lines"/> and
    /// returning the prompt's error token, or null when the prompt was the ordinary one.
    /// </summary>
    private async Task<string?> ReadUntilPromptAsync(List<string> lines, CancellationToken cancellationToken)
    {
        PipeReader reader = _transport.Input;

        // Set when a read ends on a CR whose LF has not arrived yet. Without it the pair is counted
        // as two line endings and a blank line appears between two real ones — silent corruption,
        // and near-certain at 9600 baud where a 1900-byte screen is split into dozens of reads.
        bool pendingLineFeed = false;

        // Belt and braces on the timeout. The token alone is enough for a Pipe — which is what both
        // transports expose, SerialTransport deliberately so — but cancelling the pending read as
        // well means a reader implementation that treats its token loosely still cannot hang a
        // transaction past its §7.2 deadline.
        using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
            static state => ((PipeReader)state!).CancelPendingRead(), reader);

        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            SequencePosition consumed = buffer.Start;
            SequencePosition examined = buffer.Start;
            bool promptFound = false;
            string? promptStatus = null;

            try
            {
                if (!result.IsCanceled)
                {
                    promptFound = TryReadTransaction(
                        buffer, lines, ref pendingLineFeed, out consumed, out examined, out promptStatus);
                }
            }
            finally
            {
                // Always hand the buffer back, even on the way out through an exception: a pipe with a
                // read checked out cannot be read again, which would strand the reconnect path.
                reader.AdvanceTo(consumed, examined);
            }

            if (promptFound)
            {
                return promptStatus;
            }

            if (result.IsCanceled)
            {
                cancellationToken.ThrowIfCancellationRequested();
                continue;
            }

            if (result.IsCompleted)
            {
                throw new TransportException(
                    TransportFault.DeviceRemoved,
                    $"{_transport.Description} closed with no prompt after {lines.Count} line(s).");
            }
        }
    }

    /// <summary>
    /// Extracts every complete line in <paramref name="buffer"/> and reports whether the transaction
    /// terminated.
    /// </summary>
    /// <param name="buffer">The bytes the pipe has so far.</param>
    /// <param name="lines">Accumulates across reads, so a response spanning many buffers builds up in order.</param>
    /// <param name="pendingLineFeed">
    /// Carries the half-seen CRLF from one read to the next: set when a read ends on a CR, cleared
    /// by the LF that follows it.
    /// </param>
    /// <param name="consumed">How far the pipe may discard: past the prompt, or past the last complete line.</param>
    /// <param name="examined">
    /// How far this pass looked. When no prompt was found this is the end of the buffer, so the next
    /// read waits for genuinely new bytes rather than spinning on the same partial sentinel.
    /// </param>
    /// <param name="promptStatus">The prompt's error token when it carried one, otherwise null.</param>
    private static bool TryReadTransaction(
        in ReadOnlySequence<byte> buffer,
        List<string> lines,
        ref bool pendingLineFeed,
        out SequencePosition consumed,
        out SequencePosition examined,
        out string? promptStatus)
    {
        promptStatus = null;
        SequenceReader<byte> reader = new(buffer);

        if (pendingLineFeed && reader.TryPeek(out byte leading))
        {
            if (leading == Lf)
            {
                reader.Advance(1);
            }

            pendingLineFeed = false;
        }

        while (reader.TryReadToAny(out ReadOnlySequence<byte> line, LineDelimiters, advancePastDelimiter: false))
        {
            reader.TryPeek(out byte delimiter);
            reader.Advance(1);

            // CRLF is one line ending, not two. Reading to either delimiter and then stepping over a
            // following LF keeps a CR-only firmware working without inventing blank lines on a CRLF one.
            if (delimiter == Cr)
            {
                if (reader.TryPeek(out byte next))
                {
                    if (next == Lf)
                    {
                        reader.Advance(1);
                    }
                }
                else
                {
                    // The LF, if there is one, is in the next read. Waiting for it here instead would
                    // stall a firmware that ends lines with a bare CR.
                    pendingLineFeed = true;
                }
            }

            lines.Add(Decode(line));
        }

        // Whatever is left has no CR or LF in it — the loop above consumed every one. The prompt
        // never contains a line ending, so the prompt, if it has arrived, is exactly this tail.
        consumed = reader.Position;
        examined = buffer.End;

        ReadOnlySequence<byte> unread = reader.UnreadSequence;
        if (unread.Length is 0 or > MaxPromptLength)
        {
            return false;
        }

        if (!TryMatchPrompt(Decode(unread), out int promptLength, out string? status))
        {
            return false;
        }

        promptStatus = status;
        consumed = unread.GetPosition(promptLength);
        examined = consumed;
        return true;
    }

    /// <summary>
    /// Matches a complete prompt at the start of <paramref name="tail"/>.
    /// </summary>
    /// <param name="tail">The unterminated remainder of the buffer, which contains no line ending.</param>
    /// <param name="promptLength">How many characters the prompt occupies.</param>
    /// <param name="status">
    /// The error token when the receiver is reporting one — <c>E-230</c> and the like — or null for
    /// the ordinary prompt.
    /// </param>
    /// <remarks>
    /// <para>
    /// §7.2's prompt grammar has two forms, both observed on a Z3805A running firmware 1.01.03-A —
    /// the literal <c>"scpi&gt; "</c> the section used to give never matches at all:
    /// </para>
    /// <para>
    /// The ordinary prompt is <c>"scpi &gt; "</c>, with a space before the bracket. While the error
    /// queue is not empty the word is replaced entirely, <c>"E-230&gt; "</c> and the like, with no
    /// space — the prompt doubles as the queue indicator, not as a verdict on the last command. A
    /// command that is rejected answers with *only* that prompt, so a protocol looking for the
    /// literal string waits out its full timeout on every failed command and then does it again on
    /// the next one.
    /// </para>
    /// <para>
    /// Matching is deliberately narrow rather than "anything ending in &gt;": the tail is also where
    /// a half-arrived response line sits, and a status screen line containing a bracket must not be
    /// mistaken for the end of the transaction.
    /// </para>
    /// </remarks>
    private static bool TryMatchPrompt(ReadOnlySpan<char> tail, out int promptLength, out string? status)
    {
        promptLength = 0;
        status = null;

        int index = 0;
        while (index < tail.Length && tail[index] == ' ')
        {
            index++;
        }

        int tokenStart = index;
        if (tail[index..].StartsWith(PromptWord, StringComparison.Ordinal))
        {
            index += PromptWord.Length;
        }
        else if (tail[index..].StartsWith(ErrorPromptPrefix, StringComparison.Ordinal))
        {
            index += ErrorPromptPrefix.Length;
            int digits = index;
            while (index < tail.Length && char.IsAsciiDigit(tail[index]))
            {
                index++;
            }

            if (index == digits)
            {
                // "E-" with nothing after it yet: either a truncated prompt or not one at all. Both
                // mean wait, so neither needs distinguishing.
                return false;
            }
        }
        else
        {
            return false;
        }

        int tokenEnd = index;
        while (index < tail.Length && tail[index] == ' ')
        {
            index++;
        }

        if (index >= tail.Length || tail[index] != '>')
        {
            return false;
        }

        index++;
        if (index < tail.Length && tail[index] == ' ')
        {
            index++;
        }

        promptLength = index;
        ReadOnlySpan<char> token = tail[tokenStart..tokenEnd];
        status = token.Equals(PromptWord, StringComparison.Ordinal) ? null : token.ToString();
        return true;
    }

    /// <summary>
    /// Drops the leading line when it is the command coming back, and reports whether it was.
    /// </summary>
    /// <remarks>
    /// §7.2: detect echo by comparing the first received line to the transmitted line. Comparing is
    /// the point — <c>FDUPlex</c> is a device setting this app deliberately does not change, so both
    /// states have to work, on every transaction, without configuration.
    /// </remarks>
    private static bool TryDiscardEcho(string command, List<string> lines)
    {
        if (lines.Count == 0 || !string.Equals(lines[0].Trim(), command, StringComparison.Ordinal))
        {
            return false;
        }

        lines.RemoveAt(0);
        return true;
    }

    /// <summary>
    /// Decodes device bytes as Latin-1 rather than ASCII: it is the one single-byte encoding that
    /// never substitutes, so a stray high byte from line noise reaches the parser as a character it
    /// can reject instead of as a silent <c>?</c>.
    /// </summary>
    private static string Decode(in ReadOnlySequence<byte> sequence) => sequence.IsSingleSegment
        ? Encoding.Latin1.GetString(sequence.FirstSpan)
        : Encoding.Latin1.GetString(sequence.ToArray());
}
