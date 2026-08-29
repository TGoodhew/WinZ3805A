using System.Globalization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinZ3805A.Device.Commands;
using WinZ3805A.Controls;
using WinZ3805A.Device.Drivers;
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

    /// <summary>Where <c>:SYNC:TINT?</c> sits in <see cref="FastTier"/>.</summary>
    /// <remarks>
    /// Derived rather than written as a literal, because §7.3 fixes the sweep's order and an index
    /// that drifted from it would suppress the wrong reading — silently, and only while the receiver
    /// was unlocked, which is the hardest case to notice.
    /// </remarks>
    private static readonly int TimeIntervalIndex = Array.IndexOf(FastTier, ":SYNC:TINT?");

    private const string FullScreenCommand = ":SYST:STAT?";

    private readonly DeviceSessionService _session;
    private readonly ReceiverStateStore _store;
    private readonly IReceiverDriver _driver;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PollingService> _logger;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;
    private int _fullRequested;

    /// <summary>
    /// The sync state under which the receiver last refused the time-interval query, or null.
    /// </summary>
    /// <remarks>
    /// See <see cref="PollFastAsync"/>. Holding the state rather than a bare flag is what makes the
    /// suppression self-clearing: the question is asked again the moment the receiver's state
    /// changes, so nothing has to know which states support the reading.
    /// </remarks>
    private string? _timeIntervalRefusedUnder;

    /// <summary>How many sweeps have skipped the time-interval query, for the tests to see.</summary>
    public long TimeIntervalSkips { get; private set; }

    /// <summary>Creates a poller for one session.</summary>
    /// <param name="session">The session whose transport the sweeps run over.</param>
    /// <param name="store">Where each sweep's readings are published.</param>
    /// <param name="timeProvider">
    /// Drives the cadence and stamps each sample. Injected so tests can advance it rather than wait.
    /// </param>
    /// <param name="logger">Optional; resolves to <c>NullLogger</c> when absent.</param>
    /// <param name="trends">P1-2's durable history. Optional, so a headless test needs no file.</param>
    /// <param name="driver">
    /// Which receiver family is on the port; it owns the parse. Optional, defaulting to
    /// <see cref="SmartClockDriver"/> (#122).
    /// </param>
    public PollingService(
        DeviceSessionService session,
        ReceiverStateStore store,
        TimeProvider timeProvider,
        ILogger<PollingService>? logger = null,
        TrendStore? trends = null,
        IReceiverDriver? driver = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _session = session;
        _store = store;
        _timeProvider = timeProvider;
        _trends = trends;
        // Parsing belongs to the driver: the 80x24 screen is SmartClock's shape, and a receiver
        // speaking anything else shares none of it (#122).
        _driver = driver ?? new SmartClockDriver(timeProvider);
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

    /// <summary>The durable trend history, or null when persistence is not wired up.</summary>
    private readonly TrendStore? _trends;

    /// <summary>When compaction last ran, so it does not run every sweep.</summary>
    private DateTimeOffset? _lastCompaction;

    /// <summary>The last state written to the log, so only changes are recorded.</summary>
    private string? _lastSyncState;
    private int? _lastTfom;
    private int? _lastTracked;

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

    /// <summary>
    /// Runs one §7.3 fast sweep, skipping a reading the receiver has said it cannot give.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The sweep is conditional on one command, and it has to be.</b> While the receiver is not
    /// locked there is no 1 PPS to measure against, so <c>:SYNC:TINT?</c> answers nothing and puts
    /// <c>E-230</c> in the prompt — once a second, indefinitely. On the bench receiver that
    /// overflowed the error queue outright: it began answering <c>E-350</c>, and the Diagnostics
    /// page could not drain it because the poll refilled it faster than the page emptied it (#155).
    /// </para>
    /// <para>
    /// <b>The cost is not the churn.</b> §7.2 requires the error queue to be read after every tier C
    /// command and anything non-zero surfaced, which assumes the queue holds <i>that command's</i>
    /// error. Filled with poll noise it does not, so a user applying an antenna delay while the
    /// receiver was unlocked was told about a time-interval poll instead — a fault reported that did
    /// not happen, and one that did hidden behind it.
    /// </para>
    /// <para>
    /// <b>Suppression is keyed on the sync state rather than on a list of states that support the
    /// reading.</b> Nothing here has to know which those are: the receiver is asked once, and if it
    /// refuses, it is not asked again until its own state changes. That is at most one error per
    /// transition instead of one per second, it self-clears on recovery, and it makes no claim
    /// about a sibling model whose firmware may answer where this one does not.
    /// </para>
    /// </remarks>
    private async Task PollFastAsync(CancellationToken cancellationToken)
    {
        string?[] answers = new string?[FastTier.Length];

        // The sync state comes first in §7.3's order, which is what makes this possible at all.
        answers[0] = await AskAsync(FastTier[0], cancellationToken).ConfigureAwait(false);
        string? state = ScalarParsers.ParseKeyword(answers[0]);

        for (int i = 1; i < FastTier.Length; i++)
        {
            if (i == TimeIntervalIndex)
            {
                if (string.Equals(_timeIntervalRefusedUnder, state ?? string.Empty, StringComparison.Ordinal))
                {
                    TimeIntervalSkips++;
                    continue;
                }

                (answers[i], bool refused) =
                    await AskWithStatusAsync(FastTier[i], cancellationToken).ConfigureAwait(false);

                _timeIntervalRefusedUnder = refused ? state ?? string.Empty : null;
                continue;
            }

            answers[i] = await AskAsync(FastTier[i], cancellationToken).ConfigureAwait(false);
        }

        string? syncState = state;
        int? tfom = ScalarParsers.ParseInteger(answers[1]);
        int? tracked = ScalarParsers.ParseInteger(answers[5]);

        LogStateChange(syncState, tfom, tracked);

        double? timeInterval = ScalarParsers.ParseSecondsAsNanoseconds(answers[3]);
        double? efc = ScalarParsers.ParseDecimal(answers[4]);

        int? ffom = ScalarParsers.ParseInteger(answers[2]);

        // Whether this sweep is a reading at all, decided once and applied to everything (#237).
        //
        // #209 asks whether the sync state is a state this receiver has. That catches a slip which
        // began before the sync state was read - but §7.3's order has it read on its own, ahead of
        // the loop that reads the rest, so a slip beginning INSIDE that loop leaves it correct and
        // shifts every later answer. #237's remaining risk in one sentence: a slip that leaves a
        // plausible sync state while corrupting the others is stored in full, and nothing shows it.
        //
        // So the numeric fields are checked against bounds they cannot cross, all documented or
        // physical rather than fitted to observed data - see ReadingPlausibility.
        string? slipped = IsCoherent(syncState)
            ? ReadingPlausibility.Implausible(timeInterval, tfom, ffom, efc, tracked)
            : $"the sync state read \"{Summarise(syncState)}\", which is not a state this receiver reports";

        if (slipped is not null)
        {
            // Information, because the application ships at Information and this is a reading the
            // user will not find in the trend later. A Debug line would make it invisible exactly
            // when somebody is asking why the series has a gap (#14 made the same mistake). The
            // reason names the field, so a field report says WHICH one slipped.
            _logger.LogInformation(
                "Dropped a reading that cannot have come from the receiver: {Reason}. "
                + "The link may have misaligned.",
                slipped);

            // Deliberately not applied to the store either (#237). It used to be: the sweep was
            // kept out of the durable series and then handed to the UI anyway, so a slip showed as
            // the medallion flickering through a state the receiver was never in. §9.11 would
            // rather the last good reading stayed on screen with its staleness climbing - "an old
            // reading with an honest timestamp beats an empty field", and it beats a wrong one by
            // further still.
            FastSweeps++;
            return;
        }

        // P1-2: every fast sweep is a row. Append never throws (see TrendStore), so a locked file
        // or a full disk costs a gap in the trend rather than the polling cadence itself.
        _trends?.Append(new TrendRecord(
            _timeProvider.GetUtcNow().UtcTicks, efc, timeInterval, syncState, tracked));

        MaybeCompact();

        _store.UpdateFast(
            syncState,
            tfom,
            ffom,
            timeInterval,
            efc,
            tracked);

        FastSweeps++;
    }

    /// <summary>
    /// Records mode, figure of merit and satellite count when any of them moves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>On change, never per sweep.</b> §7.3 polls once a second on a receiver §1 expects to be
    /// left running for weeks; a line a second is 2.5 million lines a month and nothing anyone
    /// would read. A line when something moves is a few dozen a day and is the whole history of the
    /// session.
    /// </para>
    /// <para>
    /// This exists because the interesting faults on this hardware are intermittent. A satellite
    /// count wandering between four and zero over hours is invisible to someone glancing at the
    /// window and obvious in a file — and it is the one diagnosis available when the receiver
    /// cannot be reached physically. Information level, so it survives the default configuration.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Whether a sweep is a reading at all, rather than the tail of another command's reply (#209).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The discriminator is the sync state, because it is the one field with a closed set of legal
    /// values - <c>LOCK</c>, <c>REC</c>, <c>WAIT</c>, <c>HOLD</c>, <c>POW</c>, <c>OFF</c>. Anything
    /// else did not come from <c>:SYNC:STAT?</c>.
    /// </para>
    /// <para>
    /// <b>The whole sweep is dropped, not the offending field</b>, and that is the point. When the
    /// link had misaligned on 24 Aug the sync state held a diagnostic log dump, and the same sweep's
    /// other fields held a time interval of two seconds and an EFC of +2 % - the second of which is
    /// inside the control range and indistinguishable from a real reading by magnitude. No
    /// per-field range check catches that one. What identifies it is the company it keeps.
    /// </para>
    /// <para>
    /// <b>The cost is real and is logged.</b> A sync state this application has not been taught
    /// would drop rows while looking healthy, so every rejection says what it saw. A silent guard
    /// here would be worse than the defect it prevents.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <c>ReceiverModes</c> lives under <c>Controls/</c> and is used from here anyway, deliberately:
    /// it holds the one definition of the closed set, it speaks no WinUI, and it is already linked
    /// into the headless test project. Restating the six tokens here would be the second copy of a
    /// list that has to stay identical, which is a worse problem than a using directive.
    /// </remarks>
    private static bool IsCoherent(string? syncState) =>
        ReceiverModes.FromSyncState(syncState) != ReceiverMode.Disconnected;

    /// <summary>A rejected sync state, short enough to log and long enough to recognise.</summary>
    private static string Summarise(string? syncState)
    {
        if (string.IsNullOrWhiteSpace(syncState))
        {
            return "(empty)";
        }

        string oneLine = syncState.ReplaceLineEndings(" ").Trim();

        return oneLine.Length <= 60 ? oneLine : oneLine[..60] + "…";
    }

    private void LogStateChange(string? syncState, int? tfom, int? tracked)
    {
        if (syncState == _lastSyncState && tfom == _lastTfom && tracked == _lastTracked)
        {
            return;
        }

        // The first sweep of a session is a change from nothing, and is worth a line: it is the
        // baseline every later entry is read against.
        _logger.LogInformation(
            "State: {SyncState}, TFOM {Tfom}, {Tracked} satellite(s) tracked.",
            syncState ?? "unknown",
            tfom?.ToString(CultureInfo.InvariantCulture) ?? "—",
            tracked?.ToString(CultureInfo.InvariantCulture) ?? "—");

        _lastSyncState = syncState;
        _lastTfom = tfom;
        _lastTracked = tracked;
    }

    /// <summary>
    /// Thins and prunes the trend store, occasionally.
    /// </summary>
    /// <remarks>
    /// Hourly rather than per sweep. §12's compaction rewrites rows a day old and older, so running
    /// it every second would be a full table scan a second to reclaim ten seconds of rows — and the
    /// file is bounded by retention either way. Once on the first sweep as well, so an application
    /// that is only ever open briefly still tidies what the last session left.
    /// </remarks>
    private void MaybeCompact()
    {
        if (_trends is null)
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (_lastCompaction is DateTimeOffset last && now - last < TimeSpan.FromHours(1))
        {
            return;
        }

        _lastCompaction = now;
        _trends.Compact(now.UtcTicks);
    }

    private async Task PollFullAsync(CancellationToken cancellationToken)
    {
        string? screen = await AskAsync(FullScreenCommand, cancellationToken).ConfigureAwait(false);
        if (screen is null)
        {
            return;
        }

        ReceiverStatus status = _driver.Parse(screen);
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
    private async Task<string?> AskAsync(string mnemonic, CancellationToken cancellationToken) =>
        (await AskWithStatusAsync(mnemonic, cancellationToken).ConfigureAwait(false)).Text;

    /// <summary>
    /// As <see cref="AskAsync"/>, and also reports whether the receiver <i>refused</i> the command.
    /// </summary>
    /// <returns>
    /// <c>Refused</c> is true only when the receiver answered and rejected it — an error token in
    /// the prompt. A timeout, a dropped link or an uncatalogued mnemonic are all false: those say
    /// nothing about whether the receiver would have answered, and suppressing a reading because
    /// the cable was unplugged would keep it suppressed after it was plugged back in.
    /// </returns>
    private async Task<(string? Text, bool Refused)> AskWithStatusAsync(
        string mnemonic,
        CancellationToken cancellationToken)
    {
        ScpiCommand? command = CommandCatalog.Find(mnemonic);
        if (command is null)
        {
            // Only reachable if the catalog and this list disagree, which is a bug rather than a
            // device condition — hence a warning rather than silence.
            _logger.LogWarning("{Mnemonic} is not in the command catalog and was not polled.", mnemonic);
            return (null, false);
        }

        try
        {
            Transaction transaction = await _session.ExecuteAsync(command, origin: CommandOrigin.Poll, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // WasRejected, not ErrorQueueNotEmpty: this drives the "do not ask again until the
            // sync state changes" suppression, and an unrelated queued error must not silence a
            // query that is answering perfectly well (#173).
            return (transaction.Succeeded ? transaction.Text : null, transaction.WasRejected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (TransportFaults.IsTransportFault(exception))
        {
            _logger.LogDebug(exception, "Polling {Mnemonic} failed.", mnemonic);
            return (null, false);
        }
    }
}
