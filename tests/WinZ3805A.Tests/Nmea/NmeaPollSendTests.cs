using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Drivers.Nmea;
using WinZ3805A.Device.Models;
using WinZ3805A.Device.Transport;

namespace WinZ3805A.Tests.Nmea;

/// <summary>
/// The one thing a broadcast link writes, end to end through the listener (#508).
/// </summary>
/// <remarks>
/// <para>
/// A broadcast family is overheard and never written to — that is what <c>LinkStyle.Broadcast</c>
/// means and it stayed true for every sentence until this one. These pin the exception: the poll
/// reaches the wire, the reply is recognised despite being proprietary, and a receiver that says
/// nothing produces a timeout rather than a hang or an error.
/// </para>
/// <para>
/// <b>The bound is on the wait and not on a loop.</b> Every test here completes in milliseconds; the
/// ten seconds is there so a broken wait fails rather than taking the test host with it, which is
/// #445 and #510's shared lesson.
/// </para>
/// </remarks>
public sealed class NmeaPollSendTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 2, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>The bench VK-162's own reply, checksummed by the codec rather than by hand.</summary>
    private static string Reply()
    {
        const string body = "PUBX,04,020530.00,080926,180329.99,2435,18,-831247,-847.876,21";
        return $"${body}*{NmeaSentence.Checksum(body):X2}";
    }

    private static FakeTransport Talker(Func<string, string?> responder) => new(responder)
    {
        EchoCommands = false,
        EmitPrompt = false,
    };

    [Fact]
    public async Task ThePollReachesTheWireAndItsReplyComesBack()
    {
        FakeTimeProvider clock = new(Now);
        await using FakeTransport transport = Talker(
            command => command.Contains("PUBX,04", StringComparison.OrdinalIgnoreCase) ? Reply() : null);

        await transport.OpenAsync();

        await using BroadcastListener listener = new(transport, new NmeaDriver(clock), clock);
        listener.Start();

        Transaction answer = await listener
            .PollAsync(NmeaPoll.Outgoing, NmeaPoll.ReplyKey, Patience)
            .WaitAsync(Patience);

        Assert.Equal(TransactionOutcome.Completed, answer.Outcome);
        Assert.Contains("PUBX,04", Assert.Single(answer.Lines), StringComparison.Ordinal);

        // It really was written, rather than the reply having been lying about.
        Assert.Contains(
            transport.CommandsWritten,
            written => written.Contains("PUBX,04", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The readings the reply carries, read back through the parser that consumes it.</summary>
    [Fact]
    public async Task TheReplyYieldsTheReadingsItCarries()
    {
        FakeTimeProvider clock = new(Now);
        await using FakeTransport transport = Talker(
            command => command.Contains("PUBX,04", StringComparison.OrdinalIgnoreCase) ? Reply() : null);

        await transport.OpenAsync();

        await using BroadcastListener listener = new(transport, new NmeaDriver(clock), clock);
        listener.Start();

        Transaction answer = await listener
            .PollAsync(NmeaPoll.Outgoing, NmeaPoll.ReplyKey, Patience)
            .WaitAsync(Patience);

        PollReadings? readings = NmeaPoll.Parse(string.Join('\n', answer.Lines));

        Assert.NotNull(readings);
        Assert.Equal(18, readings.LeapSeconds);
        Assert.Equal(-847.876, readings.ClockDriftNanosecondsPerSecond);
    }

    /// <summary>
    /// A receiver that does not understand the sentence says nothing, and silence is the honest
    /// failure — the same shape as a talker that has gone quiet, and meaning the same to a caller.
    /// </summary>
    [Fact]
    public async Task AReceiverThatSaysNothingTimesOutRatherThanFailing()
    {
        FakeTimeProvider clock = new(Now);
        await using FakeTransport transport = Talker(_ => null);

        await transport.OpenAsync();

        await using BroadcastListener listener = new(transport, new NmeaDriver(clock), clock);
        listener.Start();

        Task<Transaction> polling = listener.PollAsync(NmeaPoll.Outgoing, NmeaPoll.ReplyKey, TimeSpan.FromSeconds(2));

        // The timeout is on the injected clock, so it is wound rather than waited for — the test
        // takes milliseconds and does not care how busy the machine is (#510).
        while (!polling.IsCompleted)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(5, CancellationToken.None);
        }

        Transaction answer = await polling.WaitAsync(Patience);

        Assert.Equal(TransactionOutcome.TimedOut, answer.Outcome);
        Assert.Empty(answer.Lines);
    }

    /// <summary>
    /// The driver offers the poll's text for the poll's key and nothing for anything else, so the
    /// session cannot be talked into writing on a broadcast link by an unfamiliar mnemonic.
    /// </summary>
    [Fact]
    public void OnlyThePollHasOutgoingText()
    {
        NmeaDriver driver = new(new FakeTimeProvider(Now));

        Assert.Equal(NmeaPoll.Outgoing, driver.OutgoingTextFor(NmeaPoll.ReplyKey));
        Assert.Null(driver.OutgoingTextFor("$--RMC"));
        Assert.Null(driver.OutgoingTextFor(PollPlan.WholeCycle));
        Assert.Null(driver.OutgoingTextFor(null));
    }

    /// <summary>
    /// What the driver offers to send must pass its own refusal, or §8.1's guarantee that every
    /// command sent came from the allowlist is only as good as one method agreeing with another.
    /// </summary>
    [Fact]
    public void TheTextTheDriverOffersIsNotRefusedByItsOwnRule()
    {
        NmeaDriver driver = new(new FakeTimeProvider(Now));

        string? outgoing = driver.OutgoingTextFor(NmeaPoll.ReplyKey);

        Assert.NotNull(outgoing);
        Assert.False(driver.IsBlocked(outgoing));
    }

    /// <summary>The reply is recognised, though its talker is the proprietary P rather than a GNSS one.</summary>
    [Fact]
    public void TheReplyIsClassifiedDespiteBeingProprietary()
    {
        NmeaDriver driver = new(new FakeTimeProvider(Now));

        Assert.Equal(NmeaPoll.ReplyKey, driver.ClassifyLine(Reply()));
    }
}
