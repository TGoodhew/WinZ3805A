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
/// <b>The device echoes.</b> It defaults to <c>FDUPlex ON</c> and sends every character back. The
/// echo is *detected* by comparing the first line received to the line transmitted, never assumed —
/// a session that assumes echo-on eats the first line of every response the day someone turns it off.
/// </para>
/// <para>
/// <b>The terminator is a prompt, not a newline.</b> A transaction ends at <c>scpi&gt;</c>, which is
/// what makes a setter (prompt only) and a multi-line block (~1900 bytes for the status screen) the
/// same shape of read. <c>ReadLine</c> cannot express that.
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

    /// <summary>
    /// The prompt, without its trailing space. §7.2 describes the sentinel as <c>"scpi&gt; "</c>, but
    /// ending the transaction on the <c>&gt;</c> means a firmware that omits the space still works,
    /// and the orphaned space is cleared by the stale-input discard at the start of the next
    /// transaction.
    /// </summary>
    private static ReadOnlySpan<byte> PromptSentinel => "scpi>"u8;

    /// <summary>
    /// CR and LF. §6.4 nominates <see cref="SearchValues{T}"/> for this scan, but .NET 10 ships no
    /// <see cref="SequenceReader{T}"/> overload that accepts one, and §7.2 mandates
    /// <see cref="SequenceReader{T}"/> for sentinel detection. The span overloads win the conflict:
    /// they are what exists, and a two-value <c>IndexOfAny</c> is already vectorised, so
    /// <see cref="SearchValues{T}"/> — which earns its keep by amortising set preprocessing — would
    /// buy nothing here even if it fitted.
    /// </summary>
    private static ReadOnlySpan<byte> LineDelimiters => "\r\n"u8;

    private readonly ITransport _transport;
    private readonly TimeProvider _timeProvider;
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

            await ReadUntilPromptAsync(lines, linked.Token).ConfigureAwait(false);
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
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The linked source fired but the caller did not cancel, so this is the timeout. Whatever
            // arrived is kept: a truncated response is the most useful thing Diagnostics can show.
            echoDiscarded = TryDiscardEcho(sent, lines);
            TransportLog.TransactionTimedOut(_logger, sent, timeout.TotalMilliseconds, lines.Count);

            return new Transaction
            {
                Command = sent,
                Outcome = TransactionOutcome.TimedOut,
                Lines = lines,
                EchoDiscarded = echoDiscarded,
                Elapsed = _timeProvider.GetElapsedTime(startedAt),
            };
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
    /// Empties the driver buffer and the pipe before writing.
    /// </summary>
    /// <remarks>
    /// The receiver only speaks when spoken to, so anything already waiting is the late tail of a
    /// transaction that timed out. Leaving it in place would prepend one dead response to every
    /// subsequent one — a single timeout silently misaligning the session for as long as it stays up.
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

    /// <summary>Reads until the prompt sentinel, appending each complete line to <paramref name="lines"/>.</summary>
    private async Task ReadUntilPromptAsync(List<string> lines, CancellationToken cancellationToken)
    {
        PipeReader reader = _transport.Input;

        // Set when a read ends on a CR whose LF has not arrived yet. Without it the pair is counted
        // as two line endings and a blank line appears between two real ones — silent corruption,
        // and near-certain at 9600 baud where a 1900-byte screen is split into dozens of reads.
        bool pendingLineFeed = false;

        // SerialPort's base stream does not honour a CancellationToken on a read already in flight, so
        // the token alone cannot enforce a timeout. Cancelling the pending pipe read does: ReadAsync
        // returns with IsCanceled set even while the underlying stream read is still outstanding.
        using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
            static state => ((PipeReader)state!).CancelPendingRead(), reader);

        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            SequencePosition consumed = buffer.Start;
            SequencePosition examined = buffer.Start;
            bool promptFound = false;

            try
            {
                if (!result.IsCanceled)
                {
                    promptFound = TryReadTransaction(buffer, lines, ref pendingLineFeed, out consumed, out examined);
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
                return;
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
    private static bool TryReadTransaction(
        in ReadOnlySequence<byte> buffer,
        List<string> lines,
        ref bool pendingLineFeed,
        out SequencePosition consumed,
        out SequencePosition examined)
    {
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

        // The prompt carries no line ending, so it is always in the unterminated tail.
        SequenceReader<byte> tail = new(reader.UnreadSequence);
        if (!tail.TryReadTo(out ReadOnlySequence<byte> beforePrompt, PromptSentinel, advancePastDelimiter: true))
        {
            consumed = reader.Position;
            examined = buffer.End;
            return false;
        }

        // Some firmware ends the last response line at the prompt rather than at a CRLF; keep it.
        if (!beforePrompt.IsEmpty)
        {
            string trailing = Decode(beforePrompt);
            if (!string.IsNullOrWhiteSpace(trailing))
            {
                lines.Add(trailing);
            }
        }

        if (tail.TryPeek(out byte space) && space == (byte)' ')
        {
            tail.Advance(1);
        }

        consumed = tail.Position;
        examined = consumed;
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
