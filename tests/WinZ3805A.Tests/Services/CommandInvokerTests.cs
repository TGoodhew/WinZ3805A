using Microsoft.Extensions.Time.Testing;
using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// Running a confirmed tier C command: §7.2's error-queue read, and §9.11's account of what
/// happened (P0-8).
/// </summary>
public class CommandInvokerTests
{
    private const string Identity = "SYMMETRICOM,Z3805A,3625A02931,1.01.03-A";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static ScpiCommand Command(string mnemonic) => CommandCatalog.Find(mnemonic)!;

    /// <summary>
    /// A receiver that records what it was asked and answers the error queue as told.
    /// </summary>
    private sealed class Bench
    {
        public List<string> Written { get; } = [];

        /// <summary>What <c>:SYST:ERR?</c> answers. The empty queue by default.</summary>
        public string ErrorQueue { get; set; } = "0,\"No error\"";

        /// <summary>What the command under test answers, for the tier C queries.</summary>
        public string CommandReply { get; set; } = string.Empty;

        public ControllableTransport Transport { get; }

        public Bench()
        {
            Transport = new ControllableTransport(command =>
            {
                if (command.StartsWith("*IDN", StringComparison.OrdinalIgnoreCase))
                {
                    return Identity;
                }

                Written.Add(command);

                return command.StartsWith(":SYST:ERR", StringComparison.OrdinalIgnoreCase)
                    ? ErrorQueue
                    : CommandReply;
            })
            { Banner = Identity };
        }
    }

    private static async Task<DeviceSessionService> ConnectedAsync(Bench bench, TimeProvider clock)
    {
        DeviceSessionService session = new((_, _) => bench.Transport, clock);
        await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout);
        bench.Written.Clear();
        return session;
    }

    // -------------------------------------------------------------------------------------
    // §7.2's error-queue rule
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// §7.2: after every tier C command, issue <c>:SYST:ERR?</c>. Without it a rejected setter is
    /// silent — the receiver answers a setter with the prompt alone either way.
    /// </summary>
    [Fact]
    public async Task ReadsTheErrorQueueAfterEveryTierCCommand()
    {
        Bench bench = new();
        await using DeviceSessionService session = await ConnectedAsync(bench, new FakeTimeProvider());

        await new CommandInvoker(session).ExecuteAsync(Command(":DIAG:LOG:CLEar")).WaitAsync(TestTimeout);

        Assert.Equal(2, bench.Written.Count);
        Assert.StartsWith(":DIAG:LOG:CLE", bench.Written[0], StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(":SYST:ERR?", bench.Written[1], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An empty queue after the command means it worked.</summary>
    [Fact]
    public async Task AnEmptyQueueMeansTheCommandSucceeded()
    {
        Bench bench = new();
        await using DeviceSessionService session = await ConnectedAsync(bench, new FakeTimeProvider());

        CommandOutcome outcome = await new CommandInvoker(session)
            .ExecuteAsync(Command(":DIAG:LOG:CLEar")).WaitAsync(TestTimeout);

        Assert.True(outcome.Succeeded);
        Assert.Equal("Cleared the diagnostic log.", outcome.Message);
    }

    /// <summary>
    /// A non-zero entry means it did not, however cleanly the transaction itself completed. This is
    /// the case that would otherwise pass silently.
    /// </summary>
    [Fact]
    public async Task ANonZeroEntryMeansTheReceiverRejectedIt()
    {
        Bench bench = new() { ErrorQueue = "-222,\"Data out of range\"" };
        await using DeviceSessionService session = await ConnectedAsync(bench, new FakeTimeProvider());

        CommandOutcome outcome = await new CommandInvoker(session)
            .ExecuteAsync(Command(":GPS:SAT:TRAC:EMANgle"), "9.9E+001", "99").WaitAsync(TestTimeout);

        Assert.False(outcome.Succeeded);
        Assert.Equal(CommandOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal(-222, outcome.Error!.Code);
    }

    // -------------------------------------------------------------------------------------
    // §9.11's copy rules
    // -------------------------------------------------------------------------------------

    /// <summary>§9.11: surface the SCPI error number <em>and</em> its plain-language meaning.</summary>
    [Fact]
    public async Task TheFailureCarriesBothTheNumberAndItsMeaning()
    {
        Bench bench = new() { ErrorQueue = "-222,\"Data out of range\"" };
        await using DeviceSessionService session = await ConnectedAsync(bench, new FakeTimeProvider());

        CommandOutcome outcome = await new CommandInvoker(session)
            .ExecuteAsync(Command(":GPS:SAT:TRAC:EMANgle"), "9.9E+001", "99").WaitAsync(TestTimeout);

        Assert.Contains("-222", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("Data out of range", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// §9.11's error pattern ends with what to do next. A range is the one remedy the catalog knows,
    /// and the elevation mask has one.
    /// </summary>
    [Fact]
    public async Task AnOutOfRangeFailureNamesTheRange()
    {
        Bench bench = new() { ErrorQueue = "-222,\"Data out of range\"" };
        await using DeviceSessionService session = await ConnectedAsync(bench, new FakeTimeProvider());

        CommandOutcome outcome = await new CommandInvoker(session)
            .ExecuteAsync(Command(":GPS:SAT:TRAC:EMANgle"), "9.9E+001", "99").WaitAsync(TestTimeout);

        Assert.Contains("Enter a value between 0 and 90", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>§9.11: no "Oops", no "Sorry", and the verb from the button that started it.</summary>
    [Fact]
    public async Task TheFailureOpensWithTheCommandsOwnVerb()
    {
        Bench bench = new() { ErrorQueue = "-221,\"Settings conflict\"" };
        await using DeviceSessionService session = await ConnectedAsync(bench, new FakeTimeProvider());

        CommandOutcome outcome = await new CommandInvoker(session)
            .ExecuteAsync(Command(":DIAG:LOG:CLEar")).WaitAsync(TestTimeout);

        Assert.StartsWith("Couldn't clear diagnostic log.", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// §9.11: <em>Start survey</em> → <em>Start survey?</em> → "Started the position survey." The
    /// past-tense sentence comes from the catalog, so the third step is as fixed as the first two.
    /// </summary>
    [Fact]
    public async Task TheSuccessSentenceIsTheCatalogsAndKeepsTheVerb()
    {
        Bench bench = new();
        await using DeviceSessionService session = await ConnectedAsync(bench, new FakeTimeProvider());

        CommandOutcome outcome = await new CommandInvoker(session)
            .ExecuteAsync(Command(":GPS:POSition:SURVey:STATe ONCE")).WaitAsync(TestTimeout);

        Assert.StartsWith("Started the position survey.", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>The success line states the value that was actually sent.</summary>
    [Fact]
    public async Task TheSuccessSentenceQuotesTheValue()
    {
        Bench bench = new();
        await using DeviceSessionService session = await ConnectedAsync(bench, new FakeTimeProvider());

        CommandOutcome outcome = await new CommandInvoker(session)
            .ExecuteAsync(Command(":GPS:SAT:TRAC:EMANgle"), "1.0E+001", "10").WaitAsync(TestTimeout);

        Assert.Equal("Set the elevation mask to 10°.", outcome.Message);
    }

    // -------------------------------------------------------------------------------------
    // Answers, and the absence of them
    // -------------------------------------------------------------------------------------

    /// <summary>The tier C queries answer with a value, and it survives to the caller.</summary>
    [Fact]
    public async Task AQueryKeepsWhatTheReceiverAnswered()
    {
        Bench bench = new() { CommandReply = " +0" };
        await using DeviceSessionService session = await ConnectedAsync(bench, new FakeTimeProvider());

        CommandOutcome outcome = await new CommandInvoker(session)
            .ExecuteAsync(Command("*TST?")).WaitAsync(TestTimeout);

        Assert.True(outcome.Succeeded);
        Assert.Equal(" +0", Assert.Single(outcome.Lines));
    }

    /// <summary>Nothing runs while disconnected, and the caller is told why rather than left waiting.</summary>
    [Fact]
    public async Task ADisconnectedSessionFailsWithoutSendingAnything()
    {
        await using DeviceSessionService session = new((_, _) => new ControllableTransport(_ => null), new FakeTimeProvider());

        CommandOutcome outcome = await new CommandInvoker(session)
            .ExecuteAsync(Command(":DIAG:LOG:CLEar")).WaitAsync(TestTimeout);

        Assert.Equal(CommandOutcomeKind.Failed, outcome.Kind);
        Assert.Contains("not connected", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------------------
    // The tier boundary
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// §7.2 puts the error-queue read on tier C alone, so a safe command arriving here is a caller
    /// that has confused the two paths. Obliging it would double the traffic on the poll tier and
    /// hide the mistake.
    /// </summary>
    [Fact]
    public async Task RefusesACommandThatIsNotTierC()
    {
        Bench bench = new();
        await using DeviceSessionService session = await ConnectedAsync(bench, new FakeTimeProvider());

        await Assert.ThrowsAsync<ArgumentException>(
            () => new CommandInvoker(session).ExecuteAsync(Command(":SYNC:STAT?")));

        Assert.Empty(bench.Written);
    }
}
