using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Models;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;
using WinZ3805A.Tests.Drivers;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// #287's selection: the probe belongs to no driver, and the identity chooses one.
/// </summary>
/// <remarks>
/// <para>
/// These are the tests that make item 1 of #287 fail loudly instead of needing a grep. Every one
/// runs the application's real connect and poll paths over <see cref="FakeReceiverDriver"/> — a
/// family that shares no mnemonic with the SmartClock — so any service that still reached the
/// SmartClock statics would either ask the wrong questions on the wire or offer the wrong
/// vocabulary, and an assertion here would see it.
/// </para>
/// <para>
/// The fictional identities follow the precedent <c>DeviceSessionServiceTests</c> set with
/// <c>ACME,ONE,…</c>: four IEEE 488.2 fields, a manufacturer no real receiver reports.
/// </para>
/// </remarks>
public sealed class DriverSelectionTests
{
    private const string SmartClockIdentity = "SYMMETRICOM,Z3805A,3625A02931,1.01.03-A";
    private const string AcmeIdentity = "ACME,ONE,0001,1.0";
    private const string UnclaimedIdentity = "TRIMBLE,THUNDERBOLT,0001,1.0";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Both families, SmartClock first — the composition root's registration order.</summary>
    private static IReadOnlyList<IReceiverDriver> BothDrivers(FakeTimeProvider clock) =>
        [new SmartClockDriver(clock), new FakeReceiverDriver()];

    /// <summary>A transport whose receiver announces <paramref name="identity"/> and answers everything else blandly.</summary>
    private static ControllableTransport Receiver(string identity, string answer = "IDLE") =>
        new(command => command.StartsWith("*IDN", StringComparison.OrdinalIgnoreCase) ? identity : answer)
        {
            Banner = identity,
        };

    // -------------------------------------------------------------------------------------
    // Selection at connect
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task TheDriverWhoseFamilyAnsweredIsSelected()
    {
        FakeTimeProvider clock = new();
        await using DeviceSessionService session = new(
            (_, _) => Receiver(AcmeIdentity), clock, drivers: BothDrivers(clock));

        Assert.Equal("SmartClock", session.Driver.Family);

        Assert.True(await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout));
        Assert.Equal("Acme", session.Driver.Family);
    }

    [Fact]
    public async Task TheSmartClockKeepsItsOwnReceivers()
    {
        FakeTimeProvider clock = new();
        await using DeviceSessionService session = new(
            (_, _) => Receiver(SmartClockIdentity), clock, drivers: BothDrivers(clock));

        Assert.True(await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout));
        Assert.Equal("SmartClock", session.Driver.Family);
    }

    /// <summary>
    /// An identity nothing claims connects under the first registered driver, exactly as every
    /// receiver did before selection existed.
    /// </summary>
    [Fact]
    public async Task AnUnclaimedIdentityFallsBackToTheFirstRegistered()
    {
        FakeTimeProvider clock = new();
        await using DeviceSessionService session = new(
            (_, _) => Receiver(UnclaimedIdentity), clock, drivers: BothDrivers(clock));

        Assert.True(await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout));
        Assert.Equal("SmartClock", session.Driver.Family);
    }

    /// <summary>
    /// Selection runs on every connect, because the receiver on the port can have been swapped
    /// while the link was down.
    /// </summary>
    [Fact]
    public async Task ReconnectingReselectsForWhateverIsOnThePortNow()
    {
        FakeTimeProvider clock = new();
        string identity = AcmeIdentity;
        await using DeviceSessionService session = new(
            (_, _) => Receiver(identity), clock, drivers: BothDrivers(clock));

        Assert.True(await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout));
        Assert.Equal("Acme", session.Driver.Family);

        identity = SmartClockIdentity;
        Assert.True(await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout));
        Assert.Equal("SmartClock", session.Driver.Family);
    }

    // -------------------------------------------------------------------------------------
    // The auto-detect plan
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The walk is every driver's sequence, in registration order, first appearance winning.
    /// </summary>
    /// <remarks>
    /// The fake's sequence shares one entry with the SmartClock's and adds one, so this exercises
    /// both halves of the union: the duplicate collapses, the addition appends. Registration order
    /// mattering is the point — adding a driver may only ever append probes, never reorder the
    /// walk §10.12 fixes for the family already shipped.
    /// </remarks>
    [Fact]
    public async Task TheAutoDetectPlanIsTheUnionInRegistrationOrder()
    {
        FakeTimeProvider clock = new();
        await using DeviceSessionService session = new(
            (_, _) => Receiver(AcmeIdentity), clock, drivers: BothDrivers(clock));

        int smartClockCount = SerialSettings.AutoDetectSequence.Count;

        Assert.Equal(smartClockCount + 1, session.AutoDetectPlan.Count);
        Assert.Equal(SerialSettings.AutoDetectSequence, session.AutoDetectPlan.Take(smartClockCount));
        Assert.Equal(new FakeReceiverDriver().AutoDetectSequence[1], session.AutoDetectPlan[^1]);
    }

    [Fact]
    public async Task WithOneDriverThePlanIsExactlyItsSequence()
    {
        FakeTimeProvider clock = new();
        await using DeviceSessionService session = new((_, _) => Receiver(SmartClockIdentity), clock);

        Assert.Equal(SerialSettings.AutoDetectSequence, session.AutoDetectPlan);
    }

    // -------------------------------------------------------------------------------------
    // The seam, end to end
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The poller asks the selected driver's questions, not the SmartClock's.
    /// </summary>
    /// <remarks>
    /// This is the test that catches a poll vocabulary hard-coded app-side: before #287 the fast
    /// tier was a static array of <c>:SYNC:</c> queries in <c>PollingService</c>, and this receiver
    /// — which has no <c>:SYNC:</c> node — would have been swept with another family's questions.
    /// </remarks>
    [Fact]
    public async Task ThePollerSweepsTheSelectedDriversPlan()
    {
        FakeTimeProvider clock = new();
        ControllableTransport transport = new(command =>
            command.StartsWith("*IDN", StringComparison.OrdinalIgnoreCase) ? AcmeIdentity
            : command.StartsWith(":ACME:STAT", StringComparison.OrdinalIgnoreCase) ? " RUN"
            : command.StartsWith(":ACME:LEV", StringComparison.OrdinalIgnoreCase) ? " +42.5"
            : command.StartsWith(":ACME:DUMP", StringComparison.OrdinalIgnoreCase) ? "all quiet"
            : " 0")
        {
            Banner = AcmeIdentity,
        };

        await using DeviceSessionService session = new(
            (_, _) => transport, clock, drivers: [new FakeReceiverDriver(), new SmartClockDriver(clock)]);
        Assert.True(await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout));
        Assert.Equal("Acme", session.Driver.Family);

        ReceiverStateStore store = new(clock);
        await using PollingService poller = new(session, store, clock);

        poller.Start();
        await WaitFor(clock, () => poller.FastSweeps >= 1 && poller.FullSweeps >= 1, () => poller.FastSweeps);
        await poller.StopAsync();

        Assert.Equal("RUN", store.SyncState);
        Assert.Equal(42.5, store.OscillatorControl);

        Assert.Contains(transport.CommandsWritten, written => written.StartsWith(":ACME:STAT", StringComparison.Ordinal));
        Assert.Contains(transport.CommandsWritten, written => written.StartsWith(":ACME:DUMP", StringComparison.Ordinal));
        Assert.DoesNotContain(transport.CommandsWritten, written => written.Contains(":SYNC:", StringComparison.Ordinal));
        Assert.DoesNotContain(transport.CommandsWritten, written => written.Contains(":SYST:STAT", StringComparison.Ordinal));
    }

    /// <summary>
    /// The console's picker offers the selected driver's vocabulary and nothing else's.
    /// </summary>
    [Fact]
    public void TheConsoleOffersTheSelectedDriversVocabulary()
    {
        ConsoleCatalog acme = new(new FakeReceiverDriver());
        ConsoleCatalog smartClock = new(new SmartClockDriver(new FakeTimeProvider()));

        Assert.Contains(acme.All, entry => entry.Mnemonic.StartsWith(":ACME:", StringComparison.Ordinal));
        Assert.DoesNotContain(acme.All, entry => entry.Mnemonic.StartsWith(":SYNC:", StringComparison.Ordinal));
        Assert.DoesNotContain(smartClock.All, entry => entry.Mnemonic.StartsWith(":ACME:", StringComparison.Ordinal));
    }

    /// <summary>
    /// A driver registered in the composition root reaches the keyed session (#287).
    /// </summary>
    /// <remarks>
    /// The DI half of the seam: <c>AddDevice</c> resolves every <c>IReceiverDriver</c>
    /// registration, so adding a family really is one <c>AddSingleton</c> line. Before this,
    /// <c>AddDevice</c> had no way to pass a driver at all and a registration was constructed and
    /// ignored.
    /// </remarks>
    [Fact]
    public async Task ARegisteredDriverReachesTheKeyedSession()
    {
        ServiceCollection services = new();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddSingleton<IReceiverDriver>(new FakeReceiverDriver());
        services.AddDevice("bench", (_, _) => new FakeTransport());

        await using ServiceProvider provider = services.BuildServiceProvider();
        DeviceContext context = provider.GetRequiredKeyedService<DeviceContext>("bench");

        Assert.Equal("Acme", context.Driver.Family);
    }

    // -------------------------------------------------------------------------------------
    // Guard rails the branch review added (#287)
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// A command the connected receiver's driver does not offer is refused at the point of send,
    /// unsent.
    /// </summary>
    /// <remarks>
    /// Pages resolve commands when they open, and a reconnect can select a different family
    /// underneath them — this is §8.1's "provably came from the allowlist" made true of the
    /// receiver actually on the port, not of some driver at some earlier time. Refused as a
    /// faulted transaction rather than an exception, so a stale click cannot take the poll loop
    /// with it.
    /// </remarks>
    [Fact]
    public async Task ACommandFromAnotherFamilysCatalogIsRefusedUnsent()
    {
        FakeTimeProvider clock = new();
        ControllableTransport transport = Receiver(SmartClockIdentity, answer: "LOCK");
        await using DeviceSessionService session = new(
            (_, _) => transport, clock, drivers: BothDrivers(clock));
        Assert.True(await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout));
        Assert.Equal("SmartClock", session.Driver.Family);

        ScpiCommand foreign = new FakeReceiverDriver().Find(":ACME:STAT?")!;
        Transaction refused = await session.ExecuteAsync(foreign).WaitAsync(TestTimeout);

        Assert.False(refused.Succeeded);
        Assert.Equal(TransactionOutcome.Faulted, refused.Outcome);
        Assert.DoesNotContain(transport.CommandsWritten, written => written.Contains(":ACME:", StringComparison.Ordinal));
    }

    /// <summary>
    /// A driver whose Recognises throws is read as "does not claim" — the connect survives.
    /// </summary>
    /// <remarks>
    /// The predicate has no never-throw contract the way Parse and InterpretSweep do, and the
    /// connect path's catches filter on transport faults — so before this guard, one buggy
    /// third-party predicate could take down every connect including the fire-and-forget
    /// reconnect loop.
    /// </remarks>
    [Fact]
    public async Task ADriverWhoseRecognisesThrowsIsSkippedNotFatal()
    {
        FakeTimeProvider clock = new();
        await using DeviceSessionService session = new(
            (_, _) => Receiver(AcmeIdentity),
            clock,
            drivers: [new ThrowingRecogniser(), new FakeReceiverDriver()]);

        Assert.True(await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout));
        Assert.Equal("Acme", session.Driver.Family);
    }

    /// <summary>
    /// A driver without the §7.2 error query fails a tier C command loudly, never silently.
    /// </summary>
    /// <remarks>
    /// The invoker's missing-entry throw used to be swallowed by the catch written for a
    /// torn-down session, which turned "this driver cannot satisfy §7.2" into tier C commands
    /// that skipped the mandated error-queue read and reported success. The throw now escapes,
    /// which is what the contract tests promise it does.
    /// </remarks>
    [Fact]
    public async Task ATierCCommandWithoutAnErrorQueueFailsLoudly()
    {
        FakeTimeProvider clock = new();
        ErrorlessDriver driver = new();
        await using DeviceSessionService session = new(
            (_, _) => Receiver(AcmeIdentity, answer: "OK"), clock, drivers: [driver]);
        Assert.True(await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout));

        CommandInvoker invoker = new(session);
        ScpiCommand tierC = driver.Find(":ACME:MARK")!;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => invoker.ExecuteAsync(tierC).WaitAsync(TestTimeout));
    }

    /// <summary>A driver whose recognition predicate is a bug, for the guard test above.</summary>
    private sealed class ThrowingRecogniser : IReceiverDriver
    {
        private readonly FakeReceiverDriver _inner = new();

        public string Family => "Broken";

        public IReadOnlyList<ScpiCommand> Commands => _inner.Commands;

        public PollCadence Cadence => _inner.Cadence;

        public IReadOnlyList<SerialSettings> AutoDetectSequence => _inner.AutoDetectSequence;

        public PollPlan Plan => _inner.Plan;

        public bool Recognises(DeviceIdentity? identity) =>
            throw new InvalidOperationException("This predicate is a bug.");

        public ScpiCommand? Find(string? mnemonic) => _inner.Find(mnemonic);

        public bool IsBlocked(string? header) => _inner.IsBlocked(header);

        public TimeSpan TimeoutFor(string? mnemonic) => _inner.TimeoutFor(mnemonic);

        public ReceiverStatus Parse(string? response) => _inner.Parse(response);

        public SweepInterpretation InterpretSweep(IReadOnlyList<string?> answers) => _inner.InterpretSweep(answers);
        public ReceiverMode InterpretSyncState(string? syncState) => _inner.InterpretSyncState(syncState);
    }

    /// <summary>
    /// A contract-breaking driver with no <c>:SYST:ERR?</c>, for the loud-failure test above.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT in <c>ReceiverDriverTests.AllDrivers</c>: the contract tests would fail it
    /// on <c>TheErrorQueueQueryIsCatalogued</c>, which is the point — it models the driver a
    /// third party ships without reading the contract.
    /// </remarks>
    private sealed class ErrorlessDriver : IReceiverDriver
    {
        private readonly FakeReceiverDriver _inner = new();

        public string Family => "Errorless";

        public IReadOnlyList<ScpiCommand> Commands { get; } =
            [.. new FakeReceiverDriver().Commands.Where(c => c.Mnemonic != ":SYST:ERR?")];

        public PollCadence Cadence => _inner.Cadence;

        public IReadOnlyList<SerialSettings> AutoDetectSequence => _inner.AutoDetectSequence;

        public PollPlan Plan => _inner.Plan;

        public bool Recognises(DeviceIdentity? identity) => _inner.Recognises(identity);

        public ScpiCommand? Find(string? mnemonic) =>
            Commands.FirstOrDefault(c => string.Equals(c.Mnemonic, mnemonic?.Trim(), StringComparison.OrdinalIgnoreCase));

        public bool IsBlocked(string? header) => _inner.IsBlocked(header);

        public TimeSpan TimeoutFor(string? mnemonic) => _inner.TimeoutFor(mnemonic);

        public ReceiverStatus Parse(string? response) => _inner.Parse(response);

        public SweepInterpretation InterpretSweep(IReadOnlyList<string?> answers) => _inner.InterpretSweep(answers);
        public ReceiverMode InterpretSyncState(string? syncState) => _inner.InterpretSyncState(syncState);
    }

    // -------------------------------------------------------------------------------------

    /// <summary>Advances the fake clock until a condition holds — PollingServiceTests' pattern.</summary>
    private static async Task WaitFor(FakeTimeProvider clock, Func<bool> condition, Func<int>? progress = null)
    {
        using CancellationTokenSource giveUp = new(TestTimeout);
        while (!condition() && !giveUp.IsCancellationRequested)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await SettleAsync(progress, giveUp.Token);
        }

        Assert.True(condition(), "The condition never held.");
    }

    /// <summary>Waits for the poll loop to go quiet between clock advances, so no tick is lost.</summary>
    private static async Task SettleAsync(Func<int>? progress, CancellationToken cancellationToken)
    {
        if (progress is null)
        {
            await Task.Delay(5, cancellationToken);
            return;
        }

        int previous = -1;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            await Task.Delay(2, cancellationToken);

            int current = progress();
            if (current == previous)
            {
                return;
            }

            previous = current;
        }
    }
}
