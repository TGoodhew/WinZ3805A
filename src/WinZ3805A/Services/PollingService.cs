using System.Globalization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinZ3805A.Device.Commands;
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
    private readonly DeviceSessionService _session;
    private readonly ReceiverStateStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PollingService> _logger;

    /// <summary>The driver for whatever is on the port right now (#287).</summary>
    /// <remarks>
    /// Read from the session per use rather than held, because the session re-selects it at every
    /// connect — a reconnect can find a different receiver on the same port, and a poller holding
    /// the old driver would sweep the new hardware with the old family's questions.
    /// </remarks>
    private IReceiverDriver Driver => _session.Driver;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;
    private int _fullRequested;

    /// <summary>
    /// The discriminator's answer under which the receiver last refused the plan's refusable query,
    /// or null.
    /// </summary>
    /// <remarks>
    /// See <see cref="PollFastAsync"/>. Holding the state rather than a bare flag is what makes the
    /// suppression self-clearing: the question is asked again the moment the receiver's state
    /// changes, so nothing has to know which states support the reading.
    /// </remarks>
    private string? _refusedUnder;

    /// <summary>How many sweeps have skipped the refusable query, for the tests to see.</summary>
    public long RefusedQuerySkips { get; private set; }

    /// <summary>Creates a poller for one session.</summary>
    /// <param name="session">The session whose transport the sweeps run over.</param>
    /// <param name="store">Where each sweep's readings are published.</param>
    /// <param name="timeProvider">
    /// Drives the cadence and stamps each sample. Injected so tests can advance it rather than wait.
    /// </param>
    /// <param name="logger">Optional; resolves to <c>NullLogger</c> when absent.</param>
    /// <param name="trends">P1-2's durable history. Optional, so a headless test needs no file.</param>
    public PollingService(
        DeviceSessionService session,
        ReceiverStateStore store,
        TimeProvider timeProvider,
        ILogger<PollingService>? logger = null,
        TrendStore? trends = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _session = session;
        _store = store;
        _timeProvider = timeProvider;
        _trends = trends;
        _logger = logger ?? NullLogger<PollingService>.Instance;
    }

    /// <summary>Overrides the fast cadence, or null to follow the driver's (§7.3 default 1 s).</summary>
    /// <remarks>
    /// Nullable since #287: the cadence is the driver's to state — it was measured on that family's
    /// hardware — and this override exists for §7.3's "user-settable" and for tests that need a
    /// cadence the clock can be wound past.
    /// </remarks>
    public TimeSpan? FastInterval { get; init; }

    /// <summary>Overrides the full-screen cadence, or null to follow the driver's (§7.3 default 10 s).</summary>
    public TimeSpan? FullInterval { get; init; }

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
        using PeriodicTimer timer = new(Positive(FastInterval ?? Driver.Cadence.Fast, TimeSpan.FromSeconds(1)), _timeProvider);

        // Due immediately, so the first screen arrives with the first readings rather than ten
        // seconds later — the satellite table is most of what the user is waiting to see.
        DateTimeOffset nextFull = _timeProvider.GetUtcNow();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // The cadence is re-read each pass because a reconnect can select a different
                // driver (#287). Written only on change: setting Period recomputes the pending
                // schedule, and doing that every second for the same value would be a way to
                // perturb timer semantics for nothing. Clamped positive because PeriodicTimer
                // throws for anything else and this loop is the one place that must not die of
                // somebody else's bug — the contract tests make a non-positive cadence loud for
                // any registered driver, so the clamp is a backstop, not the report.
                TimeSpan fast = Positive(FastInterval ?? Driver.Cadence.Fast, TimeSpan.FromSeconds(1));
                TimeSpan full = Positive(FullInterval ?? Driver.Cadence.Full, TimeSpan.FromSeconds(10));
                if (timer.Period != fast)
                {
                    timer.Period = fast;
                }

                bool requested = Interlocked.Exchange(ref _fullRequested, 0) == 1;

                if (requested || _timeProvider.GetUtcNow() >= nextFull)
                {
                    await PollFullAsync(cancellationToken).ConfigureAwait(false);

                    if (requested)
                    {
                        // A screen the user asked for resets the cadence rather than advancing it.
                        // Advancing would leave the scheduled sweep still due, and they would get a
                        // second screen moments after the one they pressed F5 for.
                        nextFull = _timeProvider.GetUtcNow() + full;
                    }
                    else
                    {
                        // Advance from the time it was *due*, not from the time it finished. The
                        // screen takes about 3.5 s to arrive on a 9600 baud link, so scheduling from
                        // completion silently stretches §7.3's 10 s cadence to 13.5 s — a drift that
                        // compounds and that nobody would think to look for.
                        nextFull += full;

                        // Unless it has fallen so far behind that catching up would mean two screens
                        // back to back, which would starve the fast tier for seven seconds to
                        // recover time that is already lost.
                        if (nextFull <= _timeProvider.GetUtcNow())
                        {
                            nextFull = _timeProvider.GetUtcNow() + full;
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
    /// Runs one fast sweep of the driver's plan, skipping a reading the receiver has said it
    /// cannot give.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The sweep is conditional on the plan's one refusable command, and it has to be.</b> The
    /// case the mechanism was built for (§7.3.1): while a SmartClock receiver is not locked there
    /// is no 1 PPS to measure against, so <c>:SYNC:TINT?</c> answers nothing and puts <c>E-230</c>
    /// in the prompt — once a second, indefinitely. On the bench receiver that overflowed the
    /// error queue outright: it began answering <c>E-350</c>, and the Diagnostics page could not
    /// drain it because the poll refilled it faster than the page emptied it (#155). Which command
    /// that is, if any, is the driver's to say (<see cref="PollPlan.RefusableIndex"/>, #287); the
    /// suppression policy is this loop's either way.
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
        // Read once, so nothing about this sweep straddles a driver swap: the answers are
        // positional, and driver B reading — or resolving, or timing — driver A's sweep would be
        // #209's misalignment made in software. Every ask below goes through this reference, not
        // the live property.
        IReceiverDriver driver = Driver;
        ForgetTheOldDriversState(driver);

        PollPlan plan = driver.Plan;
        if (plan.FastTier.Count == 0)
        {
            // Contract-breaking — the driver tests say a plan sweeps something — but the poll loop
            // is the one place that must not die of somebody else's bug (§11.1's reasoning).
            WarnOnceAboutAnEmptyPlan(driver);
            return;
        }

        string?[] answers = new string?[plan.FastTier.Count];

        // The discriminator comes first in the plan's order, which is what makes this possible at
        // all: its answer keys the refusal suppression for the rest of the sweep.
        answers[0] = await AskAsync(driver, plan.FastTier[0], cancellationToken).ConfigureAwait(false);
        string? state = ScalarParsers.ParseKeyword(answers[0]);

        for (int i = 1; i < answers.Length; i++)
        {
            if (i == plan.RefusableIndex)
            {
                if (string.Equals(_refusedUnder, state ?? string.Empty, StringComparison.Ordinal))
                {
                    RefusedQuerySkips++;
                    continue;
                }

                (answers[i], bool refused) =
                    await AskWithStatusAsync(driver, plan.FastTier[i], cancellationToken).ConfigureAwait(false);

                _refusedUnder = refused ? state ?? string.Empty : null;
                continue;
            }

            answers[i] = await AskAsync(driver, plan.FastTier[i], cancellationToken).ConfigureAwait(false);
        }

        SweepInterpretation sweep = driver.InterpretSweep(answers);
        FastReadings readings = sweep.Readings;

        LogStateChange(readings.SyncState, readings.Tfom, readings.SatellitesTracked);

        // Whether this sweep is a reading at all, decided once and applied to everything (#237) —
        // in two layers since #287, because the two questions have different owners.
        //
        // The driver owns "is this sweep mine?": #209's discriminator asks whether the sync state
        // is a state the receiver reports, and only the driver knows its own closed vocabulary.
        // That catches a slip which began before the sync state was read — but the plan's order has
        // it read on its own, ahead of the loop that reads the rest, so a slip beginning INSIDE
        // that loop leaves it correct and shifts every later answer. #237's remaining risk in one
        // sentence: a slip that leaves a plausible sync state while corrupting the others is stored
        // in full, and nothing shows it.
        //
        // So this service owns the second layer: the accepted fields are checked against bounds the
        // common currency's own definitions say they cannot cross, all documented or physical
        // rather than fitted to observed data — see ReadingPlausibility.
        string? slipped = sweep.Rejection
            ?? ReadingPlausibility.Implausible(
                readings.TimeIntervalNanoseconds,
                readings.Tfom,
                readings.Ffom,
                readings.EfcPercent,
                readings.SatellitesTracked);

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
            _timeProvider.GetUtcNow().UtcTicks,
            readings.EfcPercent,
            readings.TimeIntervalNanoseconds,
            readings.SyncState,
            readings.SatellitesTracked));

        MaybeCompact();

        _store.UpdateFast(
            readings.SyncState,
            readings.Tfom,
            readings.Ffom,
            readings.TimeIntervalNanoseconds,
            readings.EfcPercent,
            readings.SatellitesTracked);

        FastSweeps++;
    }

    /// <summary>Says once that the driver's plan sweeps nothing, then stays quiet.</summary>
    /// <remarks>
    /// Once, because the loop runs every second and the defect it reports is in a driver, not in
    /// anything that changes between sweeps — a warning a second would be #155's error-queue
    /// mistake made against the log file instead.
    /// </remarks>
    private void WarnOnceAboutAnEmptyPlan(IReceiverDriver driver)
    {
        if (_warnedAboutEmptyPlan)
        {
            return;
        }

        _warnedAboutEmptyPlan = true;

        // "The fast sweep is idle", not "nothing is being polled": the full-status query still
        // runs on its own cadence, and a log line that overstated the outage would misdirect
        // whoever reads it.
        _logger.LogWarning(
            "The {Family} driver's poll plan has no fast-tier queries; the fast sweep is idle.",
            driver.Family);
    }

    /// <summary>Whether the empty-plan warning has been given, for the current driver.</summary>
    private bool _warnedAboutEmptyPlan;

    /// <summary>The driver the poller's per-driver state belongs to.</summary>
    private IReceiverDriver? _observedDriver;

    /// <summary>
    /// Clears state that describes one driver's receiver when a different driver appears (#287).
    /// </summary>
    /// <remarks>
    /// The refusal suppression records only the discriminator's token, and state tokens are short
    /// generic words — two families can both say <c>HOLD</c>. Carrying the suppression across a
    /// swap would silently withhold the new driver's refusable query from hardware that was never
    /// asked, so both it and the empty-plan warning reset the moment a different driver is
    /// observed. Reference identity is the right test: the session hands out one instance per
    /// registered driver.
    /// </remarks>
    private void ForgetTheOldDriversState(IReceiverDriver driver)
    {
        if (ReferenceEquals(_observedDriver, driver))
        {
            return;
        }

        _observedDriver = driver;
        _refusedUnder = null;
        _warnedAboutEmptyPlan = false;
    }

    /// <summary>A backstop for a cadence the timer would throw on; the contract tests are the report.</summary>
    private static TimeSpan Positive(TimeSpan candidate, TimeSpan fallback) =>
        candidate > TimeSpan.Zero ? candidate : fallback;

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
        // Read once, so the ask and the parse cannot straddle a driver swap: whatever answered the
        // plan's query is what interprets the answer.
        IReceiverDriver driver = Driver;

        string? screen = await AskAsync(driver, driver.Plan.FullStatus, cancellationToken).ConfigureAwait(false);
        if (screen is null)
        {
            return;
        }

        ReceiverStatus status = driver.Parse(screen);
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
    private async Task<string?> AskAsync(IReceiverDriver driver, string mnemonic, CancellationToken cancellationToken) =>
        (await AskWithStatusAsync(driver, mnemonic, cancellationToken).ConfigureAwait(false)).Text;

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
        IReceiverDriver driver,
        string mnemonic,
        CancellationToken cancellationToken)
    {
        // The caller's captured driver, never the live property: the mnemonic came from that
        // driver's plan, and resolving it through whatever the session holds NOW would let a
        // mid-sweep reconnect swap pair one family's question with another's catalog entry.
        ScpiCommand? command = driver.Find(mnemonic);
        if (command is null)
        {
            // Only reachable if the driver's catalog and its own plan disagree, which is a bug in
            // the driver rather than a device condition — hence a warning rather than silence. The
            // contract tests assert the plan resolves, so a registered driver cannot get here.
            _logger.LogWarning("{Mnemonic} is not in the driver's command catalog and was not polled.", mnemonic);
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
