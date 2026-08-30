using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Models;
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
/// <para>
/// <b>Two link styles, one session</b> (#310). A query/response family is served through
/// <see cref="LineProtocol"/>. A broadcast family — one that talks unprompted, an NMEA 0183 talker
/// being the shipped case — is claimed by what its driver's <see cref="IReceiverDriver.Overhear"/>
/// makes of the lines the synchronise step heard, before <c>*IDN?</c> is ever sent to it; its
/// driver's <see cref="IReceiverDriver.Link"/> says which kind it is, and from recognition on it is
/// served from a <see cref="BroadcastListener"/> and never written to. The synchronise step's
/// <c>*CLS</c> is the one write such a family ever receives (§7.2's scope note).
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
    private readonly IReadOnlyList<IReceiverDriver> _drivers;
    private readonly ILogger<DeviceSessionService> _logger;

    /// <summary>The driver for the receiver on this port. Starts as the first registered (#287).</summary>
    /// <remarks>
    /// Volatile because the poller reads it from its own thread between sweeps, and the write
    /// happens on the connect path. The swap is a single reference assignment made before the pump
    /// starts, so a sweep never sees half a driver — at worst it sees the previous one for commands
    /// that will fail anyway, the link being down while the swap happens.
    /// </remarks>
    private volatile IReceiverDriver _driver;

    /// <summary>
    /// The read side of a broadcast link while one is connected (#310). Null for a query/response
    /// family, which is served through <see cref="_protocol"/> instead.
    /// </summary>
    private BroadcastListener? _listener;

    private readonly Channel<PendingCommand> _queue = Channel.CreateUnbounded<PendingCommand>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private ITransport? _transport;
    private LineProtocol? _protocol;
    private CancellationTokenSource? _sessionCts;
    private Task? _pump;

    /// <summary>Wakes the backoff wait when Retry now or Stop retrying is pressed (#248).</summary>
    private volatile CancellationTokenSource? _retryNow;
    private DateTimeOffset? _nextRetryAt;
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
    /// <param name="drivers">
    /// The receiver families this session can drive, in priority order — the first is the fallback
    /// when no identity is claimed. Optional, defaulting to <see cref="SmartClockDriver"/> alone —
    /// the family this application was written against — so every existing construction site keeps
    /// working untouched (#122, #287).
    /// </param>
    public DeviceSessionService(
        Func<string, SerialSettings, ITransport> transportFactory,
        TimeProvider timeProvider,
        ILogger<DeviceSessionService>? logger = null,
        IReadOnlyList<IReceiverDriver>? drivers = null)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _transportFactory = transportFactory;
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger<DeviceSessionService>.Instance;

        // Optional, and defaulting to the family this application was written against, so every
        // existing construction site and every existing test keeps working untouched (#122).
        _drivers = drivers is { Count: > 0 } ? drivers : [new SmartClockDriver(timeProvider)];
        _driver = _drivers[0];

        // The union in registration order, first-seen wins, so adding a driver can only append
        // probes — it cannot reorder the walk §10.12 fixes for the family already shipped.
        AutoDetectPlan = _drivers
            .SelectMany(candidate => candidate.AutoDetectSequence)
            .Distinct()
            .ToList()
            .AsReadOnly();
    }

    /// <summary>Raised whenever <see cref="Status"/> changes.</summary>
    public event EventHandler<ConnectionStatusChanged>? StatusChanged;

    /// <summary>
    /// Raised after every transaction this session completes, for §10.11's transcript.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every transaction the pump serves — polls, user commands, refusals, successes, timeouts and
    /// faults alike — plus the connect sequence's identity read, or the overheard listen that
    /// stands in for it on a broadcast link (#310). The synchronise step's <c>*CLS</c> is not
    /// published. §10.11 says the transcript shows all traffic, and a transcript that quietly
    /// omitted the failures would be worthless for the one job it has.
    /// </para>
    /// <para>
    /// Raised off the UI thread — on the pump for served commands, on the connect path for the
    /// identity entry. A handler that touches XAML must marshal.
    /// </para>
    /// </remarks>
    public event EventHandler<TranscriptEntry>? TransactionCompleted;

    /// <summary>Where the session stands.</summary>
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;

    /// <summary>The driver for the receiver on this port (#122).</summary>
    /// <remarks>
    /// Selected where the identity is read (#287, #310): the connect sequence listens first and
    /// hands the lines it hears to every driver's <see cref="IReceiverDriver.Overhear"/>; only when
    /// nothing claims them does it probe <c>*IDN?</c> neutrally. Either way it then picks the first
    /// registered driver whose <see cref="IReceiverDriver.Recognises"/> claims the identity, falling
    /// back to the first registered when none does. Callers should read it per use rather than
    /// caching it, because a reconnect re-selects — the receiver on the port can have been swapped
    /// while the link was down.
    /// </remarks>
    public IReceiverDriver Driver => _driver;

    /// <summary>
    /// The serial configurations auto-detect walks: every registered driver's, in registration
    /// order, first appearance wins (#287).
    /// </summary>
    /// <remarks>
    /// Exposed so the connection dialog's "n of m" counts the walk that will actually run, rather
    /// than restating one family's list.
    /// </remarks>
    public IReadOnlyList<SerialSettings> AutoDetectPlan { get; }

    /// <summary>
    /// The identity string the receiver answered <c>*IDN?</c> with — or, for a family overheard
    /// before it was asked (#310), the identity its driver claimed, in the same four-field shape —
    /// once connected.
    /// </summary>
    public string? Identity { get; private set; }

    /// <summary>
    /// <see cref="Identity"/> parsed into its four fields, or null when it did not parse (#64).
    /// </summary>
    /// <remarks>
    /// The raw string is kept alongside rather than replaced: §11.1's rule is that an unparseable
    /// field becomes null and the reader still gets what the device said. The log records the raw
    /// line; nothing on screen shows it yet, so a model this build has never heard of loses
    /// nothing that was ever displayed.
    /// </remarks>
    public DeviceIdentity? ParsedIdentity { get; private set; }

    /// <summary>
    /// What this model has, per §8.6 — the conservative profile until an identity is read.
    /// </summary>
    /// <remarks>
    /// Conservative before connecting, and conservative for a model that is not recognised, so the
    /// failure mode is a feature that is absent rather than a command sent to hardware without it.
    /// </remarks>
    public ModelProfile Profile => ModelProfile.For(ParsedIdentity);

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
    /// auto-detect walks every registered driver's settings — ten in the shipped composition — and
    /// all but one are expected to fail. That collapses two outcomes §9.11 gives different copy
    /// to: a port that answered nothing, and a port Windows would not open at all. This carries the
    /// distinction out without reintroducing the exception —
    /// <see cref="TransportFault.AccessDenied"/> is the "No permission" row, and
    /// <see cref="TransportFault.PortNotFound"/> is the port that stopped being there.
    /// </remarks>
    public TransportFault LastFault { get; private set; }

    /// <summary>
    /// Whether a dropped link is retried. Corresponds to "Reconnect automatically" in §10.12.
    /// </summary>
    public bool StayConnected { get; set; } = true;

    /// <summary>
    /// When the next reconnect attempt is due, or <see langword="null"/> when none is scheduled.
    /// </summary>
    /// <remarks>
    /// §9.11's Connection-lost row asks for a countdown — "Lost the connection to COM3. Retrying in
    /// 4 seconds." — which needs the schedule, not just the fact of retrying. Exposed as the instant
    /// rather than the remaining seconds so the caller can tick it against its own clock without
    /// this class raising an event per second (#248).
    /// </remarks>
    public DateTimeOffset? NextRetryAt
    {
        get => _nextRetryAt;
        private set
        {
            if (_nextRetryAt == value)
            {
                return;
            }

            _nextRetryAt = value;
            RetryScheduleChanged?.Invoke();
        }
    }

    /// <summary>
    /// Raised when <see cref="NextRetryAt"/> changes: published when the loop schedules an attempt,
    /// cleared when the wait ends. A test seam, and deliberately not part of the public surface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing in the application wants this. §9.11's banner reads the instant and ticks against
    /// its own clock, which is the whole reason the schedule is a property rather than an event
    /// (#248) — adding a public event to serve tests would undo that decision.
    /// </para>
    /// <para>
    /// <b>What it is for (#326).</b> The reconnect loop is fire-and-forget, so a test had no way to
    /// know it had reached its wait, and three tests sampled <see cref="NextRetryAt"/> in a
    /// <c>Task.Delay(10)</c> loop with a wall-clock budget instead. That is a deadline, not an
    /// ordering: on a busy machine the loop simply had not got there inside the budget, and the
    /// assertion failed for a reason that had nothing to do with the property under test. Every
    /// flake this repository has had is that shape, and widening the budget only moves it to a
    /// busier machine — CI is a busier machine.
    /// </para>
    /// </remarks>
    internal event Action? RetryScheduleChanged;

    /// <summary>Tries again now instead of waiting out the backoff (§9.11's <b>Retry now</b>).</summary>
    /// <remarks>
    /// Wakes the wait rather than starting a second attempt, so the schedule keeps one attempt in
    /// flight at a time. Does nothing unless a reconnect is actually waiting — pressing it during
    /// the attempt itself should not queue another.
    /// </remarks>
    public void RetryNow() => _retryNow?.Cancel();

    /// <summary>
    /// Stops retrying and leaves the link faulted (§9.11's <b>Stop retrying</b>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Faulted rather than Disconnected, and the distinction is §9.11's: <c>Disconnected</c> is a
    /// state the user chose for the <i>link</i>, and offers "Choose a port". This is the user
    /// declining to keep <i>retrying</i> a link that dropped underneath them, which is still a
    /// fault — the receiver is not there, and saying "not connected, choose a port" would suggest
    /// the port was the problem.
    /// </para>
    /// <para>
    /// <see cref="StayConnected"/> is deliberately left alone. It is the §10.12 preference
    /// "Reconnect automatically", and one press of a button in one outage must not silently rewrite
    /// a setting that governs every future one.
    /// </para>
    /// </remarks>
    public void StopRetrying()
    {
        if (Status != ConnectionStatus.Reconnecting)
        {
            return;
        }

        NextRetryAt = null;
        SetStatus(ConnectionStatus.Faulted, "Stopped retrying.");

        // Wakes the backoff wait so the loop sees the status change now rather than up to thirty
        // seconds later, still calling itself Reconnecting to anything that asks.
        _retryNow?.Cancel();
    }

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
    /// Walks <see cref="AutoDetectPlan"/> — §10.12's sequence and every other registered driver's,
    /// in registration order — until the port answers with a plausible identity or is overheard
    /// saying one (#310).
    /// </summary>
    /// <param name="portName">The port to probe.</param>
    /// <param name="progress">Reports each combination as it is tried, for the dialog's progress line.</param>
    /// <param name="cancellationToken">Cancels the walk; §10.12 requires it to be cancellable.</param>
    /// <returns>The settings that worked, or <see langword="null"/> if none did.</returns>
    /// <remarks>
    /// Most-likely-first, so a Z3805A answers on the first attempt and a Z3801A on the second, and
    /// the worst case — every registered driver's settings, ten in the shipped composition — is
    /// only reached by a receiver configured unusually or a port with nothing on it. Each probe
    /// opens the port afresh: a wrong baud rate leaves framing errors behind, and reusing the
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

            foreach (SerialSettings candidate in AutoDetectPlan)
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
                // every baud rate. Walking the rest of the plan only delays the message §9.11 has
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
    /// <para>
    /// Takes an <see cref="ScpiCommand"/> rather than a string, so every command the application
    /// sends provably came from the §8.1 allowlist. There is deliberately no overload that accepts
    /// arbitrary text: the Advanced Console validates against the catalog and hands back an entry,
    /// it does not get a back door here.
    /// </para>
    /// <para>
    /// Since #287 the entry is also checked against the <i>current</i> driver's catalog at the
    /// moment it is served, because pages resolve commands when they open and a reconnect can
    /// select a different family underneath them. A command the connected receiver's driver does
    /// not offer comes back as a faulted transaction, unsent.
    /// </para>
    /// </remarks>
    /// <param name="command">A catalogued command. There is no overload taking text.</param>
    /// <param name="argument">Its parameter, already formatted and validated by the caller.</param>
    /// <param name="origin">
    /// Who asked, which only §10.11's transcript reads. It changes nothing about what is sent — the
    /// wire cannot tell a poll from a click — and exists so the console's "hide poll traffic" toggle
    /// filters on a fact rather than on a guess from the mnemonic.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait, not the transaction already in flight.</param>
    public async Task<Transaction> ExecuteAsync(
        ScpiCommand command,
        string? argument = null,
        CommandOrigin origin = CommandOrigin.User,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(_disposed, this);

        PendingCommand pending = new(command, argument, origin, cancellationToken);
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
            // path is also the auto-detect inner loop, and at a wrong baud rate every transaction in
            // it times out — the listen, the *CLS it sends, and the identity probe. Two seconds each
            // keeps a silent combination under ten seconds, and the walk is every registered
            // driver's settings, ten in the shipped composition.
            Transaction heard = await _protocol.SynchroniseAsync(TransactionTimeouts.AutoDetectProbe, cancellationToken).ConfigureAwait(false);

            // #310: a receiver that talks unprompted has already said who it is by the time the
            // synchronise step times out, and asking it *IDN? would only spend another probe timeout
            // on a question it will never answer. Every driver gets what was heard; the first that
            // claims it is selected below, and the identity probe is skipped.
            DeviceIdentity? overheard = Overhear(heard.Lines);
            if (overheard is not null)
            {
                Record(CommandOrigin.Session, heard);
                Identity = $"{overheard.Manufacturer},{overheard.Model},{overheard.SerialNumber},{overheard.FirmwareRevision}";
                ParsedIdentity = overheard;
            }
            else
            {
                Transaction identity = await _protocol
                    .ExecuteAsync("*IDN?", TransactionTimeouts.AutoDetectProbe, cancellationToken)
                    .ConfigureAwait(false);

                Record(CommandOrigin.Session, identity);

                if (!identity.Succeeded || !LooksLikeIdentity(identity.FirstLine))
                {
                    return false;
                }

                Identity = identity.FirstLine!.Trim();
                ParsedIdentity = DeviceIdentity.Parse(Identity);
            }

            _consecutiveTimeouts = 0;

            // #287: the probe above belonged to no driver — a bare *IDN? at a neutral timeout —
            // and this is where the answer chooses one. It runs on every connect, including a
            // reconnect, because the receiver on the port can have been swapped while the link was
            // down, and before the pump starts, so no command is ever served under a driver the
            // identity has disqualified.
            SelectDriver();

            // #310: a broadcast family is served from what its listener hears rather than from the
            // protocol. Started here, after the probe has finished with the pipe and before the
            // pump can ask anything, so the first poll already has a cycle to read.
            if (_driver.Link == LinkStyle.Broadcast)
            {
                _listener = new BroadcastListener(_transport, _driver, _timeProvider, _logger);

                // The probe consumed the talker's first seconds from the pipe; they are real data,
                // and without them the first sweep would find nothing to answer with and could read
                // a healthy link as a silent one.
                _listener.Seed(heard.Lines);
                _listener.Start();
            }

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
    /// Hands what the synchronise step heard to every driver, and returns the identity of the
    /// first to claim it (#310). Null when nobody does — the ordinary case for a receiver that
    /// waits to be asked.
    /// </summary>
    /// <remarks>
    /// Guarded the way <see cref="SelectDriver"/> guards <c>Recognises</c>: a driver that throws
    /// on a list of lines has a bug, and the connect path is the wrong place to pay for it.
    /// </remarks>
    private DeviceIdentity? Overhear(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return null;
        }

        foreach (IReceiverDriver candidate in _drivers)
        {
            DeviceIdentity? claimed;
            try
            {
                claimed = candidate.Overhear(lines);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "The {Family} driver's Overhear threw over {Count} line(s); treating that as not claimed.",
                    candidate.Family,
                    lines.Count);
                continue;
            }

            if (claimed is not null)
            {
                _logger.LogInformation(
                    "The {Family} driver overheard {Model} on {Port} in {Count} line(s); the identity probe is skipped.",
                    candidate.Family,
                    claimed.Model,
                    PortName,
                    lines.Count);
                return claimed;
            }
        }

        return null;
    }

    /// <summary>
    /// Picks the driver for the identity just read: the first registered that claims it, or the
    /// first registered outright when none does (#287).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Registration order is priority order, deterministically.</b> Two drivers both claiming an
    /// identity is a race only if the winner depends on anything but the list — here it never does,
    /// so a driver author who over-claims loses to whoever registered first and finds out from the
    /// log rather than from intermittent behaviour.
    /// </para>
    /// <para>
    /// <b>The fallback is a warning, not a refusal.</b> An unrecognised identity connected fine
    /// before there was any selection at all — the SmartClock driver simply drove it — and §8.6
    /// already handles an unknown <i>model</i> conservatively. Refusing to connect would turn every
    /// receiver with an odd identity string into a regression.
    /// </para>
    /// </remarks>
    private void SelectDriver()
    {
        IReceiverDriver? claimed = null;
        foreach (IReceiverDriver candidate in _drivers)
        {
            bool recognises;
            try
            {
                recognises = candidate.Recognises(ParsedIdentity);
            }
            catch (Exception exception)
            {
                // A predicate that throws is a driver bug, but the connect path is the wrong place
                // to pay for it: the exception would escape the transport-fault filters and take
                // down a reconnect loop that is fire-and-forget. Parse and InterpretSweep carry an
                // explicit never-throw contract; Recognises gets the same protection here because
                // a claim that errored is soundly read as "does not claim".
                _logger.LogWarning(
                    exception,
                    "The {Family} driver's Recognises threw for {Identity}; treating that as not claimed.",
                    candidate.Family,
                    Identity);
                continue;
            }

            if (recognises)
            {
                claimed = candidate;
                break;
            }
        }

        if (claimed is null && _drivers.Count > 1)
        {
            // Only worth a warning when there was a choice to make. With one registered driver
            // this is the pre-#287 behaviour exactly, and §8.6's conservative profile already
            // covers the unknown-model case within the family.
            _logger.LogWarning(
                "No registered driver recognises {Identity}; continuing with {Family}.",
                Identity,
                _drivers[0].Family);
        }

        IReceiverDriver selected = claimed ?? _drivers[0];
        if (!ReferenceEquals(selected, _driver))
        {
            _logger.LogInformation(
                "The {Family} driver now serves {Identity}.",
                selected.Family,
                Identity);
        }

        _driver = selected;
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

        // #287: the command must be in the CURRENT driver's allowlist at the moment it is served,
        // not merely have been in some registered driver's at some earlier time. Pages resolve
        // their commands when they are opened, and a reconnect can select a different family
        // underneath an open page — this check is what keeps §8.1's "every command sent provably
        // came from the allowlist" true of the receiver actually on the port. Refused as a faulted
        // transaction rather than an exception, because the poller treats non-transport exceptions
        // as fatal and a stale page click must not take the poll loop with it. Nothing legitimate
        // is caught: the identity probe goes over the protocol directly, and every in-family
        // caller resolved through this same driver moments earlier.
        if (_driver.Find(pending.Command.Mnemonic) is null)
        {
            Transaction refused = new()
            {
                Command = pending.Command.Mnemonic,
                Outcome = TransactionOutcome.Faulted,
                Lines = [],
                EchoDiscarded = false,
                Elapsed = TimeSpan.Zero,
            };

            pending.Completion.TrySetResult(refused);
            Record(pending.Origin, refused);
            _logger.LogWarning(
                "{Mnemonic} was refused without being sent: it is not in the {Family} driver's catalog. "
                + "The receiver on the port has changed since the command was resolved.",
                pending.Command.Mnemonic,
                _driver.Family);
            return;
        }

        // #310: on a broadcast link nothing goes to the wire. The answer is what the listener has
        // heard under this key, and a talker that has gone quiet reads as a timeout so the
        // reconnect logic below treats it exactly as a receiver that stopped answering.
        if (_driver.Link == LinkStyle.Broadcast)
        {
            BroadcastListener? listener = _listener;
            if (listener is null)
            {
                pending.Completion.TrySetException(new TransportException(TransportFault.NotOpen, "Not listening."));
                return;
            }

            Transaction heard = listener.Answer(pending.Command.Mnemonic, TimeoutFor(pending.Command));
            pending.Completion.TrySetResult(heard);
            Record(pending.Origin, heard);
            await NoteOutcomeAsync(heard).ConfigureAwait(false);
            return;
        }

        string text = TextFor(pending.Command, pending.Argument);

        try
        {
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(sessionToken, pending.CancellationToken);

            Transaction transaction = await protocol
                .ExecuteAsync(text, TimeoutFor(pending.Command), linked.Token)
                .ConfigureAwait(false);

            pending.Completion.TrySetResult(transaction);
            Record(pending.Origin, transaction);
            await NoteOutcomeAsync(transaction).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (pending.CancellationToken.IsCancellationRequested)
        {
            pending.Completion.TrySetCanceled(pending.CancellationToken);
        }
        catch (Exception exception) when (TransportFaults.IsTransportFault(exception))
        {
            pending.Completion.TrySetException(exception);

            // Recorded before the reconnect, because the transcript's whole value here is showing
            // what was on the wire when the link went. A fault leaves no Transaction, so the entry
            // is built from what is known: what was sent, and that it did not come back.
            RecordFault(pending.Origin, text, exception);

            BeginReconnect($"The link to {PortName} failed: {exception.Message}");
        }
        finally
        {
            // Nobody may be left awaiting a completion that will never be set (#259).
            //
            // A caller waits on pending.Completion bounded only by its OWN token, so a completion
            // that is never set is not a slow command — it is a caller that never returns. The poll
            // loop passes a token it does not cancel, so it waits for the life of the process:
            // alive, holding its sweep, ignoring the refresh flag, and logging nothing.
            //
            // The path that used to do it is the OperationCanceledException raised when
            // TearDownAsync cancels the SESSION token while a command is in flight. That is neither
            // the caller cancelling — the filter above tests the caller's token, not this one — nor
            // a transport fault, so it matched no catch, escaped to PumpAsync, and ended the pump
            // as an ordinary shutdown with this caller still waiting.
            //
            // Which is why a power cycle wedged the app and a USB unplug never did: removal throws
            // IOException, a transport fault, and the caller was failed properly.
            //
            // TrySet on an already-completed source is a no-op, so this only catches the paths that
            // would otherwise set nothing.
            pending.Completion.TrySetException(
                new TransportException(
                    TransportFault.NotOpen,
                    "The session ended before the command completed."));
        }
    }

    /// <summary>How long to wait for a command, asked of the driver rather than of a static (#122).</summary>
    /// <remarks>
    /// These are measurements against one receiver, not conventions — the Z3805A's GPS self-test
    /// reached 24.0 s against a 30 s class — so which receiver is attached decides them. Routing
    /// through the driver is what makes that true rather than merely intended, and it keeps one
    /// table and one lookup: a second timeout policy kept here once diverged silently from the
    /// tested one.
    /// </remarks>
    private TimeSpan TimeoutFor(ScpiCommand command) => _driver.TimeoutFor(command.Mnemonic);

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

    /// <summary>
    /// Retries the connection on §7.2's backoff — doubling from 2 s, capped at 30 s — until it
    /// connects, is told to stop, or is disposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The wait goes through the <see cref="TimeProvider"/> overload of <c>Task.Delay</c>, so a
    /// test can step a 30 s cap instantly instead of waiting for it.
    /// </para>
    /// <para>
    /// <b>Every attempt is logged at Information, and that is deliberate</b> (#14). P0-14's only
    /// verification is a person unplugging the adapter once and watching what happens, and its
    /// acceptance is a pair of durations — Disconnected within 10 s, reconnected within 45 s of
    /// replug. Those are measurable from the log, which timestamps to the millisecond, but only if
    /// the log says what happened between the two status lines.
    /// </para>
    /// <para>
    /// The second figure was 30 s until 28 Aug 2026, when the QA run this logging exists for showed
    /// it could not be met: <see cref="MaximumBackoff"/> is itself 30 s, so an adapter returning
    /// just after a failed attempt waits the whole interval and then needs a further ~2.2 s to open
    /// the port and finish auto-detect. The log is what made that measurable rather than arguable —
    /// which is the case for logging every attempt, made by the thing it was written for.
    /// </para>
    /// <para>
    /// It used to say nothing. The failure path that <i>throws</i> was logged at Debug, below the
    /// level the application ships at; the failure path that simply returns <see langword="false"/>
    /// — the adapter is back but the receiver is not answering yet, which is the ordinary case
    /// after a power cycle — was not logged at all. A recovery that took forty-five seconds left
    /// forty-five seconds of silence, with no way to tell one slow attempt from fifteen fast ones.
    /// </para>
    /// <para>
    /// This is not chatter: the session has already announced <c>Reconnecting</c> before the loop
    /// starts, every line here is news within an abnormal state, and they stop when it connects.
    /// The backoff is named in each line, so the log also demonstrates §7.2's 2 / 4 / 8 / 30 second
    /// schedule rather than merely obeying it.
    /// </para>
    /// </remarks>
    private async Task ReconnectLoopAsync()
    {
        TimeSpan backoff = FirstBackoff;
        int attempt = 0;

        while (StayConnected && !_disposed && Status == ConnectionStatus.Reconnecting)
        {
            attempt++;

            // Computed before the attempt so a failure can say what happens next, rather than the
            // reader having to hold §7.2's schedule in their head while reading a log at a bench.
            TimeSpan next = backoff < MaximumBackoff
                ? TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaximumBackoff.Ticks))
                : MaximumBackoff;

            try
            {
                // Published before the wait so the banner can count down against it, and waited
                // through a source Retry now can cancel (#248). Cancelling it is not a failure —
                // it means somebody pressed the button, and the attempt simply happens now.
                using (CancellationTokenSource wake = new())
                {
                    _retryNow = wake;
                    NextRetryAt = _timeProvider.GetUtcNow() + backoff;

                    try
                    {
                        await Task.Delay(backoff, _timeProvider, wake.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Retry now, or Stop retrying. The loop condition below settles which.
                    }
                    finally
                    {
                        _retryNow = null;
                        NextRetryAt = null;
                    }
                }

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

                    // No exception, no connection: the port opened or did not, and the receiver did
                    // not answer. Silent before #14, and the commonest shape after a power cycle.
                    _logger.LogInformation(
                        "Reconnect attempt {Attempt} to {Port} did not answer; next try in {Backoff}.",
                        attempt,
                        PortName,
                        next);
                }
                finally
                {
                    _lifecycle.Release();
                }
            }
            catch (Exception exception) when (TransportFaults.IsTransportFault(exception))
            {
                _logger.LogInformation(
                    exception,
                    "Reconnect attempt {Attempt} to {Port} failed; next try in {Backoff}.",
                    attempt,
                    PortName,
                    next);
            }

            backoff = next;
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

        // Everything still queued, now that no pump is left to serve it (#259). PumpAsync has always
        // said "queued callers are failed by TearDownAsync" and until now that was not true: the
        // channel was left holding them, and they waited for whichever pump started next — so a poll
        // queued before an outage could be sent minutes later, against a different connection, and a
        // tier C command the user confirmed before the link dropped could execute after it came
        // back without being confirmed again.
        while (_queue.Reader.TryRead(out PendingCommand? queued))
        {
            queued.Completion.TrySetException(
                new TransportException(
                    TransportFault.NotOpen,
                    "The session ended before the command was sent."));
        }

        _sessionCts?.Dispose();
        _sessionCts = null;
        _protocol = null;

        // Before the transport goes: the listener is the pipe's reader, and stopping it after the
        // transport has closed is a read on a disposed pipe (#310).
        if (_listener is not null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
            _listener = null;
        }

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

    /// <summary>
    /// Exactly what goes on the wire for a command and its argument.
    /// </summary>
    /// <remarks>
    /// Public and static so §10.11's "Will send:" line is produced by the same code that produces
    /// the bytes, rather than by a second expression that agrees with it today. A preview that can
    /// disagree with what is sent is worse than no preview: it is a confirmation step that lies.
    /// </remarks>
    public static string TextFor(ScpiCommand command, string? argument)
    {
        ArgumentNullException.ThrowIfNull(command);

        return string.IsNullOrEmpty(argument) ? command.Mnemonic : $"{command.Mnemonic} {argument}";
    }

    /// <summary>Publishes a completed transaction to §10.11's transcript.</summary>
    private void Record(CommandOrigin origin, Transaction transaction) =>
        TransactionCompleted?.Invoke(this, new TranscriptEntry(
            _timeProvider.GetUtcNow().UtcTicks,
            origin,
            transaction.Command,
            transaction.Lines,
            transaction.Outcome,
            transaction.Elapsed,
            transaction.PromptStatus));

    /// <summary>Publishes a transaction that never completed, so the transcript shows the gap.</summary>
    private void RecordFault(CommandOrigin origin, string sent, Exception exception) =>
        TransactionCompleted?.Invoke(this, new TranscriptEntry(
            _timeProvider.GetUtcNow().UtcTicks,
            origin,
            sent,
            [exception.Message],
            TransactionOutcome.Faulted,
            TimeSpan.Zero,
            PromptStatus: null));

    /// <summary>One queued command and the caller waiting on it.</summary>
    private sealed class PendingCommand(
        ScpiCommand command,
        string? argument,
        CommandOrigin origin,
        CancellationToken cancellationToken)
    {
        public ScpiCommand Command { get; } = command;

        public string? Argument { get; } = argument;

        public CommandOrigin Origin { get; } = origin;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public TaskCompletionSource<Transaction> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
