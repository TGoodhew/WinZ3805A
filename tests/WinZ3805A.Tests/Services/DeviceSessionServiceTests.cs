using System.Threading.Channels;

using Microsoft.Extensions.Time.Testing;
using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// The session layer: connect, auto-detect, one-at-a-time serialisation, and the §7.2 reconnect
/// policy (P0-1, P0-14, §15 step 4).
/// </summary>
/// <remarks>
/// <para>
/// Driven through <see cref="ControllableTransport"/> rather than <c>FakeTransport</c>, for one
/// specific reason: <c>SynchroniseAsync</c> opens a session by <em>reading</em> before it writes
/// anything, because the real unit announces itself when DTR is asserted. A transport that only
/// speaks when spoken to gives that read nothing to find, so the connect path's only exit is its
/// timeout — and on a pinned clock the timeout never arrives, so the test hangs rather than fails.
/// </para>
/// <para>
/// The pinned clock is worth that trouble: it makes a 30 s backoff cap assertable in milliseconds.
/// The hardware half of these requirements is exercised separately against the receiver on COM3.
/// </para>
/// </remarks>
public class DeviceSessionServiceTests
{
    private const string Identity = "SYMMETRICOM,Z3805A,3625A02931,1.01.03-A";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static ScpiCommand Status => CommandCatalog.Find(":SYNC:STAT?")!;

    /// <summary>A receiver that behaves like the one on the bench: banner on open, then answers.</summary>
    private static ControllableTransport Receiver() =>
        new(command => command.StartsWith("*IDN", StringComparison.OrdinalIgnoreCase) ? Identity : "LOCK")
        {
            Banner = Identity,
        };

    /// <summary>
    /// A port at the wrong line settings. It answers — with rubbish. Modelling this as silence would
    /// be both less faithful and far slower, because silence has to be waited out.
    /// </summary>
    private static ControllableTransport WrongSettings() => new(_ => "ÿþ garbage");

    /// <summary>
    /// Runs a task that is waiting on a <c>LineProtocol</c> timeout, winding the pinned clock
    /// forward until it completes.
    /// </summary>
    /// <remarks>
    /// The timeout is a <c>CancellationTokenSource</c> built on the injected <c>TimeProvider</c>, so
    /// on a fake clock it never fires by itself. Advancing in a loop rather than once avoids racing
    /// the moment the source is registered: a single jump made before registration would schedule
    /// the timeout from the new "now" and wait forever.
    /// </remarks>
    private static async Task<T> AdvanceUntilComplete<T>(FakeTimeProvider clock, Task<T> task)
    {
        while (!task.IsCompleted)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(5);
        }

        return await task;
    }

    // -------------------------------------------------------------------------------------
    // Connect
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task ConnectingReportsTheIdentityAndTheConnectedState()
    {
        await using DeviceSessionService session = new((_, _) => Receiver(), new FakeTimeProvider());
        List<ConnectionStatus> seen = [];
        session.StatusChanged += (_, e) => seen.Add(e.Status);

        bool connected = await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout);

        Assert.True(connected);
        Assert.Equal(ConnectionStatus.Connected, session.Status);
        Assert.Equal(Identity, session.Identity);
        Assert.Equal([ConnectionStatus.Connecting, ConnectionStatus.Connected], seen);
    }

    /// <summary>
    /// A wrong baud rate does not produce silence, it produces bytes — so a reply that is not shaped
    /// like an identity must not be accepted, or auto-detect settles on the first setting that
    /// returned anything at all.
    /// </summary>
    [Fact]
    public async Task AReplyThatIsNotAnIdentityIsNotAcceptedAsAConnection()
    {
        await using DeviceSessionService session = new((_, _) => WrongSettings(), new FakeTimeProvider());

        bool connected = await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout);

        Assert.False(connected);
        Assert.NotEqual(ConnectionStatus.Connected, session.Status);
        Assert.Null(session.Identity);
    }

    /// <summary>An intentional disconnect is not a fault, and §9.11 renders the two differently.</summary>
    [Fact]
    public async Task DisconnectingIsReportedAsDisconnectedRatherThanFaulted()
    {
        await using DeviceSessionService session = new((_, _) => Receiver(), new FakeTimeProvider());
        await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout);

        await session.DisconnectAsync().WaitAsync(TestTimeout);

        Assert.Equal(ConnectionStatus.Disconnected, session.Status);
    }

    // -------------------------------------------------------------------------------------
    // Auto-detect (§10.12)
    // -------------------------------------------------------------------------------------

    /// <summary>A Z3805A ships 9600-8-N-1, which §10.12 puts first so the common case costs one attempt.</summary>
    [Fact]
    public async Task AutoDetectSettlesOnTheFirstCombinationForAZ3805A()
    {
        List<SerialSettings> tried = [];
        await using DeviceSessionService session = new(
            (_, settings) =>
            {
                tried.Add(settings);
                return settings is { BaudRate: 9600, DataBits: 8 } ? Receiver() : WrongSettings();
            },
            new FakeTimeProvider());

        SerialSettings? found = await session.AutoDetectAsync("COM3").WaitAsync(TestTimeout);

        Assert.NotNull(found);
        Assert.Equal(9600, found.BaudRate);
        Assert.Single(tried);
        Assert.Equal(ConnectionStatus.Connected, session.Status);
    }

    /// <summary>
    /// A Z3801A is commonly 19200-7-E-1, which §10.12 puts second. This also proves the walk
    /// continues past a combination that answered with nothing usable.
    /// </summary>
    [Fact]
    public async Task AutoDetectWalksOnToTheSecondCombinationForAZ3801A()
    {
        List<SerialSettings> tried = [];
        await using DeviceSessionService session = new(
            (_, settings) =>
            {
                tried.Add(settings);
                return settings is { BaudRate: 19200, DataBits: 7 } ? Receiver() : WrongSettings();
            },
            new FakeTimeProvider());

        SerialSettings? found = await session.AutoDetectAsync("COM3").WaitAsync(TestTimeout);

        Assert.NotNull(found);
        Assert.Equal(19200, found.BaudRate);
        Assert.Equal(7, found.DataBits);
        Assert.Equal(2, tried.Count);
    }

    [Fact]
    public async Task AutoDetectTriesEveryCombinationAndReportsProgress()
    {
        List<SerialSettings> reported = [];
        await using DeviceSessionService session = new((_, _) => WrongSettings(), new FakeTimeProvider());

        SerialSettings? found = await session
            .AutoDetectAsync("COM3", new Progress<SerialSettings>(reported.Add))
            .WaitAsync(TestTimeout);

        Assert.Null(found);
        Assert.Equal(ConnectionStatus.Faulted, session.Status);
        Assert.Equal(SerialSettings.AutoDetectSequence.Count, reported.Count);
    }

    /// <summary>§10.12 requires the walk to be cancellable, because eight attempts is a long wait.</summary>
    [Fact]
    public async Task AutoDetectCanBeCancelled()
    {
        using CancellationTokenSource cts = new();
        await using DeviceSessionService session = new(
            (_, _) =>
            {
                cts.Cancel();
                return WrongSettings();
            },
            new FakeTimeProvider());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.AutoDetectAsync("COM3", cancellationToken: cts.Token));
    }

    // -------------------------------------------------------------------------------------
    // One transaction at a time (§7.2)
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// §7.2 allows no exceptions to this. Two overlapping transactions do not fail loudly — they
    /// interleave and hand each caller the other's answer, which is the worst possible failure for
    /// an application whose whole job is faithful reporting.
    /// </summary>
    [Fact]
    public async Task CommandsAreServedOneAtATimeAndInOrder()
    {
        int inFlight = 0;
        int maxInFlight = 0;
        object gate = new();

        ControllableTransport transport = new(command =>
        {
            lock (gate)
            {
                inFlight++;
                maxInFlight = Math.Max(maxInFlight, inFlight);
                inFlight--;
            }

            return command.StartsWith("*IDN", StringComparison.OrdinalIgnoreCase) ? Identity : "LOCK";
        })
        { Banner = Identity };

        await using DeviceSessionService session = new((_, _) => transport, new FakeTimeProvider());
        await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout);

        Task<Transaction>[] all = [.. Enumerable.Range(0, 8).Select(_ => session.ExecuteAsync(Status))];
        await Task.WhenAll(all).WaitAsync(TestTimeout);

        Assert.Equal(1, maxInFlight);
        Assert.All(all, t => Assert.Equal(TransactionOutcome.Completed, t.Result.Outcome));
    }

    [Fact]
    public async Task ACommandCarriesItsArgument()
    {
        ControllableTransport transport = Receiver();
        await using DeviceSessionService session = new((_, _) => transport, new FakeTimeProvider());
        await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout);

        await session.ExecuteAsync(CommandCatalog.Find(":GPS:SAT:TRAC:IGN:STAT?")!, "18").WaitAsync(TestTimeout);

        Assert.Contains(transport.CommandsWritten, c => c.EndsWith(" 18", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecutingBeforeConnectingFailsRatherThanHanging()
    {
        await using DeviceSessionService session = new((_, _) => Receiver(), new FakeTimeProvider());

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.ExecuteAsync(Status, cancellationToken: cts.Token));
    }

    // -------------------------------------------------------------------------------------
    // Losing the link (§7.2, P0-14)
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// One timeout is ordinary on a busy receiver; three in a row means the link is gone. Dropping
    /// a working session for a single slow reply would make the application flap.
    /// </summary>
    [Fact]
    public async Task ASingleTimeoutDoesNotDropAWorkingSession()
    {
        ControllableTransport transport = Receiver();
        FakeTimeProvider clock = new();
        await using DeviceSessionService session = new((_, _) => transport, clock);
        await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout);

        transport.Behaviour = TransportBehaviour.Silent;
        Transaction timedOut = await AdvanceUntilComplete(clock, session.ExecuteAsync(Status)).WaitAsync(TestTimeout);

        Assert.Equal(TransactionOutcome.TimedOut, timedOut.Outcome);
        Assert.Equal(ConnectionStatus.Connected, session.Status);
    }

    /// <summary>Three consecutive timeouts are §7.2's other trigger, alongside an outright fault.</summary>
    [Fact]
    public async Task ThreeConsecutiveTimeoutsDropTheSession()
    {
        ControllableTransport transport = Receiver();
        FakeTimeProvider clock = new();
        await using DeviceSessionService session = new((_, _) => transport, clock) { StayConnected = false };
        await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout);

        transport.Behaviour = TransportBehaviour.Silent;
        for (int i = 0; i < 3; i++)
        {
            await AdvanceUntilComplete(clock, session.ExecuteAsync(Status)).WaitAsync(TestTimeout);
        }

        Assert.Equal(ConnectionStatus.Faulted, session.Status);
    }

    /// <summary>
    /// The P0-14 case: the adapter is pulled mid-session. What matters is that the failure is
    /// reported rather than escaping — <c>SerialPort</c> has a long history of turning this into a
    /// process kill. Whether the caller sees a faulted transaction or an exception is a detail;
    /// surviving it is not.
    /// </summary>
    [Fact]
    public async Task APulledAdapterIsReportedRatherThanKillingTheProcess()
    {
        ControllableTransport transport = Receiver();
        await using DeviceSessionService session = new((_, _) => transport, new FakeTimeProvider())
        {
            StayConnected = false,
        };

        await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout);
        transport.Behaviour = TransportBehaviour.Faulting;

        await RunAndTolerateFailureAsync(session);

        Assert.Equal(ConnectionStatus.Faulted, session.Status);
    }

    /// <summary>
    /// With retry enabled the same loss becomes Reconnecting, which §9.11 gives a countdown rather
    /// than the flat failure Faulted gets.
    /// </summary>
    [Fact]
    public async Task WithRetryOnALostLinkBecomesReconnecting()
    {
        ControllableTransport transport = Receiver();
        await using DeviceSessionService session = new((_, _) => transport, new FakeTimeProvider())
        {
            StayConnected = true,
        };

        await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout);
        transport.Behaviour = TransportBehaviour.Faulting;

        await RunAndTolerateFailureAsync(session);

        Assert.Equal(ConnectionStatus.Reconnecting, session.Status);
    }

    /// <summary>
    /// The adapter comes back. §7.2's backoff starts at 2 s, so winding the clock is what makes this
    /// assertable without waiting through it.
    /// </summary>
    [Fact]
    public async Task ARestoredLinkReconnectsOnTheBackoff()
    {
        // A fresh transport per attempt, because that is what the real factory does: reconnecting
        // closes the port and opens a new one. Handing back a single instance would hand back a
        // disposed one, and the retry could never succeed for a reason the service is not at fault
        // for.
        int portsOpened = 0;
        ControllableTransport? live = null;
        FakeTimeProvider clock = new();

        await using DeviceSessionService session = new(
            (_, _) =>
            {
                portsOpened++;
                live = Receiver();
                return live;
            },
            clock)
        { StayConnected = true };

        await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout);
        live!.Behaviour = TransportBehaviour.Faulting;
        await RunAndTolerateFailureAsync(session);
        Assert.Equal(ConnectionStatus.Reconnecting, session.Status);

        // The adapter is back, so the next attempt on the backoff finds a working port.
        //
        // Latched from StatusChanged rather than sampled from Status (#192, and #198's lesson).
        // BeginReconnect starts the retry loop fire-and-forget, so nothing here owns the task that
        // produces the result being asserted on: polling Status after a wall-clock budget asks
        // whether that loop happened to have run *yet*, which in a full suite it competes with
        // every other test to do. Alone this test passed 25 runs out of 25; in the suite it failed
        // about one run in six. The signal is the answer, the property is a sample of it.
        TaskCompletionSource reconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void Latch(object? sender, ConnectionStatusChanged change)
        {
            if (change.Status == ConnectionStatus.Connected)
            {
                reconnected.TrySetResult();
            }
        }

        session.StatusChanged += Latch;

        try
        {
            // A step larger than MaximumBackoff, so one advance always clears whatever the retry
            // loop is waiting on. Stepping 4 s against a 30 s cap needed eight advances per attempt
            // once the backoff saturated, which is eight scheduling round-trips bought for nothing.
            using CancellationTokenSource giveUp = new(TestTimeout);
            while (!reconnected.Task.IsCompleted && !giveUp.IsCancellationRequested)
            {
                clock.Advance(TimeSpan.FromMinutes(1));

                // Returns the moment the service signals rather than sleeping a fixed slice, so a
                // loaded machine costs iterations instead of the whole budget.
                await Task.WhenAny(reconnected.Task, Task.Delay(5, CancellationToken.None));
            }

            await reconnected.Task.WaitAsync(TestTimeout);
        }
        finally
        {
            session.StatusChanged -= Latch;
        }

        Assert.Equal(ConnectionStatus.Connected, session.Status);
        Assert.True(portsOpened >= 2, $"Expected the port to be reopened, saw {portsOpened}.");
    }

    /// <summary>
    /// Runs one command over a link that is about to fail, tolerating either shape of failure. A
    /// <see cref="TimeoutException"/> is not tolerated: that would mean the call hung, which is a
    /// real defect rather than a detail.
    /// </summary>
    private static async Task RunAndTolerateFailureAsync(DeviceSessionService session)
    {
        try
        {
            await session.ExecuteAsync(Status).WaitAsync(TestTimeout);
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception)
        {
            // Expected: the link failed.
        }
    }

    // -------------------------------------------------------------------------------------
    // §12 — per-device, no static state
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// §12 requires this to be instantiable per device even though v1 creates one. Two sessions on
    /// two ports must not see each other, which is what "no static state for connection or device
    /// identity" means in practice.
    /// </summary>
    [Fact]
    public async Task TwoSessionsOnTwoPortsAreIndependent()
    {
        await using DeviceSessionService first = new(
            (port, _) => new ControllableTransport(c =>
                c.StartsWith("*IDN", StringComparison.OrdinalIgnoreCase) ? $"ACME,ONE,{port},1.0" : "0")
            { Banner = "ACME,ONE,BANNER,1.0" },
            new FakeTimeProvider());

        await using DeviceSessionService second = new(
            (port, _) => new ControllableTransport(c =>
                c.StartsWith("*IDN", StringComparison.OrdinalIgnoreCase) ? $"ACME,TWO,{port},2.0" : "0")
            { Banner = "ACME,TWO,BANNER,2.0" },
            new FakeTimeProvider());

        await first.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout);
        await second.ConnectAsync("COM7", SerialSettings.Default).WaitAsync(TestTimeout);

        Assert.Equal("COM3", first.PortName);
        Assert.Equal("COM7", second.PortName);
        Assert.Contains("ONE", first.Identity);
        Assert.Contains("TWO", second.Identity);

        await first.DisconnectAsync().WaitAsync(TestTimeout);

        Assert.Equal(ConnectionStatus.Disconnected, first.Status);
        Assert.Equal(ConnectionStatus.Connected, second.Status);
    }

    [Fact]
    public async Task DisposingTwiceIsHarmless()
    {
        DeviceSessionService session = new((_, _) => Receiver(), new FakeTimeProvider());
        await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout);

        await session.DisposeAsync();
        await session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ExecuteAsync(Status));
    }

    // -------------------------------------------------------------------------------------
    // A caller is never left waiting on a completion nobody will set (#259)
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Asserts a caller was <i>released</i> rather than left waiting, and only then how it ended.
    /// </summary>
    /// <remarks>
    /// <b>Written this way after the obvious version proved vacuous.</b>
    /// <c>Assert.ThrowsAnyAsync(() =&gt; task.WaitAsync(timeout))</c> reads like it checks the task
    /// failed, and it passes when the task never completes at all — because <c>WaitAsync</c> itself
    /// throws <c>TimeoutException</c>, which is an exception like any other. Two of the three tests
    /// below passed against the unfixed code until this separated the two questions: first that the
    /// caller came back, then what it came back with.
    /// </remarks>
    private static async Task AssertReleasedAsync(Task task)
    {
        await Task.WhenAny(task, Task.Delay(TestTimeout));

        Assert.True(task.IsCompleted, "the caller was left waiting on a completion nobody would set (#259)");
        await Assert.ThrowsAnyAsync<Exception>(() => task);
    }

    /// <summary>Tearing the session down while a command is in flight completes that command.</summary>
    /// <remarks>
    /// <para>
    /// <b>The regression this exists for.</b> A caller awaits <c>pending.Completion</c> bounded only
    /// by its own token, so a completion that is never set is not a slow command — it is a caller
    /// that never returns. <c>PollingService</c> passes a token it does not cancel, so it waited for
    /// the life of the process: alive, holding its sweep, ignoring the refresh flag, logging
    /// nothing, with the session still reporting Connected.
    /// </para>
    /// <para>
    /// The escape was the <c>OperationCanceledException</c> raised when teardown cancels the session
    /// token under an in-flight command. It is neither the caller cancelling — the filter there
    /// tests the caller's token — nor a transport fault, so it matched no catch, escaped to the
    /// pump, and ended it as an ordinary shutdown with this caller still waiting.
    /// </para>
    /// <para>
    /// <see cref="TransportBehaviour.Silent"/> rather than a faulting transport, and the distinction
    /// is the whole of #259: a pulled adapter throws <c>IOException</c>, which is a transport fault
    /// and was always handled. A receiver being power-cycled throws nothing at all — the handle
    /// stays valid and the far end simply stops replying — which is why that case wedged the app and
    /// the unplug case never did.
    /// </para>
    /// <para>
    /// Without the fix this test does not fail, it <b>hangs</b>, which is why every wait is bounded.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TearingDownWhileACommandIsInFlightDoesNotStrandTheCaller()
    {
        ControllableTransport receiver = Receiver();
        await using DeviceSessionService session = new((_, _) => receiver, new FakeTimeProvider());

        Assert.True(await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout));

        // Answers during connect, then stops — and the clock is pinned, so the LineProtocol timeout
        // cannot fire on its own and the command stays genuinely in flight.
        receiver.Behaviour = TransportBehaviour.Silent;
        Task<Transaction> inFlight = session.ExecuteAsync(Status);

        // In flight means the command reached the wire, which the transport says — not that 50 ms
        // went by, which was the old approximation of it (#326).
        await receiver.NextWriteAsync(TestTimeout);
        Assert.False(inFlight.IsCompleted, "the command should still be in flight");

        await session.DisconnectAsync().WaitAsync(TestTimeout);

        // The assertion is that this returns at all. What it returns is secondary — a fault and a
        // cancellation are both honest answers to "the session went away underneath you"; waiting
        // for ever is not.
        await AssertReleasedAsync(inFlight);
    }

    /// <summary>A command still queued when the session ends is failed rather than kept.</summary>
    /// <remarks>
    /// <c>PumpAsync</c> has always claimed "queued callers are failed by TearDownAsync" and until
    /// #259 that was not true — the channel kept them for whichever pump started next. So a poll
    /// queued before an outage could be sent minutes later against a different connection, and a
    /// tier C command the user confirmed before the link dropped could execute after it came back
    /// without being confirmed again.
    /// </remarks>
    [Fact]
    public async Task ACommandStillQueuedWhenTheSessionEndsIsFailed()
    {
        ControllableTransport receiver = Receiver();
        await using DeviceSessionService session = new((_, _) => receiver, new FakeTimeProvider());

        Assert.True(await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout));

        receiver.Behaviour = TransportBehaviour.Silent;

        // The first occupies the pump; the second is left in the channel behind it. Waiting for the
        // first to reach the wire is what makes that true — the pump serves one at a time, so a
        // command on the wire is a command the pump is still busy with (#326).
        Task<Transaction> inFlight = session.ExecuteAsync(Status);
        Task<Transaction> queued = session.ExecuteAsync(Status);
        await receiver.NextWriteAsync(TestTimeout);

        await session.DisconnectAsync().WaitAsync(TestTimeout);

        await AssertReleasedAsync(inFlight);
        await AssertReleasedAsync(queued);
    }

    /// <summary>The session works again afterwards, which is the state the application is left in.</summary>
    /// <remarks>
    /// A reconnect is teardown followed by a fresh open, so this is the shape of the real failure:
    /// the session came back, said Connected, and the caller stranded by the teardown never
    /// returned. Asserting that a later command answers is asserting the app is usable again.
    /// </remarks>
    [Fact]
    public async Task ASessionTornDownMidCommandStillWorksAfterwards()
    {
        ControllableTransport first = Receiver();
        ControllableTransport second = Receiver();
        int opened = 0;

        await using DeviceSessionService session = new(
            (_, _) => ++opened == 1 ? first : second,
            new FakeTimeProvider());

        Assert.True(await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout));

        first.Behaviour = TransportBehaviour.Silent;
        Task<Transaction> stranded = session.ExecuteAsync(Status);
        await first.NextWriteAsync(TestTimeout);

        await session.DisconnectAsync().WaitAsync(TestTimeout);
        await AssertReleasedAsync(stranded);

        Assert.True(await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout));

        Transaction after = await session.ExecuteAsync(Status).WaitAsync(TestTimeout);
        Assert.True(after.Succeeded);
    }

    // -------------------------------------------------------------------------------------
    // The retry schedule §9.11's countdown needs, and its two actions (#248)
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Records every change to <see cref="DeviceSessionService.NextRetryAt"/> so a test can await
    /// the next one instead of sampling for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The queue is what makes this race-free. The reconnect loop runs fire-and-forget, so a change
    /// can happen before the test gets round to asking for it; buffering every transition means the
    /// test reads one that has already occurred rather than waiting for one that never comes again.
    /// Subscribing has to happen before the session is faulted, which is why
    /// <see cref="ReconnectingSessionAsync"/> attaches it and hands it back.
    /// </para>
    /// <para>
    /// This replaces three <c>Task.Delay(10)</c> polling loops with wall-clock budgets (#326). They
    /// asserted the right properties and failed for the wrong reason: on a loaded machine the
    /// background loop had not reached its wait inside the budget, which says nothing about the
    /// schedule. Every flake this repository has had is that shape.
    /// </para>
    /// </remarks>
    private sealed class RetrySchedule : IDisposable
    {
        private readonly DeviceSessionService _session;
        private readonly Channel<DateTimeOffset?> _changes =
            Channel.CreateUnbounded<DateTimeOffset?>(new UnboundedChannelOptions { SingleReader = true });

        public RetrySchedule(DeviceSessionService session)
        {
            _session = session;
            _session.RetryScheduleChanged += OnChanged;
        }

        /// <summary>The next change, whatever it is.</summary>
        public async Task<DateTimeOffset?> NextChangeAsync()
        {
            using CancellationTokenSource giveUp = new(TestTimeout);
            return await _changes.Reader.ReadAsync(giveUp.Token);
        }

        /// <summary>The next change that schedules an attempt, skipping any that clear one.</summary>
        public async Task<DateTimeOffset> NextScheduledAsync()
        {
            while (true)
            {
                if (await NextChangeAsync() is DateTimeOffset due)
                {
                    return due;
                }
            }
        }

        public void Dispose() => _session.RetryScheduleChanged -= OnChanged;

        private void OnChanged() => _changes.Writer.TryWrite(_session.NextRetryAt);
    }

    /// <summary>
    /// Drops a connected session into Reconnecting and returns it with a watcher on its retry
    /// schedule, subscribed before the fault so no transition can be missed.
    /// </summary>
    private static async Task<(DeviceSessionService Session, RetrySchedule Schedule)> ReconnectingSessionAsync(
        ControllableTransport transport,
        FakeTimeProvider clock)
    {
        DeviceSessionService session = new((_, _) => transport, clock) { StayConnected = true };
        RetrySchedule schedule = new(session);

        await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout);
        transport.Behaviour = TransportBehaviour.Faulting;
        await RunAndTolerateFailureAsync(session);

        Assert.Equal(ConnectionStatus.Reconnecting, session.Status);
        return (session, schedule);
    }

    /// <summary>While it is waiting to retry, the session says when the next attempt is due.</summary>
    /// <remarks>
    /// §9.11's row wants "Retrying in 4 seconds", which needs the schedule and not merely the fact
    /// of retrying. Published as an instant rather than a remaining count so a caller can tick it
    /// against its own clock instead of this class raising an event per second.
    /// </remarks>
    [Fact]
    public async Task WhileWaitingToRetryTheNextAttemptIsPublished()
    {
        FakeTimeProvider clock = new();
        (DeviceSessionService session, RetrySchedule schedule) = await ReconnectingSessionAsync(Receiver(), clock);
        await using DeviceSessionService _ = session;
        using RetrySchedule watcher = schedule;

        DateTimeOffset due = await watcher.NextScheduledAsync();

        // The first backoff is 2 s (§7.2), so the first attempt is due about then and never behind.
        Assert.InRange(due - clock.GetUtcNow(), TimeSpan.Zero, TimeSpan.FromSeconds(2));
        Assert.Equal(due, session.NextRetryAt);
    }

    /// <summary>Stop retrying leaves the link faulted rather than disconnected.</summary>
    /// <remarks>
    /// <para>
    /// The distinction is §9.11's. <c>Disconnected</c> is a state the user chose for the <i>link</i>
    /// and offers "Choose a port". This is the user declining to keep <i>retrying</i> a link that
    /// dropped underneath them, which is still a fault — the receiver is not there, and telling them
    /// to choose a port would suggest the port was the problem.
    /// </para>
    /// <para>
    /// It also stops the loop, which is the point: without it the only way out of a retry that caps
    /// at thirty seconds was to close the application.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task StopRetryingFaultsTheSessionAndEndsTheLoop()
    {
        FakeTimeProvider clock = new();
        ControllableTransport receiver = Receiver();
        (DeviceSessionService session, RetrySchedule schedule) = await ReconnectingSessionAsync(receiver, clock);
        await using DeviceSessionService _ = session;
        using RetrySchedule watcher = schedule;

        int opensBefore = receiver.OpenCount;
        session.StopRetrying();

        Assert.Equal(ConnectionStatus.Faulted, session.Status);
        Assert.Null(session.NextRetryAt);

        // The loop is gone: winding well past the cap produces no further attempts and no change.
        //
        // A budget is the right instrument here and the wrong one above (#326). This asserts that
        // something does NOT happen, so time can only make the test more thorough: too little and it
        // misses a live loop, never too little and it fails a dead one. The waits this file used to
        // open with were the opposite — they asserted something DOES happen, so running out of time
        // failed a session that was merely slow.
        clock.Advance(TimeSpan.FromMinutes(2));
        await Task.Delay(50);

        Assert.Equal(opensBefore, receiver.OpenCount);

        Assert.Equal(ConnectionStatus.Faulted, session.Status);
    }

    /// <summary>Stop retrying does not rewrite the "Reconnect automatically" preference.</summary>
    /// <remarks>
    /// <c>StayConnected</c> is §10.12's setting and governs every future outage. One press of a
    /// button during one of them must not silently turn it off — the user said "stop this retry",
    /// not "never retry again", and the difference would only be discovered the next time something
    /// failed to come back on its own.
    /// </remarks>
    [Fact]
    public async Task StopRetryingLeavesTheReconnectPreferenceAlone()
    {
        FakeTimeProvider clock = new();
        (DeviceSessionService session, RetrySchedule schedule) = await ReconnectingSessionAsync(Receiver(), clock);
        await using DeviceSessionService _ = session;
        using RetrySchedule watcher = schedule;

        session.StopRetrying();

        Assert.True(session.StayConnected);
    }

    /// <summary>Retry now does not wait out the backoff.</summary>
    /// <remarks>
    /// Asserted as "the schedule was abandoned without the clock reaching it", which is the property
    /// that matters and the one a fake clock can state plainly: the pinned clock never advances, so
    /// a wait that ends can only have been woken.
    /// </remarks>
    [Fact]
    public async Task RetryNowDoesNotWaitOutTheBackoff()
    {
        FakeTimeProvider clock = new();
        (DeviceSessionService session, RetrySchedule schedule) = await ReconnectingSessionAsync(Receiver(), clock);
        await using DeviceSessionService _ = session;
        using RetrySchedule watcher = schedule;

        DateTimeOffset pinned = clock.GetUtcNow();
        DateTimeOffset due = await watcher.NextScheduledAsync();
        Assert.True(due > pinned, "the wait should still be ahead of the clock");

        session.RetryNow();

        // Waking clears the schedule, so the very next change is the one Retry now caused.
        Assert.Null(await watcher.NextChangeAsync());

        // And it was the wake rather than the passage of time: nothing advanced the clock, so it
        // never reached the instant the attempt was scheduled for.
        Assert.Equal(pinned, clock.GetUtcNow());
        Assert.True(due > clock.GetUtcNow());
    }

    /// <summary>Retry now on a session that is not retrying does nothing.</summary>
    /// <remarks>
    /// Pressing it during the attempt itself must not queue a second one — the receiver serves one
    /// transaction at a time and the schedule keeps one attempt in flight, which is the whole reason
    /// this wakes a wait rather than starting anything.
    /// </remarks>
    [Fact]
    public async Task RetryNowOnAHealthySessionDoesNothing()
    {
        await using DeviceSessionService session = new((_, _) => Receiver(), new FakeTimeProvider());
        await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout);

        session.RetryNow();
        session.StopRetrying();

        Assert.Equal(ConnectionStatus.Connected, session.Status);
    }

}
