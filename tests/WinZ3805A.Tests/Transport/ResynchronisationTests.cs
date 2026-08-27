using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Transport;

namespace WinZ3805A.Tests.Transport;

/// <summary>
/// What happens to the wire when a reply is abandoned part-read (#209, §7.2).
/// </summary>
/// <remarks>
/// <para>
/// Not hypothetical. On 24 Aug 2026 three consecutive polls parsed replies belonging to other
/// commands: one stored an EFC reading as a sync state, and two wrote time intervals of two and
/// three <b>seconds</b> into <c>trend.db</c>, where three bad samples out of 12,488 made every chart
/// over a seven-day window unreadable. The trigger was navigating away from the Diagnostics page,
/// which cancels its diagnostic-log read — a 15 kB reply, about sixteen seconds of wire at 9600 baud.
/// </para>
/// <para>
/// <b>The timing in these tests is the whole point, and getting it wrong makes them pass against the
/// bug.</b> The first version of this file emitted the abandoned reply's tail <i>before</i> issuing
/// the next command, and every test passed with the fix removed — because bytes already waiting are
/// exactly what <c>DiscardStaleInput</c> has always handled. The defect is the bytes that have
/// <b>not arrived yet</b>: the receiver is still transmitting while the next command is written, so
/// its tail lands in that command's read. Every test below therefore emits the tail only after the
/// following command is in flight.
/// </para>
/// </remarks>
public class ResynchronisationTests
{
    private const string Prompt = "scpi > ";
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    // WaitForReaderToConsume on every transport here, and it is load-bearing rather than tidy.
    // Whether a realignment is owed depends on whether any of the reply had arrived, so a test that
    // emits a line and then advances the clock without waiting for the protocol to read it is
    // testing the other branch half the time. That is what the first version of the timeout test
    // did, and it failed five runs in six.

    /// <summary>Waits for something the protocol does on a continuation, without a fixed sleep.</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource giveUp = new(Settle);

        while (!condition() && !giveUp.IsCancellationRequested)
        {
            await Task.Delay(5, CancellationToken.None);
        }

        Assert.True(condition(), "the protocol did not reach the expected state in time");
    }

    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// #209 end to end. The caller abandons a long reply; the next command goes out; the rest of the
    /// old reply arrives <i>while that command is waiting for its own answer</i>. It must get its
    /// own answer, not the tail.
    /// </remarks>
    [Fact]
    public async Task ACancelledReplysTailDoesNotBecomeTheNextCommandsAnswer()
    {
        FakeTimeProvider clock = new();
        await using FakeTransport transport = new() { EchoCommands = false, WaitForReaderToConsume = true };
        await transport.OpenAsync();

        LineProtocol protocol = new(transport, clock);

        using CancellationTokenSource giveUp = new();
        Task<Transaction> abandoned = protocol.ExecuteAsync(
            ":DIAG:LOG:READ:ALL?", TimeSpan.FromSeconds(60), giveUp.Token);

        await transport.ReadCommandAsync();
        await transport.EmitAsync("LOG 001:20070108.12:04:16:  GPS LOCK STARTED\r\n");

        await giveUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        // The next command is issued while the receiver is still mid-reply.
        Task<Transaction> next = protocol.ExecuteAsync(
            ":SYNC:STAT?", TimeSpan.FromSeconds(3), CancellationToken.None);

        // The receiver never learned anyone stopped listening, so it finishes what it started.
        await transport.EmitAsync("LOG 002:20070108.14:55:09:  HOLDOVER STARTED\r\n");
        await transport.EmitAsync($"LOG 003:20070108.15:07:57:  GPS LOCK STARTED\r\n{Prompt}");

        // Only now can the new command reach the wire, because the link was busy until that prompt.
        Assert.Equal(":SYNC:STAT?", await transport.ReadCommandAsync().AsTask().WaitAsync(Settle));
        await transport.EmitAsync($"LOCK\r\n{Prompt}");

        Transaction status = await next.WaitAsync(Settle);

        Assert.Equal(TransactionOutcome.Completed, status.Outcome);
        Assert.Equal(["LOCK"], status.Lines);
        Assert.DoesNotContain(status.Lines, line => line.StartsWith("LOG", StringComparison.Ordinal));
    }

    /// <remarks>
    /// The same for a timeout. Part of the reply had arrived, so the receiver is mid-sentence
    /// whichever way the transaction ended.
    /// </remarks>
    [Fact]
    public async Task ATimedOutReplysTailIsAlsoSwallowed()
    {
        FakeTimeProvider clock = new();
        await using FakeTransport transport = new() { EchoCommands = false, WaitForReaderToConsume = true };
        await transport.OpenAsync();

        LineProtocol protocol = new(transport, clock);

        Task<Transaction> slow = protocol.ExecuteAsync(
            ":SYST:STAT?", TimeSpan.FromSeconds(15), CancellationToken.None);

        await transport.ReadCommandAsync();
        await transport.EmitAsync("first line of the screen\r\n");

        clock.Advance(TimeSpan.FromSeconds(16));
        Assert.Equal(TransactionOutcome.TimedOut, (await slow.WaitAsync(Settle)).Outcome);

        Task<Transaction> next = protocol.ExecuteAsync(
            ":SYNC:STAT?", TimeSpan.FromSeconds(3), CancellationToken.None);

        await transport.EmitAsync($"the rest of the screen\r\n{Prompt}");

        Assert.Equal(":SYNC:STAT?", await transport.ReadCommandAsync().AsTask().WaitAsync(Settle));
        await transport.EmitAsync($"LOCK\r\n{Prompt}");

        Assert.Equal(["LOCK"], (await next.WaitAsync(Settle)).Lines);
    }

    /// <remarks>
    /// <b>The case that must not cost anything.</b> A transaction that received nothing at all was
    /// talking to a device that is silent or gone, and there is no tail to drain. Realigning would
    /// wait for a prompt that is not coming, and §7.2 allows three consecutive timeouts before
    /// reconnecting — so doing it would triple the time spent discovering a dead link.
    /// </remarks>
    [Fact]
    public async Task ASilentDeviceIsNotWaitedForTwice()
    {
        FakeTimeProvider clock = new();
        await using FakeTransport transport = new() { EchoCommands = false, WaitForReaderToConsume = true };
        await transport.OpenAsync();

        LineProtocol protocol = new(transport, clock);

        Task<Transaction> first = protocol.ExecuteAsync(
            ":SYNC:STAT?", TimeSpan.FromSeconds(3), CancellationToken.None);
        await transport.ReadCommandAsync();

        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(TransactionOutcome.TimedOut, (await first.WaitAsync(Settle)).Outcome);

        // Nothing arrived, so nothing is owed: this must reach the wire without waiting for a
        // prompt, and without the clock being advanced at all.
        Task<Transaction> second = protocol.ExecuteAsync(
            ":SYNC:STAT?", TimeSpan.FromSeconds(3), CancellationToken.None);

        Assert.Equal(":SYNC:STAT?", await transport.ReadCommandAsync().AsTask().WaitAsync(Settle));
        await transport.EmitAsync($"LOCK\r\n{Prompt}");

        Assert.Equal(["LOCK"], (await second.WaitAsync(Settle)).Lines);
    }

    /// <summary>A realignment that never finds a prompt gives up on its own budget.</summary>
    /// <remarks>
    /// The budget is the abandoned transaction's own, because that is the longest its remaining
    /// reply can take. Without a bound, one cancelled read on a sick link would hang every command
    /// after it — trading a misaligned session for a stuck one.
    /// </remarks>
    [Fact]
    public async Task ARealignmentThatFindsNoPromptGivesUp()
    {
        FakeTimeProvider clock = new();
        await using FakeTransport transport = new() { EchoCommands = false, WaitForReaderToConsume = true };
        await transport.OpenAsync();

        LineProtocol protocol = new(transport, clock);

        using CancellationTokenSource giveUp = new();
        Task<Transaction> abandoned = protocol.ExecuteAsync(
            ":DIAG:LOG:READ:ALL?", TimeSpan.FromSeconds(60), giveUp.Token);

        await transport.ReadCommandAsync();
        await transport.EmitAsync("LOG 001:  GPS LOCK STARTED\r\n");

        await giveUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        // No prompt ever arrives for the abandoned reply.
        Task<Transaction> next = protocol.ExecuteAsync(
            ":SYNC:STAT?", TimeSpan.FromSeconds(3), CancellationToken.None);

        Task<string> reaches = transport.ReadCommandAsync().AsTask();

        // Stepped rather than jumped. FakeTimeProvider fires the timers that exist when it advances,
        // and the realignment's deadline is registered on a continuation that may not have run yet —
        // one 61-second jump can land before it exists and be lost entirely.
        while (!reaches.IsCompleted)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(5, CancellationToken.None);
        }

        Assert.Equal(":SYNC:STAT?", await reaches.WaitAsync(Settle));
        await transport.EmitAsync($"LOCK\r\n{Prompt}");

        Assert.Equal(["LOCK"], (await next.WaitAsync(Settle)).Lines);
    }

    /// <summary>It happens once, not before every command for the rest of the session.</summary>
    [Fact]
    public async Task RealignmentIsNotRepeated()
    {
        FakeTimeProvider clock = new();
        await using FakeTransport transport = new() { EchoCommands = false, WaitForReaderToConsume = true };
        await transport.OpenAsync();

        LineProtocol protocol = new(transport, clock);

        using CancellationTokenSource giveUp = new();
        Task<Transaction> abandoned = protocol.ExecuteAsync(
            ":DIAG:LOG:READ:ALL?", TimeSpan.FromSeconds(60), giveUp.Token);

        await transport.ReadCommandAsync();
        await transport.EmitAsync("LOG 001:  GPS LOCK STARTED\r\n");
        await giveUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        Task<Transaction> first = protocol.ExecuteAsync(
            ":SYNC:STAT?", TimeSpan.FromSeconds(3), CancellationToken.None);

        await transport.EmitAsync($"LOG 002:  done\r\n{Prompt}");
        await transport.ReadCommandAsync().AsTask().WaitAsync(Settle);
        await transport.EmitAsync($"LOCK\r\n{Prompt}");
        Assert.Equal(["LOCK"], (await first.WaitAsync(Settle)).Lines);

        // Two more, with no tail outstanding. Each must reach the wire straight away.
        for (int i = 0; i < 2; i++)
        {
            Task<Transaction> again = protocol.ExecuteAsync(
                ":SYNC:STAT?", TimeSpan.FromSeconds(3), CancellationToken.None);

            await transport.ReadCommandAsync().AsTask().WaitAsync(Settle);
            await transport.EmitAsync($"WAIT\r\n{Prompt}");

            Assert.Equal(["WAIT"], (await again.WaitAsync(Settle)).Lines);
        }
    }

    /// <summary>The realignment holds the wire until the old reply ends, rather than racing it.</summary>
    /// <remarks>
    /// Stated as its own test because it is the property the fix turns on: the next command must not
    /// be written while the receiver is still talking. If it were, the answers would interleave and
    /// no amount of discarding afterwards would sort them out.
    /// </remarks>
    [Fact]
    public async Task TheNextCommandIsHeldBackUntilTheOldReplyEnds()
    {
        FakeTimeProvider clock = new();
        await using FakeTransport transport = new() { EchoCommands = false, WaitForReaderToConsume = true };
        await transport.OpenAsync();

        LineProtocol protocol = new(transport, clock);

        using CancellationTokenSource giveUp = new();
        Task<Transaction> abandoned = protocol.ExecuteAsync(
            ":DIAG:LOG:READ:ALL?", TimeSpan.FromSeconds(60), giveUp.Token);

        await transport.ReadCommandAsync();
        await transport.EmitAsync("LOG 001:  GPS LOCK STARTED\r\n");
        await giveUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        int before = transport.CommandsWritten.Count;

        _ = protocol.ExecuteAsync(":SYNC:STAT?", TimeSpan.FromSeconds(3), CancellationToken.None);

        // Give it every chance to write early.
        await Task.Delay(100, CancellationToken.None);
        Assert.Equal(before, transport.CommandsWritten.Count);

        await transport.EmitAsync($"LOG 002:  done\r\n{Prompt}");

        await UntilAsync(() => transport.CommandsWritten.Count == before + 1);
    }
}
