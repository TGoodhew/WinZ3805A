using System.Text;
using Microsoft.Extensions.Time.Testing;
using WinZ3805A.Device.Transport;

namespace WinZ3805A.Tests.Transport;

/// <summary>
/// P0-2, the echo-tolerant line protocol, proved against replayed bytes with no hardware attached
/// (§15 step 1).
/// </summary>
/// <remarks>
/// The clock is a <see cref="FakeTimeProvider"/> throughout, so the 15 s status-screen timeout is
/// asserted in microseconds. Waits on the transaction itself use a real-time guard instead, because
/// a protocol bug should fail the test rather than hang the run.
/// </remarks>
public class LineProtocolTests
{
    /// <summary>How long a test waits for a transaction that ought to finish immediately.</summary>
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromSeconds(10);

    // The real thing, from the receiver on COM3: manufacturer, model, serial, firmware.
    private const string IdentityResponse = "SYMMETRICOM,Z3805A,3625A02931,1.01.03-A";

    [Fact]
    public async Task EchoedCommandIsDiscardedAndOnlyTheResponseSurvives()
    {
        await using FakeTransport transport = new(_ => IdentityResponse) { EchoCommands = true };
        LineProtocol protocol = await ConnectAsync(transport);

        Transaction transaction = await protocol.ExecuteAsync("*IDN?").WaitAsync(s_testTimeout);

        Assert.Equal(TransactionOutcome.Completed, transaction.Outcome);
        Assert.True(transaction.EchoDiscarded);
        Assert.Equal(new[] { IdentityResponse }, transaction.Lines);
        Assert.Equal(new[] { "*IDN?" }, transport.CommandsWritten);
    }

    /// <summary>
    /// The same exchange with the echo off. §7.2 requires echo to be detected rather than assumed:
    /// a protocol that unconditionally drops line one eats the answer the moment someone sets
    /// <c>FDUPlex OFF</c>.
    /// </summary>
    [Fact]
    public async Task ResponseSurvivesIntactWhenTheDeviceDoesNotEcho()
    {
        await using FakeTransport transport = new(_ => IdentityResponse) { EchoCommands = false };
        LineProtocol protocol = await ConnectAsync(transport);

        Transaction transaction = await protocol.ExecuteAsync("*IDN?").WaitAsync(s_testTimeout);

        Assert.Equal(TransactionOutcome.Completed, transaction.Outcome);
        Assert.False(transaction.EchoDiscarded);
        Assert.Equal(new[] { IdentityResponse }, transaction.Lines);
    }

    /// <summary>§7.2: a command that produces no response returns only the prompt. That is success, not a timeout.</summary>
    [Fact]
    public async Task SetterAnsweredByThePromptAloneCompletesWithNoLines()
    {
        await using FakeTransport transport = new(_ => null);
        LineProtocol protocol = await ConnectAsync(transport);

        Transaction transaction = await protocol.ExecuteAsync("*CLS").WaitAsync(s_testTimeout);

        Assert.Equal(TransactionOutcome.Completed, transaction.Outcome);
        Assert.True(transaction.EchoDiscarded);
        Assert.Empty(transaction.Lines);
        Assert.Null(transaction.FirstLine);
    }

    /// <summary>
    /// The sentinel arrives split down the middle. This is the case §6.4 buys Pipelines for, and the
    /// one a hand-rolled buffer gets wrong.
    /// </summary>
    [Fact]
    public async Task PromptSplitAcrossTwoReadsStillEndsTheTransaction()
    {
        await using FakeTransport transport = new();
        LineProtocol protocol = await ConnectAsync(transport);

        Task<Transaction> pending = protocol.ExecuteAsync("*IDN?");

        Assert.Equal("*IDN?", await transport.ReadCommandAsync().AsTask().WaitAsync(s_testTimeout));
        await transport.EmitAsync("*IDN?\r\n");
        await transport.EmitAsync(IdentityResponse + "\r\n");
        await transport.EmitAsync("sc");
        await transport.EmitAsync("pi");
        await transport.EmitAsync(" > ");

        Transaction transaction = await pending.WaitAsync(s_testTimeout);

        Assert.Equal(TransactionOutcome.Completed, transaction.Outcome);
        Assert.True(transaction.EchoDiscarded);
        Assert.Equal(new[] { IdentityResponse }, transaction.Lines);
    }

    /// <summary>
    /// Every spelling of the prompt ends the transaction.
    /// </summary>
    /// <remarks>
    /// §7.2 says the sentinel is <c>"scpi&gt; "</c>. The receiver on the bench — a Z3805A running
    /// firmware 1.01.03-A — emits <c>"scpi &gt; "</c>, with a space before the bracket. Matching the
    /// specified spelling literally means no transaction ever completes and the app never connects,
    /// so the protocol matches the word and steps over whatever spacing follows.
    /// </remarks>
    [Theory]
    [InlineData("scpi > ")]
    [InlineData("scpi> ")]
    [InlineData("scpi>")]
    [InlineData("scpi   >   ")]
    public async Task EveryObservedSpellingOfThePromptEndsTheTransaction(string prompt)
    {
        await using FakeTransport transport = new(_ => IdentityResponse) { Prompt = prompt };
        LineProtocol protocol = await ConnectAsync(transport);

        Transaction transaction = await protocol.ExecuteAsync("*IDN?").WaitAsync(s_testTimeout);

        Assert.Equal(TransactionOutcome.Completed, transaction.Outcome);
        Assert.Equal(new[] { IdentityResponse }, transaction.Lines);
    }

    /// <summary>
    /// The prompt word appearing in response text is not a prompt. Only the bracket makes it one.
    /// </summary>
    [Fact]
    public async Task ThePromptWordWithoutItsBracketIsTreatedAsResponseText()
    {
        await using FakeTransport transport = new(_ => "scpi is the parser\r\nsecond line");
        LineProtocol protocol = await ConnectAsync(transport);

        Transaction transaction = await protocol.ExecuteAsync(":SYST:STAT?").WaitAsync(s_testTimeout);

        Assert.Equal(TransactionOutcome.Completed, transaction.Outcome);
        Assert.Equal(new[] { "scpi is the parser", "second line" }, transaction.Lines);
    }

    /// <summary>A response line split across reads is reassembled, not truncated at the boundary.</summary>
    [Fact]
    public async Task ResponseLineSplitAcrossReadsIsReassembled()
    {
        await using FakeTransport transport = new();
        LineProtocol protocol = await ConnectAsync(transport);

        Task<Transaction> pending = protocol.ExecuteAsync(":SYNC:TINT?");

        await transport.ReadCommandAsync().AsTask().WaitAsync(s_testTimeout);
        await transport.EmitAsync(":SYNC:TINT?\r\n-3.3");
        await transport.EmitAsync("1E-008\r\nscpi > ");

        Transaction transaction = await pending.WaitAsync(s_testTimeout);

        Assert.Equal(TransactionOutcome.Completed, transaction.Outcome);
        Assert.Equal("-3.31E-008", transaction.FirstLine);
    }

    /// <summary>
    /// The full status screen: ~1900 bytes of it, delivered seventeen bytes at a time. §7.2 singles
    /// this out as the read that <c>ReadLine</c> cannot express.
    /// </summary>
    /// <remarks>
    /// The block is generated, not captured. Real screens are captured from the receiver into
    /// <c>tests/WinZ3805A.Tests/Fixtures/</c> for the parser in §15 step 2 (P0-4); what is being
    /// proved here is reassembly across buffer boundaries, and inventing device output that later
    /// gets mistaken for a capture would be a poor trade for that.
    /// </remarks>
    [Fact]
    public async Task StatusScreenArrivingInSeventeenByteChunksIsReassembledInOrder()
    {
        string[] screen = BuildLongResponse(lineCount: 24, lineLength: 78);

        await using FakeTransport transport = new(_ => string.Join("\r\n", screen)) { ChunkSize = 17 };
        LineProtocol protocol = await ConnectAsync(transport);

        Transaction transaction = await protocol.ExecuteAsync(":SYST:STAT?").WaitAsync(s_testTimeout);

        Assert.Equal(TransactionOutcome.Completed, transaction.Outcome);
        Assert.True(transaction.EchoDiscarded);
        Assert.Equal(screen, transaction.Lines);
        Assert.True(Encoding.ASCII.GetByteCount(transaction.Text) > 1800, "the block should be status-screen sized");
    }

    /// <summary>
    /// A CRLF split down the middle by a read boundary is still one line ending.
    /// </summary>
    /// <remarks>
    /// At 9600 baud this happens constantly, and the failure it causes is quiet: a blank line
    /// appears between two real ones. The status screen is parsed by locating a header row and
    /// deriving columns from it (§11.1), so a phantom row is exactly the kind of corruption that
    /// surfaces as a mysteriously empty satellite table rather than as an error.
    /// </remarks>
    [Fact]
    public async Task CarriageReturnAndLineFeedSplitAcrossReadsIsOneLineEnding()
    {
        await using FakeTransport transport = new() { WaitForReaderToConsume = true };
        LineProtocol protocol = await ConnectAsync(transport);

        Task<Transaction> pending = protocol.ExecuteAsync(":SYST:STAT?");

        await transport.ReadCommandAsync().AsTask().WaitAsync(s_testTimeout);

        // Each emit only returns once the protocol has consumed it, so the boundary lands between
        // the CR and the LF every run rather than whenever the scheduler happens to put it there.
        await transport.EmitAsync(":SYST:STAT?\r\nfirst\r").AsTask().WaitAsync(s_testTimeout);
        await transport.EmitAsync("\nsecond\r\nscpi > ").AsTask().WaitAsync(s_testTimeout);

        Transaction transaction = await pending.WaitAsync(s_testTimeout);

        Assert.Equal(new[] { "first", "second" }, transaction.Lines);
    }

    /// <summary>Blank lines inside a response are structure, not noise: the screen is laid out in columns and blocks.</summary>
    [Fact]
    public async Task BlankLinesInsideAResponseArePreserved()
    {
        await using FakeTransport transport = new(_ => "first\r\n\r\nthird");
        LineProtocol protocol = await ConnectAsync(transport);

        Transaction transaction = await protocol.ExecuteAsync(":SYST:STAT?").WaitAsync(s_testTimeout);

        Assert.Equal(new[] { "first", string.Empty, "third" }, transaction.Lines);
    }

    /// <summary>A device that stops answering produces a timed-out transaction, not an exception.</summary>
    [Fact]
    public async Task SilentDeviceTimesOutAndReportsRatherThanThrows()
    {
        FakeTimeProvider time = new();
        await using FakeTransport transport = new(_ => IdentityResponse) { Silent = true };
        LineProtocol protocol = await ConnectAsync(transport, time);

        Task<Transaction> pending = protocol.ExecuteAsync("*IDN?", TransactionTimeouts.Default);
        await transport.ReadCommandAsync().AsTask().WaitAsync(s_testTimeout);

        time.Advance(TransactionTimeouts.Default);
        Transaction transaction = await pending.WaitAsync(s_testTimeout);

        Assert.Equal(TransactionOutcome.TimedOut, transaction.Outcome);
        Assert.False(transaction.Succeeded);
        Assert.Empty(transaction.Lines);
    }

    /// <summary>
    /// A response that starts and then stops half way keeps what arrived. Diagnostics showing three
    /// lines and a timeout is a far better bug report than showing nothing.
    /// </summary>
    [Fact]
    public async Task TruncatedResponseIsKeptWhenTheTransactionTimesOut()
    {
        FakeTimeProvider time = new();

        // The emit has to be consumed before the clock moves, or the timeout races the reader and
        // the assertion below depends on which won. Waiting for the reader removes the race rather
        // than making it rare: this test failed on CI and passed locally, which is the same lesson
        // the CRLF-boundary test taught.
        await using FakeTransport transport = new() { WaitForReaderToConsume = true };
        LineProtocol protocol = await ConnectAsync(transport, time);

        Task<Transaction> pending = protocol.ExecuteAsync(":SYST:STAT?");
        await transport.ReadCommandAsync().AsTask().WaitAsync(s_testTimeout);
        await transport.EmitAsync(":SYST:STAT?\r\nSmartClock Mode\r\n").AsTask().WaitAsync(s_testTimeout);

        time.Advance(TransactionTimeouts.StatusScreen);
        Transaction transaction = await pending.WaitAsync(s_testTimeout);

        Assert.Equal(TransactionOutcome.TimedOut, transaction.Outcome);
        Assert.True(transaction.EchoDiscarded);
        Assert.Equal(new[] { "SmartClock Mode" }, transaction.Lines);
    }

    /// <summary>
    /// The adapter is pulled mid-transaction (P0-14). §6.4 requires the read path to survive it, and
    /// the session layer needs the classification to decide whether to reconnect.
    /// </summary>
    [Fact]
    public async Task LinkFailingMidTransactionIsReportedAsAFault()
    {
        await using FakeTransport transport = new();
        LineProtocol protocol = await ConnectAsync(transport);

        Task<Transaction> pending = protocol.ExecuteAsync("*IDN?");
        await transport.ReadCommandAsync().AsTask().WaitAsync(s_testTimeout);
        transport.Fail(new IOException("The device is not connected."));

        Transaction transaction = await pending.WaitAsync(s_testTimeout);

        Assert.Equal(TransactionOutcome.Faulted, transaction.Outcome);
        Assert.Equal(TransportFault.Io, transaction.Fault);
        Assert.NotNull(transaction.FaultMessage);
    }

    /// <summary>
    /// A late response to a transaction that already timed out must not become the next
    /// transaction's answer — one timeout would otherwise misalign the session indefinitely.
    /// </summary>
    [Fact]
    public async Task LateBytesFromAnEarlierTransactionAreDiscardedBeforeTheNextOne()
    {
        await using FakeTransport transport = new(_ => IdentityResponse);
        LineProtocol protocol = await ConnectAsync(transport);

        await transport.EmitAsync("0,\"No error\"\r\nscpi > ");

        Transaction transaction = await protocol.ExecuteAsync("*IDN?").WaitAsync(s_testTimeout);

        Assert.Equal(TransactionOutcome.Completed, transaction.Outcome);
        Assert.Equal(new[] { IdentityResponse }, transaction.Lines);
        Assert.Equal(1, transport.DiscardCount);
    }

    /// <summary>Caller cancellation is the one outcome with nothing to report, so it throws.</summary>
    [Fact]
    public async Task CallerCancellationThrowsRatherThanReturningATransaction()
    {
        await using FakeTransport transport = new();
        LineProtocol protocol = await ConnectAsync(transport);

        using CancellationTokenSource cancellation = new();
        Task<Transaction> pending = protocol.ExecuteAsync("*IDN?", cancellation.Token);
        await transport.ReadCommandAsync().AsTask().WaitAsync(s_testTimeout);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(s_testTimeout));
    }

    /// <summary>§7.2: transmit is the command text followed by CRLF.</summary>
    [Fact]
    public async Task CommandIsTransmittedWithACarriageReturnLineFeedTerminator()
    {
        await using FakeTransport transport = new();
        LineProtocol protocol = await ConnectAsync(transport);

        Task<Transaction> pending = protocol.ExecuteAsync(":SYNC:TINT?");
        Assert.Equal(":SYNC:TINT?", await transport.ReadCommandAsync().AsTask().WaitAsync(s_testTimeout));

        await transport.EmitAsync("scpi > ");
        await pending.WaitAsync(s_testTimeout);
    }

    /// <summary>
    /// A clean transaction leaves nothing to purge, so the next command does not purge (#395).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The purge is <c>SerialPort.DiscardInBuffer</c>, and it aborts the read the pump has in
    /// flight — <see cref="SerialTransport"/> catches the resulting cancellation and carries on, by
    /// design. Before every command, that was one exception per command: <b>eight a second</b> on
    /// an idle connected receiver, unlogged, and it read as a runaway retry loop in #385's counters.
    /// </para>
    /// <para>
    /// The first command still purges, which is why this counts from one rather than zero: it
    /// follows whatever the receiver said when DTR was asserted.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ACleanTransactionMeansTheNextCommandDoesNotPurgeTheBuffer()
    {
        await using FakeTransport transport = new(_ => IdentityResponse);
        LineProtocol protocol = await ConnectAsync(transport);

        Transaction first = await protocol.ExecuteAsync("*IDN?").WaitAsync(s_testTimeout);
        Assert.Equal(TransactionOutcome.Completed, first.Outcome);
        Assert.Equal(1, transport.DiscardCount);

        for (int i = 0; i < 5; i++)
        {
            Transaction next = await protocol.ExecuteAsync("*IDN?").WaitAsync(s_testTimeout);
            Assert.Equal(TransactionOutcome.Completed, next.Outcome);
        }

        Assert.Equal(1, transport.DiscardCount);
    }

    /// <summary>
    /// A transaction that did not end on a prompt does, because the receiver may still be talking.
    /// </summary>
    /// <remarks>
    /// This is the case the purge exists for and the reason it is conditional rather than removed:
    /// a receiver that timed out mid-answer has a tail coming, and a tail read as the next
    /// command's answer misaligns the session indefinitely (#209).
    /// </remarks>
    [Fact]
    public async Task ATimedOutTransactionMeansTheNextCommandDoesPurge()
    {
        FakeTimeProvider clock = new();
        await using FakeTransport transport = new() { Silent = true };
        LineProtocol protocol = await ConnectAsync(transport, clock);

        Task<Transaction> pending = protocol.ExecuteAsync("*IDN?", TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromSeconds(4));

        Transaction timedOut = await pending.WaitAsync(s_testTimeout);
        Assert.Equal(TransactionOutcome.TimedOut, timedOut.Outcome);

        int afterFirst = transport.DiscardCount;

        // The next command purges because the last one did not finish. Nothing answers here either,
        // so the count is what is asserted rather than the outcome.
        Task<Transaction> second = protocol.ExecuteAsync("*IDN?", TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromSeconds(4));
        _ = await second.WaitAsync(s_testTimeout);

        Assert.True(
            transport.DiscardCount > afterFirst,
            $"a command after a timed-out one must purge the driver buffer; count stayed at {afterFirst} (#395)");
    }

    /// <summary>
    /// And once it is talking cleanly again, it stops purging (#395).
    /// </summary>
    [Fact]
    public async Task PurgingStopsAgainOnceTheLinkIsHealthy()
    {
        FakeTimeProvider clock = new();
        await using FakeTransport transport = new(_ => IdentityResponse);
        LineProtocol protocol = await ConnectAsync(transport, clock);

        // Healthy, healthy: one purge for the first command and none after.
        _ = await protocol.ExecuteAsync("*IDN?").WaitAsync(s_testTimeout);
        _ = await protocol.ExecuteAsync("*IDN?").WaitAsync(s_testTimeout);

        Assert.Equal(1, transport.DiscardCount);
    }

    private static async Task<LineProtocol> ConnectAsync(FakeTransport transport, TimeProvider? timeProvider = null)
    {
        await transport.OpenAsync();
        return new LineProtocol(transport, timeProvider ?? new FakeTimeProvider());
    }

    /// <summary>
    /// Builds a block the size and shape of a status screen — long enough to span many reads, and
    /// visibly synthetic so it is never mistaken for a capture.
    /// </summary>
    private static string[] BuildLongResponse(int lineCount, int lineLength)
    {
        string[] lines = new string[lineCount];
        for (int index = 0; index < lineCount; index++)
        {
            lines[index] = $"SYNTHETIC LINE {index:D2} ".PadRight(lineLength, '.');
        }

        return lines;
    }
}
