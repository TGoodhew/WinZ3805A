using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;

namespace WinZ3805A.Device.Transport;

/// <summary>
/// A receiver made of bytes. Feeds captured or scripted output through the same
/// <see cref="PipeReader"/> the real port uses, so <see cref="LineProtocol"/> is exercised in full
/// with no hardware attached (§15 step 1).
/// </summary>
/// <remarks>
/// <para>
/// It lives in the device library rather than the test project for two reasons. §15 makes replaying
/// fixtures part of step 1's deliverable, alongside the transport it stands in for; and the design
/// work in §9 and §10 has to proceed on machines with no Z3805A on the desk, which needs a device
/// that answers.
/// </para>
/// <para>
/// Two ways to drive it. Give the constructor a responder and it answers every command by itself,
/// optionally in <see cref="ChunkSize"/>-byte pieces so the response genuinely spans several reads.
/// Or leave it silent and call <see cref="ReadCommandAsync"/> then <see cref="EmitAsync(string, CancellationToken)"/>
/// to place each byte exactly where the test wants it — which is how the prompt gets split down the
/// middle on purpose.
/// </para>
/// </remarks>
public sealed class FakeTransport : ITransport
{
    private readonly Channel<string> _commands = Channel.CreateUnbounded<string>();
    private readonly List<string> _commandsWritten = [];
    private readonly Func<string, string?>? _responder;
    private readonly StringBuilder _partialCommand = new();

    /// <summary>Guards the lazy construction of <see cref="Pipe"/>, which two threads race for (#324).</summary>
    private readonly Lock _pipeGate = new();

    /// <summary>
    /// Cancels a response pump that is paused at the write threshold when the transport is disposed
    /// (#381). Without it, disposal waits for a drain that is never coming.
    /// </summary>
    private readonly CancellationTokenSource _disposing = new();

    private Pipe? _pipe;
    private Task _pump = Task.CompletedTask;
    private bool _open;
    private bool _disposed;
    private int _discardCount;

    /// <summary>Creates a transport that answers nothing; the test emits every byte itself.</summary>
    public FakeTransport()
    {
    }

    /// <summary>
    /// Creates a transport that answers each command with <paramref name="responder"/>'s return
    /// value, or with the prompt alone when it returns null — the shape §7.2 gives a rejected
    /// command, and the shape a setter has whether it worked or not.
    /// </summary>
    public FakeTransport(Func<string, string?> responder)
    {
        ArgumentNullException.ThrowIfNull(responder);
        _responder = responder;
    }

    /// <summary>A transport that answers every command with the same text.</summary>
    public static FakeTransport Answering(string response) => new(_ => response);

    /// <summary>
    /// Whether the device echoes what it is sent. True by default because the manual's default is
    /// <c>FDUPlex ON</c> (§7.2) — the bench Z3805A echoes nothing, so both settings occur; set
    /// false to prove the protocol survives either.
    /// </summary>
    public bool EchoCommands { get; init; } = true;

    /// <summary>Whether to terminate each response with the prompt sentinel. False models a device that has stopped answering.</summary>
    public bool EmitPrompt { get; init; } = true;

    /// <summary>
    /// The prompt text. The default is what a Z3805A running firmware 1.01.03-A actually emits, and
    /// the spelling §7.2 has given since its 21 Aug 2026 correction — the space before the bracket
    /// is real.
    /// </summary>
    public string Prompt { get; init; } = "scpi > ";

    /// <summary>When true the responder is never called and nothing is written back, so the transaction times out.</summary>
    public bool Silent { get; init; }

    /// <summary>
    /// Bytes per write when responding, or 0 for one write. Small values fragment the response the
    /// way 9600 baud does, which is the case the prompt-sentinel scan exists to survive.
    /// </summary>
    public int ChunkSize { get; init; }

    /// <summary>
    /// Makes every emit wait until the reader has consumed it, so each one lands as its own read
    /// rather than being coalesced with the next. This is what turns "the boundary usually falls
    /// somewhere interesting" into a test that puts it exactly where it wants it.
    /// </summary>
    /// <remarks>
    /// Only safe when every emit ends on something the protocol can consume. Emitting a partial
    /// line or half a prompt under this option deadlocks the writer, because the protocol correctly
    /// holds those bytes back waiting for the rest.
    /// </remarks>
    public bool WaitForReaderToConsume { get; init; }

    /// <summary>
    /// How long a paused write may wait for the reader before it is called a deadlock (#449).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the difference between a test that fails and a test host that dies.</b> With
    /// <see cref="WaitForReaderToConsume"/> the pipe has a one-byte <c>pauseWriterThreshold</c>, so
    /// an emit does not return until something drains it. When the reader has stopped — the session
    /// faulted, the poller was stopped, a transaction was abandoned — nothing ever will, and an
    /// unbounded emit waits for the life of the process.
    /// </para>
    /// <para>
    /// It cost three CI runs to find, because the failure has no failing test in it: the suite goes
    /// green in ten seconds, the host is killed five minutes later, and <c>--blame-hang</c> names
    /// "the test running when the crash occurred" — a different one each time, with its own caveat
    /// that it may not be the source. Only the sequence file, which records
    /// <c>Completed="False"</c>, names the test. A bound here turns all of that into one assertion
    /// message.
    /// </para>
    /// <para>
    /// Generous on purpose: this is a deadlock detector, not a performance assertion. A slow,
    /// contended runner must never trip it, and the races it catches never resolve at any length.
    /// </para>
    /// </remarks>
    public TimeSpan PausedWriteTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Every command line received, in order.</summary>
    public IReadOnlyList<string> CommandsWritten => _commandsWritten;

    /// <summary>How many times <see cref="DiscardInput"/> has been called.</summary>
    public int DiscardCount => _discardCount;

    /// <inheritdoc />
    public string Description => "FakeTransport";

    /// <inheritdoc />
    public bool IsOpen => _open && !_disposed;

    /// <inheritdoc />
    public PipeReader Input
    {
        get
        {
            EnsureOpen();
            return Pipe.Reader;
        }
    }

    /// <inheritdoc />
    public ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _open = true;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        _partialCommand.Append(Encoding.Latin1.GetString(buffer.Span));

        while (TryTakeCommandLine(out string? command))
        {
            _commandsWritten.Add(command);
            await _commands.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);

            if (_responder is not null && !Silent)
            {
                StartResponse(command);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Counted rather than acted on. There is no driver buffer to flush here, and the pipe itself is
    /// drained by <see cref="LineProtocol"/> immediately afterwards, so the count is what a test
    /// asserts on.
    /// </remarks>
    public void DiscardInput() => Interlocked.Increment(ref _discardCount);

    /// <summary>Waits for the next command the protocol writes.</summary>
    public ValueTask<string> ReadCommandAsync(CancellationToken cancellationToken = default)
        => _commands.Reader.ReadAsync(cancellationToken);

    /// <summary>Sends raw text to the reader, exactly as given — no echo, no prompt, no terminator.</summary>
    public ValueTask EmitAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return EmitAsync(Encoding.Latin1.GetBytes(text), cancellationToken);
    }

    /// <summary>Sends raw bytes to the reader.</summary>
    /// <exception cref="TimeoutException">
    /// When <see cref="WaitForReaderToConsume"/> is set and nothing drained the write within
    /// <see cref="PausedWriteTimeout"/>. See that property for why this is an exception rather than
    /// a wait (#449).
    /// </exception>
    public async ValueTask EmitAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        EnsureOpen();

        if (!WaitForReaderToConsume)
        {
            // Nothing to deadlock on: the writer is never paused, so the write completes at once.
            await Pipe.Writer.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            return;
        }

        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(PausedWriteTimeout);

        try
        {
            await Pipe.Writer.WriteAsync(bytes, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller's own token is untouched, so this is the budget and nothing else. Rethrown
            // as a TimeoutException rather than a cancellation because no caller asked to stop:
            // a cancellation would be caught by the very `catch (OperationCanceledException)` blocks
            // these tests use to mean "the transaction was abandoned", and the deadlock would be
            // swallowed as an expected outcome.
            throw new TimeoutException(
                $"A FakeTransport emit of {bytes.Length} byte(s) was never consumed within "
                + $"{PausedWriteTimeout.TotalSeconds:N0}s. WaitForReaderToConsume pauses the writer "
                + "after one byte, so nothing is reading this transport — the reader has stopped, "
                + "or the emit ended mid-line and the protocol is correctly holding it back.");
        }
    }

    /// <summary>
    /// Ends the byte stream with an exception, modelling the adapter being pulled mid-transaction —
    /// the P0-14 case, and the reason §6.4 wraps every read.
    /// </summary>
    public void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Pipe.Writer.Complete(exception);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _open = false;
        _commands.Writer.TryComplete();

        // ------------------------------------------------------------------------------------
        // CANCEL THE PUMP BEFORE WAITING FOR IT (#381). With WaitForReaderToConsume the pipe has a
        // one-byte pauseWriterThreshold, so a response stops after its first chunk and waits to be
        // drained. Nothing drains it once the session or listener under test has been disposed -
        // and in a test that is the NORMAL order, since those are declared after the transport and
        // so dispose first. Awaiting the pump then never returned: not a failed test, a hung one,
        // inside `await using`, which is why the run it stalled produced no output at all.
        //
        // Cancelling the writer's token rather than completing the reader, deliberately: the
        // protocol may still have a read in flight on the other end, and completing a reader from
        // underneath it trades this hang for an exception somewhere less obvious.
        // ------------------------------------------------------------------------------------
        await _disposing.CancelAsync().ConfigureAwait(false);

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A response was still being pumped, and is now abandoned. That is what disposal means.
        }
        catch (InvalidOperationException)
        {
            // The writer was completed by Fail() while a response was still being pumped.
        }

        if (_pipe is not null)
        {
            await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
            await _pipe.Reader.CompleteAsync().ConfigureAwait(false);
        }

        _disposing.Dispose();
    }

    /// <summary>
    /// The pipe standing in for the wire. Created on first use because
    /// <see cref="WaitForReaderToConsume"/> is an init-only property and so is not known until
    /// after the field initialisers have run.
    /// </summary>
    /// <remarks>
    /// <b>Under a lock, because the first use comes from two threads at once (#324).</b> This was
    /// <c>_pipe ??= new Pipe(…)</c>, which is not atomic: a reader on one thread and a writer on
    /// another can both find the field null and each construct a pipe, and each then goes on using
    /// <i>its own</i> — the writer writing into a pipe nobody reads, the reader waiting on a pipe
    /// nobody writes to. <see cref="BroadcastListener"/> does exactly that, taking
    /// <see cref="Input"/> on the loop it starts on the thread pool while the test writes from its
    /// own thread, so the two calls are genuinely concurrent.
    /// <para>
    /// With <see cref="WaitForReaderToConsume"/> set, losing that race deadlocks: the writer pauses
    /// at the one-byte threshold and is never drained. Without it the write simply vanishes and the
    /// assertion fails instead. Measured at three hangs in fifteen runs before this lock and none
    /// in fifteen after, which matches the roughly one-in-six the full test suite was showing.
    /// </para>
    /// </remarks>
    private Pipe Pipe
    {
        get
        {
            lock (_pipeGate)
            {
                return _pipe ??= new Pipe(WaitForReaderToConsume
                    ? new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 1)
                    : new PipeOptions());
            }
        }
    }

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_open)
        {
            throw new TransportException(TransportFault.NotOpen, "FakeTransport is not open.");
        }
    }

    private bool TryTakeCommandLine(out string command)
    {
        // The protocol terminates with CRLF (§7.2); LF alone is accepted so a hand-written test
        // string does not have to be pedantic about it.
        for (int index = 0; index < _partialCommand.Length; index++)
        {
            if (_partialCommand[index] is not ('\r' or '\n'))
            {
                continue;
            }

            command = _partialCommand.ToString(0, index);
            int skip = index + 1;
            if (_partialCommand[index] == '\r' && skip < _partialCommand.Length && _partialCommand[skip] == '\n')
            {
                skip++;
            }

            _partialCommand.Remove(0, skip);
            return true;
        }

        command = string.Empty;
        return false;
    }

    private void StartResponse(string command)
    {
        StringBuilder response = new();

        if (EchoCommands)
        {
            response.Append(command).Append("\r\n");
        }

        string? body = _responder?.Invoke(command);
        if (!string.IsNullOrEmpty(body))
        {
            response.Append(Normalise(body));
        }

        if (EmitPrompt)
        {
            response.Append(Prompt);
        }

        byte[] payload = Encoding.Latin1.GetBytes(response.ToString());
        Task previous = _pump;
        _pump = PumpAsync(previous, payload);
    }

    private async Task PumpAsync(Task previous, byte[] payload)
    {
        await previous.ConfigureAwait(false);

        int chunk = ChunkSize > 0 ? ChunkSize : payload.Length;
        for (int offset = 0; offset < payload.Length; offset += chunk)
        {
            int length = Math.Min(chunk, payload.Length - offset);
            // The token is what makes disposal terminate rather than wait (#381): with
            // WaitForReaderToConsume this write pauses until the reader drains it, and after
            // disposal there is no reader left to.
            await Pipe.Writer.WriteAsync(payload.AsMemory(offset, length), _disposing.Token)
                .ConfigureAwait(false);

            // Yield between writes so the reader observes separate reads rather than one coalesced
            // buffer. Without this the fragmentation the chunk size asks for never actually happens.
            await Task.Yield();
        }
    }

    /// <summary>Gives the response body CRLF endings and a terminating CRLF, whatever it arrived with.</summary>
    private static string Normalise(string body)
    {
        string normalised = body.ReplaceLineEndings("\r\n");
        return normalised.EndsWith("\r\n", StringComparison.Ordinal) ? normalised : normalised + "\r\n";
    }
}
