using System.Text;
using Microsoft.Extensions.Time.Testing;
using WinZ3805A.Device.Transport;

namespace WinZ3805A.Tests.Transport;

/// <summary>
/// A prompted family that also talks unprompted (#470).
/// </summary>
/// <remarks>
/// <para>
/// §7.2 was written for a receiver that speaks only when spoken to, and <c>LineProtocol</c> took one
/// liberty on the strength of it: a prompt had to be the <i>whole</i> of what followed the last line
/// ending, on the reasoning that anything longer must be a response line still arriving. A Trimble
/// UCCM-P broadcasts a 44-byte binary time code about once a second, and one landing straight after
/// the prompt made the tail too long to test — so the transaction ran to its full timeout with the
/// answer already in its <c>Lines</c>, and auto-detect reported that nothing had answered.
/// </para>
/// <para>
/// <b>The bytes below are a capture, not a plausible arrangement of bytes.</b> They were read off
/// COM3 on 10 Sep 2026 from <c>TRIMBLE,57964-80,40896646,V2.0.1.6-01</c> at 57600-8-N-1. The frame
/// contains no CR and no LF, which is exactly why it stays in the tail rather than being consumed as
/// lines — a synthetic frame that happened to contain either would not reproduce this at all.
/// </para>
/// </remarks>
public class UnsolicitedTrafficTests
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The measured identity of the module these bytes came from.</summary>
    private const string UccmIdentity = "TRIMBLE,57964-80,40896646,V2.0.1.6-01";

    /// <summary>The measured prompt. No trailing space: the next byte is the time code's marker.</summary>
    private const string UccmPrompt = "UCCM-P >";

    /// <summary>
    /// One complete unsolicited time code, captured between two <c>C5</c> markers one second apart.
    /// </summary>
    private static ReadOnlySpan<byte> TimeCode =>
    [
        0xC5, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x28, 0x1C, 0x52, 0x00,
        0x00, 0x20, 0x60, 0xC1, 0x91, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x57, 0xCE, 0x19, 0x7E, 0x00, 0x12,
        0x60, 0x04, 0x45, 0x80, 0x00, 0x00, 0x00, 0x00, 0x3D, 0xA1, 0xCA,
    ];

    private static PromptGrammar Uccm { get; } = new() { Words = ["UCCM-P"] };

    /// <summary>
    /// The reply completes even though a time code is packed in behind the prompt.
    /// </summary>
    /// <remarks>
    /// <b>The single write is the test.</b> Emitted separately, the prompt would be the whole tail
    /// for one read and the old code would match it — so a test that wrote them apart would pass
    /// against the defect it exists to catch. On the wire they arrive back to back, which is how the
    /// capture has them, and 8 + 44 bytes is over the 32-byte limit the matcher used to impose.
    /// </remarks>
    [Fact]
    public async Task AReplyCompletesWhenAnUnsolicitedFrameFollowsThePromptInOneRead()
    {
        await using FakeTransport transport = new();
        await transport.OpenAsync();
        LineProtocol protocol = new(transport, new FakeTimeProvider(), prompt: Uccm);

        Task<Transaction> pending = protocol.ExecuteAsync("*IDN?");

        Assert.Equal("*IDN?", await transport.ReadCommandAsync().AsTask().WaitAsync(s_testTimeout));
        await transport.EmitAsync(UccmIdentity + "\r\n");
        await transport.EmitAsync("Command complete\r\n");
        await transport.EmitAsync(PromptFollowedByTimeCode());

        Transaction transaction = await pending.WaitAsync(s_testTimeout);

        Assert.Equal(TransactionOutcome.Completed, transaction.Outcome);
        Assert.Equal([UccmIdentity, "Command complete"], transaction.Lines);
        Assert.Null(transaction.PromptStatus);
    }

    /// <summary>
    /// The module does not echo, so nothing is discarded as one.
    /// </summary>
    /// <remarks>
    /// #416 took "the module echoes each command before answering" from Lady Heather's source and
    /// flagged it as a hypothesis. Eight sittings on 10 Sep 2026 produced no echo at all. This pins
    /// the consequence rather than the claim: <c>LineProtocol</c> compares rather than assumes
    /// (§7.2), so a family that turns out not to echo loses nothing — and if a Symmetricom module
    /// later does echo, the other half of that comparison already handles it.
    /// </remarks>
    [Fact]
    public async Task NothingIsTakenForAnEchoWhenTheReceiverDoesNotEcho()
    {
        await using FakeTransport transport = new();
        await transport.OpenAsync();
        LineProtocol protocol = new(transport, new FakeTimeProvider(), prompt: Uccm);

        Task<Transaction> pending = protocol.ExecuteAsync("*IDN?");

        Assert.Equal("*IDN?", await transport.ReadCommandAsync().AsTask().WaitAsync(s_testTimeout));
        await transport.EmitAsync(UccmIdentity + "\r\n");
        await transport.EmitAsync(PromptFollowedByTimeCode());

        Transaction transaction = await pending.WaitAsync(s_testTimeout);

        Assert.False(transaction.EchoDiscarded);
        Assert.Equal([UccmIdentity], transaction.Lines);
    }

    /// <summary>
    /// The SmartClock is unaffected: its prompt still ends a transaction with nothing behind it.
    /// </summary>
    /// <remarks>
    /// Looking at the start of the tail rather than at the whole of it is a widening, and a widening
    /// is where a family that was working stops working. This is the guard on that.
    /// </remarks>
    [Fact]
    public async Task TheSmartClockPromptStillEndsATransactionOnItsOwn()
    {
        await using FakeTransport transport = new(_ => "SYMMETRICOM,Z3805A,3625A02931,1.01.03-A");
        await transport.OpenAsync();
        LineProtocol protocol = new(transport, new FakeTimeProvider());

        Transaction transaction = await protocol.ExecuteAsync("*IDN?").WaitAsync(s_testTimeout);

        Assert.Equal(TransactionOutcome.Completed, transaction.Outcome);
        Assert.Equal(["SYMMETRICOM,Z3805A,3625A02931,1.01.03-A"], transaction.Lines);
    }

    private static byte[] PromptFollowedByTimeCode()
    {
        byte[] prompt = Encoding.Latin1.GetBytes(UccmPrompt);
        byte[] both = new byte[prompt.Length + TimeCode.Length];

        prompt.CopyTo(both, 0);
        TimeCode.CopyTo(both.AsSpan(prompt.Length));

        return both;
    }
}
