using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Models;
using WinZ3805A.Device.Parsing;
using WinZ3805A.Device.Transport;

namespace WinZ3805A.Services;

/// <summary>
/// Drives the two §7.3 cadences and writes what they find into <see cref="ReceiverStateStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two tiers, because of one asymmetry: the satellite elevation, azimuth and signal table has
/// <b>no individual query</b> — it exists only inside the full status screen — while everything
/// else has a cheap scalar. So the fast tier runs every second and drives the main window and the
/// trends, and the full screen every ten seconds drives the satellite table, position, and health.
/// </para>
/// <para>
/// <b>The two tiers never overlap</b>, and that is structural here rather than defended by a lock:
/// there is one loop, and it awaits each sweep before considering the next. §7.3 says the fast tier
/// will naturally stall behind a full-screen fetch and that this is acceptable — so a fast poll
/// that falls due mid-screen is skipped rather than queued. Queuing it would build a backlog of
/// readings that were already old when they were issued, which is worse than missing one.
/// </para>
/// <para>
/// A failed sweep is not fatal. <c>DeviceSessionService</c> owns the reconnect policy, so this loop
/// keeps its cadence and simply finds nothing while the link is down; the store's timestamps are
/// what tell the user the readings on screen are old (§9.11).
/// </para>
/// </remarks>
public sealed class PollingService : IAsyncDisposable
{
    /// <summary>The §7.3 fast tier, in the order the specification lists it.</summary>
    private static readonly string[] FastTier =
    [
        ":SYNC:STAT?",
        ":SYNC:TFOM?",
        ":SYNC:FFOM?",
        ":SYNC:TINT?",
        ":DIAG:ROSC:EFC:REL?",
        ":GPS:SAT:TRAC:COUN?",
    ];

    private const string FullScreenCommand = ":SYST:STAT?";

    private readonly DeviceSessionService _session;
    private readonly ReceiverStateStore _store;
    private readonly StatusScreenParser _parser;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PollingService> _logger;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;
    private int _fullRequested;

    /// <summary>Creates a poller for one session.</summary>
    public PollingService(
        DeviceSessionService session,
        ReceiverStateStore store,
        TimeProvider timeProvider,
        ILogger<PollingService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _session = session;
        _store = store;
        _timeProvider = timeProvider;
        _parser = new StatusScreenParser(timeProvider);
        _logger = logger ?? NullLogger<PollingService>.Instance;
    }

    /// <summary>The fast cadence (§7.3 default 1 s, user-settable).</summary>
    public TimeSpan FastInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The full-screen cadence (§7.3 default 10 s, user-settable).</summary>
    public TimeSpan FullInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Asks for a full status screen at the next fast tick, ahead of its cadence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §9.7.4's <c>F5</c>, "Refresh full status now". A flag rather than a direct call, because the
    /// poller owns both cadences (§12) and a screen issued from the UI thread alongside a sweep
    /// already in flight is the overlap the single-timer design exists to prevent. The wait is at
    /// most one fast tick.
    /// </para>
    /// <para>
    /// Setting it twice before the next tick asks once. There is nothing a user gains from two
    /// screens back to back, and the second would starve the fast tier for another three seconds.
    /// </para>
    /// </remarks>
    public void RequestFullSweep() => Volatile.Write(ref _fullRequested, 1);

    /// <summary>True while the loop is running.</summary>
    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>How many fast sweeps have completed. Diagnostics, and what the tests count.</summary>
    public int FastSweeps { get; private set; }

    /// <summary>How many full screens have been fetched.</summary>
    public int FullSweeps { get; private set; }

    /// <summary>Starts polling. Does nothing if already running.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
    }

    /// <summary>Stops polling and waits for the sweep in flight to finish.</summary>
    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    /// <remarks>
    /// One loop on the fast cadence, with the full screen taken whenever it falls due. Using a
    /// single <see cref="PeriodicTimer"/> is what makes the no-overlap rule structural: the timer
    /// does not queue missed ticks, so a sweep that runs long simply causes the next tick to be
    /// skipped rather than a backlog to accumulate.
    /// </remarks>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(FastInterval, _timeProvider);

        // Due immediately, so the first screen arrives with the first readings rather than ten
        // seconds later — the satellite table is most of what the user is waiting to see.
        DateTimeOffset nextFull = _timeProvider.GetUtcNow();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                bool requested = Interlocked.Exchange(ref _fullRequested, 0) == 1;

                if (requested || _timeProvider.GetUtcNow() >= nextFull)
                {
                    await PollFullAsync(cancellationToken).ConfigureAwait(false);

                    if (requested)
                    {
                        // A screen the user asked for resets the cadence rather than advancing it.
                        // Advancing would leave the scheduled sweep still due, and they would get a
                        // second screen moments after the one they pressed F5 for.
                        nextFull = _timeProvider.GetUtcNow() + FullInterval;
                    }
                    else
                    {
                        // Advance from the time it was *due*, not from the time it finished. The
                        // screen takes about 3.5 s to arrive on a 9600 baud link, so scheduling from
                        // completion silently stretches §7.3's 10 s cadence to 13.5 s — a drift that
                        // compounds and that nobody would think to look for.
                        nextFull += FullInterval;

                        // Unless it has fallen so far behind that catching up would mean two screens
                        // back to back, which would starve the fast tier for seven seconds to
                        // recover time that is already lost.
                        if (nextFull <= _timeProvider.GetUtcNow())
                        {
                            nextFull = _timeProvider.GetUtcNow() + FullInterval;
                        }
                    }
                }

                await PollFastAsync(cancellationToken).ConfigureAwait(false);

                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping is not a failure.
        }
    }

    private async Task PollFastAsync(CancellationToken cancellationToken)
    {
        string?[] answers = new string?[FastTier.Length];

        for (int i = 0; i < FastTier.Length; i++)
        {
            answers[i] = await AskAsync(FastTier[i], cancellationToken).ConfigureAwait(false);
        }

        _store.UpdateFast(
            ScalarParsers.ParseKeyword(answers[0]),
            ScalarParsers.ParseInteger(answers[1]),
            ScalarParsers.ParseInteger(answers[2]),
            ScalarParsers.ParseSecondsAsNanoseconds(answers[3]),
            ScalarParsers.ParseDecimal(answers[4]),
            ScalarParsers.ParseInteger(answers[5]));

        FastSweeps++;
    }

    private async Task PollFullAsync(CancellationToken cancellationToken)
    {
        string? screen = await AskAsync(FullScreenCommand, cancellationToken).ConfigureAwait(false);
        if (screen is null)
        {
            return;
        }

        ReceiverStatus status = _parser.Parse(screen);
        _store.UpdateFull(status);
        FullSweeps++;

        if (status.ParseWarnings.Count > 0)
        {
            _logger.LogDebug(
                "The status screen parsed with {Count} warning(s); the first is {Warning}.",
                status.ParseWarnings.Count,
                status.ParseWarnings[0]);
        }
    }

    /// <summary>
    /// Runs one catalogued command, returning its text or <see langword="null"/> if it did not
    /// answer.
    /// </summary>
    /// <remarks>
    /// Failures are swallowed deliberately. The session owns the reconnect policy and already
    /// counts what went wrong; a poller that rethrew would kill the loop on the first hiccup, and
    /// the user would see an application that stops updating rather than one that says it has lost
    /// the link.
    /// </remarks>
    private async Task<string?> AskAsync(string mnemonic, CancellationToken cancellationToken)
    {
        ScpiCommand? command = CommandCatalog.Find(mnemonic);
        if (command is null)
        {
            // Only reachable if the catalog and this list disagree, which is a bug rather than a
            // device condition — hence a warning rather than silence.
            _logger.LogWarning("{Mnemonic} is not in the command catalog and was not polled.", mnemonic);
            return null;
        }

        try
        {
            Transaction transaction = await _session.ExecuteAsync(command, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return transaction.Succeeded ? transaction.Text : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (TransportFaults.IsTransportFault(exception))
        {
            _logger.LogDebug(exception, "Polling {Mnemonic} failed.", mnemonic);
            return null;
        }
    }
}
