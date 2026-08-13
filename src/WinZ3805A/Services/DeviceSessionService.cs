using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Transport;

namespace WinZ3805A.Services;

/// <summary>Reports a change in <see cref="DeviceSessionService.Status"/>.</summary>
/// <param name="Status">Where the session now stands.</param>
/// <param name="Detail">A sentence fit to show the user, or <see langword="null"/>.</param>
public sealed record ConnectionStatusChanged(ConnectionStatus Status, string? Detail);

/// <summary>
/// Owns the link to one receiver: the transport, the command queue, and the connection state
/// (§12, §7.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>One transaction at a time, always.</b> The receiver serves exactly one, and §7.2 puts the
/// duty of enforcing that here rather than on callers. Every command goes through a single-consumer
/// <see cref="Channel{T}"/> and every caller awaits its turn — there are no exceptions, including
/// for the poller, because two overlapping transactions do not fail loudly, they interleave and
/// return each other's answers.
/// </para>
/// <para>
/// <b>No static state.</b> §12 requires this to be instantiable per device and resolvable from a
/// keyed DI registration even though v1 creates one. Everything here is instance state, so two
/// sessions on two ports cannot see each other.
/// </para>
/// <para>
/// <b>It is built for the adapter being yanked out.</b> §6.4 lists four exception types reachable
/// on surprise removal of a USB-serial adapter and <c>SerialPort</c> has a long-standing habit of
/// raising them from places that terminate the process. The transport already avoids the event
/// model; this class adds the other half — a fault or three consecutive timeouts drops the session
/// to <see cref="ConnectionStatus.Reconnecting"/> and retries on the §7.2 backoff rather than
/// letting the failure escape.
/// </para>
/// </remarks>
public sealed class DeviceSessionService : IAsyncDisposable
{
    /// <summary>How many consecutive timeouts mean the link is gone rather than the device being slow (§7.2).</summary>
    private const int TimeoutsBeforeReconnect = 3;

    /// <summary>The §7.2 backoff: 2 s, 4 s, 8 s, … capped at 30 s.</summary>
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(30);

    private readonly Func<string, SerialSettings, ITransport> _transportFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DeviceSessionService> _logger;

    private readonly Channel<PendingCommand> _queue = Channel.CreateUnbounded<PendingCommand>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private ITransport? _transport;
    private LineProtocol? _protocol;
    private CancellationTokenSource? _sessionCts;
    private Task? _pump;
    private int _consecutiveTimeouts;
    private bool _disposed;

    /// <summary>Creates a session.</summary>
    /// <param name="transportFactory">
    /// Builds a transport for a port and settings. Injected so tests can substitute
    /// <c>FakeTransport</c> and drive the whole reconnect policy with no hardware; the application
    /// passes one that builds a <see cref="SerialTransport"/>.
    /// </param>
    /// <param name="timeProvider">
    /// Supplies time for the backoff and for transaction timing. Injected per §12 so a test can
    /// pin the clock rather than sleep through a 30 s cap.
    /// </param>
    /// <param name="logger">Optional log sink.</param>
    public DeviceSessionService(
        Func<string, SerialSettings, ITransport> transportFactory,
        TimeProvider timeProvider,
        ILogger<DeviceSessionService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _transportFactory = transportFactory;
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger<DeviceSessionService>.Instance;
    }

    /// <summary>Raised whenever <see cref="Status"/> changes.</summary>
    public event EventHandler<ConnectionStatusChanged>? StatusChanged;

    /// <summary>Where the session stands.</summary>
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;

    /// <summary>The identity string the receiver answered <c>*IDN?</c> with, once connected.</summary>
    public string? Identity { get; private set; }

    /// <summary>The port this session is bound to, once a connection has been attempted.</summary>
    public string? PortName { get; private set; }

    /// <summary>The line settings in use.</summary>
    public SerialSettings Settings { get; private set; } = SerialSettings.Default;

    /// <summary>
    /// Why the last attempt to open the port failed, or <see cref="TransportFault.None"/> if it did
    /// not fail that way.
    /// </summary>
    /// <remarks>
    /// The connect path deliberately returns <see langword="false"/> rather than throwing, because
    /// auto-detect walks eight settings and seven of them are expected to fail. That collapses two
    /// outcomes §9.11 gives different copy to: a port that answered nothing, and a port Windows
    /// would not open at all. This carries the distinction out without reintroducing the exception —
    /// <see cref="TransportFault.AccessDenied"/> is the "No permission" row, and
    /// <see cref="TransportFault.PortNotFound"/> on ARM64 is usually the missing driver of §6.1.
    /// </remarks>
    public TransportFault LastFault { get; private set; }

    /// <summary>
    /// Whether a dropped link is retried. Corresponds to "Reconnect automatically" in §10.12.
    /// </summary>
    public bool StayConnected { get; set; } = true;

    /// <summary>Opens the port with the given settings and synchronises the protocol.</summary>
    /// <returns>True when the receiver answered.</returns>
    public async Task<bool> ConnectAsync(
        string portName,
        SerialSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await TearDownAsync().ConfigureAwait(false);

            PortName = portName;
            Settings = settings;
            SetStatus(ConnectionStatus.Connecting, $"Connecting to {portName} at {settings}.");

            return await OpenAndSynchroniseAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Walks the §10.12 sequence until a port answers with a plausible identity.
    /// </summary>
    /// <param name="portName">The port to probe.</param>
    /// <param name="progress">Reports each combination as it is tried, for the dialog's progress line.</param>
    /// <param name="cancellationToken">Cancels the walk; §10.12 requires it to be cancellable.</param>
    /// <returns>The settings that worked, or <see langword="null"/> if none did.</returns>
    /// <remarks>
    /// Most-likely-first, so a Z3805A answers on the first attempt and a Z3801A on the second, and
    /// the eight-combination worst case is only reached by a receiver configured unusually. Each
    /// probe opens the port afresh: a wrong baud rate leaves framing errors behind, and reusing the
    /// handle carries them into the next attempt.
    /// </remarks>
    public async Task<SerialSettings?> AutoDetectAsync(
        string portName,
        IProgress<SerialSettings>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await TearDownAsync().ConfigureAwait(false);

            PortName = portName;
            SetStatus(ConnectionStatus.Connecting, $"Detecting settings on {portName}.");

            foreach (SerialSettings candidate in SerialSettings.AutoDetectSequence)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(candidate);
                _logger.LogDebug("Auto-detect trying {Settings} on {Port}.", candidate, portName);

                Settings = candidate;
                if (await OpenAndSynchroniseAsync(cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogInformation("Auto-detect settled on {Settings}: {Identity}.", candidate, Identity);
                    return candidate;
                }

                await TearDownAsync().ConfigureAwait(false);

                // A port Windows will not open, or one that is not there, fails identically at
                // every baud rate. Walking the remaining seven only delays the message §9.11 has
                // for that case, and its copy is nothing like "no receiver answered".
                if (LastFault is TransportFault.AccessDenied or TransportFault.PortNotFound)
                {
                    SetStatus(ConnectionStatus.Faulted, $"Could not open {portName}.");
                    return null;
                }
            }

            SetStatus(ConnectionStatus.Faulted, $"No receiver answered on {portName} at any supported setting.");
            return null;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Closes the link deliberately, which is not a fault (§9.11).</summary>
    public async Task DisconnectAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await TearDownAsync().ConfigureAwait(false);
            SetStatus(ConnectionStatus.Disconnected, "Disconnected.");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Queues a catalogued command and awaits its turn.
    /// </summary>
    /// <remarks>
    /// Takes an <see cref="ScpiCommand"/> rather than a string, so every command the application
    /// sends provably came from the §8.1 allowlist. There is deliberately no overload that accepts
    /// arbitrary text: the Advanced Console validates against the catalog and hands back an entry,
    /// it does not get a back door here.
    /// </remarks>
    public async Task<Transaction> ExecuteAsync(
        ScpiCommand command,
        string? argument = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(_disposed, this);

        PendingCommand pending = new(command, argument, cancellationToken);
        if (!_queue.Writer.TryWrite(pending))
        {
            throw new InvalidOperationException("The command queue is closed.");
        }

        return await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Writer.TryComplete();

        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await TearDownAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
            _lifecycle.Dispose();
        }
    }

    // ===========================================================================================

    private async Task<bool> OpenAndSynchroniseAsync(CancellationToken cancellationToken)
    {
        LastFault = TransportFault.None;
        try
        {
            _transport = _transportFactory(PortName!, Settings);
            await _transport.OpenAsync(cancellationToken).ConfigureAwait(false);

            _protocol = new LineProtocol(_transport, _timeProvider);

            // The receiver emits an identity banner on DTR assert and eats the first command with a
            // framing error, so the connect sequence spends one transaction absorbing both before
            // anything real is asked. Skipping this puts every subsequent reply one behind.
            //
            // The probe timeout rather than the 3 s default, here and for the identity below: this
            // path is also the auto-detect inner loop, and at a wrong baud rate both transactions
            // time out. Two seconds each keeps the eight-combination worst case near half a minute
            // instead of most of one.
            await _protocol.SynchroniseAsync(TransactionTimeouts.AutoDetectProbe, cancellationToken).ConfigureAwait(false);

            Transaction identity = await _protocol
                .ExecuteAsync("*IDN?", TransactionTimeouts.AutoDetectProbe, cancellationToken)
                .ConfigureAwait(false);

            if (!identity.Succeeded || !LooksLikeIdentity(identity.FirstLine))
            {
                return false;
            }

            Identity = identity.FirstLine!.Trim();
            _consecutiveTimeouts = 0;

            _sessionCts = new CancellationTokenSource();
            _pump = Task.Run(() => PumpAsync(_sessionCts.Token), CancellationToken.None);

            SetStatus(ConnectionStatus.Connected, Identity);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (TransportFaults.IsTransportFault(exception))
        {
            LastFault = TransportFaults.Classify(exception);
            _logger.LogDebug(exception, "Opening {Port} at {Settings} failed.", PortName, Settings);
            return false;
        }
    }

    /// <summary>
    /// Whether a reply is plausibly an identity rather than noise a wrong baud rate produced.
    /// </summary>
    /// <remarks>
    /// A mismatched baud rate does not produce silence — it produces bytes, and some of them are
    /// printable. Requiring the SCPI four-field shape is what stops auto-detect settling on the
    /// first setting that returned anything at all. The reference unit answers
    /// <c>SYMMETRICOM,Z3805A,3625A02931,1.01.03-A</c>.
    /// </remarks>
    private static bool LooksLikeIdentity(string? line) =>
        !string.IsNullOrWhiteSpace(line) && line.Split(',').Length >= 4;

    /// <summary>
    /// The single consumer. Everything the application sends to the receiver passes through here,
    /// one at a time, in order.
    /// </summary>
    private async Task PumpAsync(CancellationToken sessionToken)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(sessionToken).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out PendingCommand? pending))
                {
                    await ServeAsync(pending, sessionToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The session was torn down; queued callers are failed by TearDownAsync.
        }
    }

    private async Task ServeAsync(PendingCommand pending, CancellationToken sessionToken)
    {
        if (pending.Completion.Task.IsCompleted)
        {
            return;
        }

        LineProtocol? protocol = _protocol;
        if (protocol is null)
        {
            pending.Completion.TrySetException(new TransportException(TransportFault.NotOpen, "Not connected."));
            return;
        }

        string text = pending.Argument is null
            ? pending.Command.Mnemonic
            : $"{pending.Command.Mnemonic} {pending.Argument}";

        try
        {
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(sessionToken, pending.CancellationToken);

            Transaction transaction = await protocol
                .ExecuteAsync(text, TimeoutFor(pending.Command), linked.Token)
                .ConfigureAwait(false);

            pending.Completion.TrySetResult(transaction);
            await NoteOutcomeAsync(transaction).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (pending.CancellationToken.IsCancellationRequested)
        {
            pending.Completion.TrySetCanceled(pending.CancellationToken);
        }
        catch (Exception exception) when (TransportFaults.IsTransportFault(exception))
        {
            pending.Completion.TrySetException(exception);
            BeginReconnect($"The link to {PortName} failed: {exception.Message}");
        }
    }

    /// <remarks>
    /// §7.2 gives the full status screen 15 s because ~1900 bytes at 9600 baud is about 2 s of wire
    /// time alone, and the self-tests 30 s because they genuinely take that long. Everything else
    /// gets the 3 s default.
    /// </remarks>
    private static TimeSpan TimeoutFor(ScpiCommand command) => command.ResponseFormat switch
    {
        ResponseFormat.StatusScreen => TransactionTimeouts.StatusScreen,
        _ when command.Mnemonic is "*TST?" || command.Mnemonic.StartsWith(":DIAG:TEST", StringComparison.OrdinalIgnoreCase)
            => TransactionTimeouts.SelfTest,
        _ => TransactionTimeouts.Default,
    };

    /// <summary>
    /// Counts consecutive timeouts toward the §7.2 reconnect trigger, and resets on any success.
    /// </summary>
    /// <remarks>
    /// Three in a row rather than one: a single timeout is ordinary on a busy receiver, and dropping
    /// a working session for one slow reply would make the app flap.
    /// </remarks>
    private Task NoteOutcomeAsync(Transaction transaction)
    {
        if (transaction.Succeeded)
        {
            _consecutiveTimeouts = 0;
            return Task.CompletedTask;
        }

        if (transaction.Outcome == TransactionOutcome.Faulted)
        {
            BeginReconnect($"The link to {PortName} failed.");
            return Task.CompletedTask;
        }

        if (++_consecutiveTimeouts >= TimeoutsBeforeReconnect)
        {
            BeginReconnect($"{PortName} stopped answering.");
        }

        return Task.CompletedTask;
    }

    private void BeginReconnect(string detail)
    {
        if (Status is ConnectionStatus.Reconnecting or ConnectionStatus.Disconnected)
        {
            return;
        }

        if (!StayConnected)
        {
            SetStatus(ConnectionStatus.Faulted, detail);
            return;
        }

        SetStatus(ConnectionStatus.Reconnecting, detail);
        _ = Task.Run(ReconnectLoopAsync, CancellationToken.None);
    }

    /// <remarks>
    /// The backoff doubles from 2 s and caps at 30 s (§7.2). It waits through
    /// <see cref="TimeProvider"/> rather than <c>Task.Delay</c> so a test can step a 30 s cap
    /// instantly instead of waiting for it.
    /// </remarks>
    private async Task ReconnectLoopAsync()
    {
        TimeSpan backoff = FirstBackoff;

        while (StayConnected && !_disposed && Status == ConnectionStatus.Reconnecting)
        {
            try
            {
                await Task.Delay(backoff, _timeProvider, CancellationToken.None).ConfigureAwait(false);

                await _lifecycle.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!StayConnected || _disposed || Status != ConnectionStatus.Reconnecting)
                    {
                        return;
                    }

                    await TearDownAsync().ConfigureAwait(false);
                    if (await OpenAndSynchroniseAsync(CancellationToken.None).ConfigureAwait(false))
                    {
                        return;
                    }
                }
                finally
                {
                    _lifecycle.Release();
                }
            }
            catch (Exception exception) when (TransportFaults.IsTransportFault(exception))
            {
                _logger.LogDebug(exception, "Reconnect attempt to {Port} failed.", PortName);
            }

            backoff = backoff < MaximumBackoff
                ? TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaximumBackoff.Ticks))
                : MaximumBackoff;
        }
    }

    /// <remarks>
    /// §6.4 item 3: disposal tolerates an already-faulted port, because after a surprise removal
    /// closing it is exactly as likely to throw as reading it was.
    /// </remarks>
    private async Task TearDownAsync()
    {
        if (_sessionCts is not null)
        {
            await _sessionCts.CancelAsync().ConfigureAwait(false);
        }

        if (_pump is not null)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (Exception exception) when (TransportFaults.IsTransportFault(exception))
            {
                _logger.LogDebug(exception, "The command pump ended faulted.");
            }

            _pump = null;
        }

        _sessionCts?.Dispose();
        _sessionCts = null;
        _protocol = null;

        if (_transport is not null)
        {
            try
            {
                await _transport.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (TransportFaults.IsTransportFault(exception))
            {
                _logger.LogDebug(exception, "Closing {Port} threw, which is expected after a removal.", PortName);
            }

            _transport = null;
        }
    }

    private void SetStatus(ConnectionStatus status, string? detail)
    {
        if (Status == status)
        {
            return;
        }

        Status = status;
        _logger.LogInformation("Session {Port} is now {Status}. {Detail}", PortName, status, detail);
        StatusChanged?.Invoke(this, new ConnectionStatusChanged(status, detail));
    }

    /// <summary>One queued command and the caller waiting on it.</summary>
    private sealed class PendingCommand(ScpiCommand command, string? argument, CancellationToken cancellationToken)
    {
        public ScpiCommand Command { get; } = command;

        public string? Argument { get; } = argument;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public TaskCompletionSource<Transaction> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
