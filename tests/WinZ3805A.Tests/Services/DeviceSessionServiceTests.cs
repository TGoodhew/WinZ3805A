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
}
