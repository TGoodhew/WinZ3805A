using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;

using WinZ3805A.Device.Transport;

namespace WinZ3805A.Tests.Services;

/// <summary>How a <see cref="ControllableTransport"/> behaves for the next command.</summary>
public enum TransportBehaviour
{
    /// <summary>Answer normally, response then prompt.</summary>
    Answering = 0,

    /// <summary>Accept the command and say nothing, so the caller has to time out.</summary>
    Silent,

    /// <summary>Throw <see cref="IOException"/> on write, as a pulled adapter does.</summary>
    Faulting,
}

/// <summary>
/// A transport whose behaviour can be changed <em>after</em> it has been opened.
/// </summary>
/// <remarks>
/// <para>
/// <c>FakeTransport</c> covers the transport's own tests well, but its <c>Silent</c> and prompt
/// switches are <c>init</c>-only, which makes one scenario unreachable: a link that answers during
/// connect and then stops. That is not an edge case for the session layer — it is exactly what a
/// USB adapter being pulled looks like, and it is the whole of P0-14.
/// </para>
/// <para>
/// Kept in the test project rather than added to the Device library: nothing in the application
/// needs a transport that can be sabotaged mid-session.
/// </para>
/// </remarks>
public sealed class ControllableTransport : ITransport
{
    /// <summary>What the receiver ends every line with (§7.2).</summary>
    private const string LineEnding = "\r\n";

    private readonly Func<string, string?> _responder;
    private readonly Pipe _pipe = new();
    private readonly StringBuilder _partial = new();
    private readonly List<string> _written = [];
    private readonly Channel<string> _writes = Channel.CreateUnbounded<string>();

    private bool _open;
    private bool _disposed;

    /// <summary>Creates a transport that answers via <paramref name="responder"/>.</summary>
    public ControllableTransport(Func<string, string?> responder)
    {
        ArgumentNullException.ThrowIfNull(responder);
        _responder = responder;
    }

    /// <summary>What the transport does with the next command. Changeable at any time.</summary>
    public TransportBehaviour Behaviour { get; set; } = TransportBehaviour.Answering;

    /// <summary>
    /// Waits until the next command reaches the wire, and returns it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "The command is in flight" is the state several tests need before doing something to the
    /// session underneath it — tearing it down, ending it, disconnecting. They approximated it with
    /// <c>await Task.Delay(50)</c>, which is a guess about how long the pump takes rather than a
    /// statement about where the command got to (#326).
    /// </para>
    /// <para>
    /// Every write is queued, so a caller that asks after the write has already happened is handed
    /// it rather than left waiting for the next one — the ordering is recorded, not raced for.
    /// </para>
    /// </remarks>
    public async Task<string> NextWriteAsync(TimeSpan timeout)
    {
        using CancellationTokenSource giveUp = new(timeout);
        return await _writes.Reader.ReadAsync(giveUp.Token);
    }

    /// <summary>The prompt this device ends a transaction with. The real unit sends "scpi &gt; ".</summary>
    public string Prompt { get; init; } = "scpi > ";

    /// <summary>
    /// An alternative prompt for particular commands, or null to use <see cref="Prompt"/> always.
    /// </summary>
    /// <remarks>
    /// The receiver replaces the prompt word with an error token when the last command failed —
    /// <c>"E-230&gt; "</c> rather than <c>"scpi &gt; "</c> — so the prompt doubles as the rejection
    /// signal. A test that needs a refusal has to be able to produce that, and it has to be per
    /// command: the real unit answers one query normally and refuses the next in the same session,
    /// which is exactly the behaviour #155 is about.
    /// </remarks>
    public Func<string, string?>? PromptFor { get; init; }

    /// <summary>
    /// What the device announces, unprompted, when DTR is asserted — followed by a prompt.
    /// </summary>
    /// <remarks>
    /// Modelling this is not decoration. <c>LineProtocol.SynchroniseAsync</c> opens a session by
    /// <em>reading</em> before it writes anything, precisely because the real unit announces itself
    /// and leaves the session one reply behind if that banner is not absorbed. A transport that only
    /// speaks when spoken to therefore leaves the connect path with nothing to read and no way out
    /// but the timeout — which on a pinned clock never arrives. Emitting at least a prompt here is
    /// what makes a session test finish rather than hang.
    /// </remarks>
    public string? Banner { get; init; }

    /// <summary>Every command line received, in order.</summary>
    public IReadOnlyList<string> CommandsWritten => _written;

    /// <summary>How many times the transport was opened, which counts reconnect attempts.</summary>
    public int OpenCount { get; private set; }

    /// <inheritdoc />
    public string Description => "ControllableTransport";

    /// <inheritdoc />
    public bool IsOpen => _open && !_disposed;

    /// <inheritdoc />
    public PipeReader Input =>
        IsOpen ? _pipe.Reader : throw new TransportException(TransportFault.NotOpen, "Not open.");

    /// <inheritdoc />
    public ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _open = true;
        OpenCount++;

        string greeting = Banner is null ? Prompt : Banner + LineEnding + Prompt;
        return WriteToPipeAsync(greeting, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!IsOpen)
        {
            throw new TransportException(TransportFault.NotOpen, "Not open.");
        }

        if (Behaviour == TransportBehaviour.Faulting)
        {
            throw new IOException("The device is not connected.");
        }

        _partial.Append(Encoding.Latin1.GetString(buffer.Span));

        while (TryTakeLine(out string? command))
        {
            _written.Add(command);
            _writes.Writer.TryWrite(command);

            if (Behaviour == TransportBehaviour.Silent)
            {
                continue;
            }

            string? response = _responder(command);
            string prompt = PromptFor?.Invoke(command) ?? Prompt;
            string payload = response is null ? prompt : $"{response}\r\n{prompt}";
            await _pipe.Writer.WriteAsync(Encoding.Latin1.GetBytes(payload), cancellationToken).ConfigureAwait(false);
            await _pipe.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void DiscardInput()
    {
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _open = false;
        _pipe.Writer.Complete();
        return ValueTask.CompletedTask;
    }

    private async ValueTask WriteToPipeAsync(string text, CancellationToken cancellationToken)
    {
        await _pipe.Writer.WriteAsync(Encoding.Latin1.GetBytes(text), cancellationToken).ConfigureAwait(false);
        await _pipe.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool TryTakeLine(out string command)
    {
        string buffered = _partial.ToString();
        int end = buffered.IndexOf('\n', StringComparison.Ordinal);
        if (end < 0)
        {
            command = string.Empty;
            return false;
        }

        command = buffered[..end].TrimEnd('\r');
        _partial.Clear();
        _partial.Append(buffered[(end + 1)..]);
        return true;
    }
}
