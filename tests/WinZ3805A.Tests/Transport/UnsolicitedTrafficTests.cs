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

    // ---- #481: the frames are lifted out, not merely tolerated -----------------------------------

    /// <summary>
    /// A real frame, from the 10 Sep 2026 sitting, whose payload contains <c>0x0A</c>.
    /// </summary>
    /// <remarks>
    /// <b>This is the frame that decides the design.</b> Byte 41 is <c>0x0A</c> — a line feed, in
    /// what looks like a checksum. Split into lines first and the frame is cut in two with no way
    /// back, because CRLF collapsing has by then discarded which byte the delimiter was. One frame
    /// in the seven captured across three sittings is like this, and since a checksum takes
    /// arbitrary values it is a rate, not a curiosity.
    /// </remarks>
    private static ReadOnlySpan<byte> TimeCodeContainingLineFeed =>
    [
        0xC5, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x28, 0x1C, 0x52, 0x00,
        0x00, 0x20, 0x60, 0xC1, 0x91, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x57, 0xCC, 0xD4, 0x2C, 0x00, 0x12,
        0x60, 0x04, 0x45, 0x80, 0x00, 0x00, 0x00, 0x00, 0x0A, 0x52, 0xCA,
    ];

    private static LineProtocol UccmProtocol(FakeTransport transport) =>
        new(transport, new FakeTimeProvider(), prompt: Uccm, frames: BinaryFrameGrammar.UccmTimeCode);

    /// <summary>A trailing frame is handed over whole and never appears among the lines.</summary>
    [Fact]
    public async Task ATrailingFrameIsSurfacedAndKeptOutOfTheLines()
    {
        await using FakeTransport transport = new();
        await transport.OpenAsync();
        LineProtocol protocol = UccmProtocol(transport);

        Task<Transaction> pending = protocol.ExecuteAsync("*IDN?");

        Assert.Equal("*IDN?", await transport.ReadCommandAsync().AsTask().WaitAsync(s_testTimeout));
        await transport.EmitAsync(UccmIdentity + "\r\n");
        await transport.EmitAsync(PromptFollowedByTimeCode());

        Transaction transaction = await pending.WaitAsync(s_testTimeout);

        Assert.Equal([UccmIdentity], transaction.Lines);
        Assert.Equal(TimeCode.ToArray(), Assert.Single(transaction.BinaryFrames));
    }

    /// <summary>
    /// A frame arriving <i>before</i> the reply does not get glued to the front of it.
    /// </summary>
    /// <remarks>
    /// <b>This is the measured defect.</b> A frame trails reply <i>n</i>, so it is still in the
    /// buffer when reply <i>n+1</i> is read. Driving <c>*IDN?</c> repeatedly through the Advanced
    /// Console on 12 Sep 2026, about 3 sends in 34 came back with binary in front of the identity,
    /// and one came back as a bare <c>0xC5</c> with the identity lost entirely. #481 predicted this
    /// exact shape as a hypothesis — a scalar reply beginning with its value directly would get the
    /// junk glued to the front of it — and said it should be looked for rather than assumed. It was
    /// looked for, and it is real.
    /// </remarks>
    [Fact]
    public async Task AFrameLeftOverFromTheLastReplyDoesNotCorruptTheNextOne()
    {
        await using FakeTransport transport = new();
        await transport.OpenAsync();
        LineProtocol protocol = UccmProtocol(transport);

        Task<Transaction> pending = protocol.ExecuteAsync("*IDN?");

        Assert.Equal("*IDN?", await transport.ReadCommandAsync().AsTask().WaitAsync(s_testTimeout));
        await transport.EmitAsync(TimeCodeThenText(UccmIdentity + "\r\n"));
        await transport.EmitAsync(Encoding.Latin1.GetBytes(UccmPrompt));

        Transaction transaction = await pending.WaitAsync(s_testTimeout);

        Assert.Equal([UccmIdentity], transaction.Lines);
        Assert.Single(transaction.BinaryFrames);
    }

    /// <summary>
    /// A frame carrying a line feed survives whole, and does not invent a line.
    /// </summary>
    /// <remarks>
    /// The case a line-level fix cannot reach, and the reason framing happens on bytes. Without the
    /// byte pass the <c>0x0A</c> at offset 41 ends a "line", so the reply gains a junk line, the
    /// frame is destroyed, and its two halves are unrecoverable.
    /// </remarks>
    [Fact]
    public async Task AFrameContainingALineFeedIsNotSplitAcrossLines()
    {
        await using FakeTransport transport = new();
        await transport.OpenAsync();
        LineProtocol protocol = UccmProtocol(transport);

        Task<Transaction> pending = protocol.ExecuteAsync("*IDN?");

        Assert.Equal("*IDN?", await transport.ReadCommandAsync().AsTask().WaitAsync(s_testTimeout));
        await transport.EmitAsync(UccmIdentity + "\r\n");
        await transport.EmitAsync(PromptThen(TimeCodeContainingLineFeed));

        Transaction transaction = await pending.WaitAsync(s_testTimeout);

        Assert.Equal([UccmIdentity], transaction.Lines);
        Assert.Equal(TimeCodeContainingLineFeed.ToArray(), Assert.Single(transaction.BinaryFrames));
    }

    /// <summary>
    /// A marker byte that does not open a well-formed frame stays in the text.
    /// </summary>
    /// <remarks>
    /// The 44-byte length is one family's observation across seven frames, not a specification. A
    /// stray <c>0xC5</c> is the evidence that it is wrong somewhere, so swallowing it would destroy
    /// the one signal that would say so — the same rule <c>Capture-Uccm.ps1</c> applies when it
    /// reports unframed markers rather than ignoring them.
    ///
    /// <para>
    /// <b>The line is long on purpose.</b> A marker cannot be judged until the frame's whole length
    /// has arrived, so one near the end of the buffer defers rather than resolving — see
    /// <see cref="AFrameSplitAcrossTwoReadsIsStillRecognisedWhole"/>. Writing this test with a short
    /// line made it hang instead of fail, which is worth knowing about the rule rather than about
    /// the test.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AMarkerThatDoesNotOpenAFrameIsLeftInTheText()
    {
        await using FakeTransport transport = new();
        await transport.OpenAsync();
        LineProtocol protocol = UccmProtocol(transport);

        string strayMarker = "AÅ" + new string('B', 60);

        Task<Transaction> pending = protocol.ExecuteAsync("*IDN?");

        Assert.Equal("*IDN?", await transport.ReadCommandAsync().AsTask().WaitAsync(s_testTimeout));
        await transport.EmitAsync(strayMarker + "\r\n");
        await transport.EmitAsync(Encoding.Latin1.GetBytes(UccmPrompt));

        Transaction transaction = await pending.WaitAsync(s_testTimeout);

        Assert.Equal([strayMarker], transaction.Lines);
        Assert.Empty(transaction.BinaryFrames);
    }

    /// <summary>
    /// A frame broken across two reads is reassembled, not half-read as text.
    /// </summary>
    /// <remarks>
    /// <b>This is why an incomplete marker defers instead of resolving.</b> 44 bytes take about
    /// 7.6 ms at 57600 baud, so a read boundary lands inside a frame regularly. Judging a marker
    /// on the bytes that happen to have arrived would call a genuine frame "not a frame" and hand
    /// its first few bytes to the line reader, which is the original defect wearing a different
    /// hat.
    /// </remarks>
    [Fact]
    public async Task AFrameSplitAcrossTwoReadsIsStillRecognisedWhole()
    {
        await using FakeTransport transport = new();
        await transport.OpenAsync();
        LineProtocol protocol = UccmProtocol(transport);

        Task<Transaction> pending = protocol.ExecuteAsync("*IDN?");

        Assert.Equal("*IDN?", await transport.ReadCommandAsync().AsTask().WaitAsync(s_testTimeout));
        await transport.EmitAsync(UccmIdentity + "\r\n");
        await transport.EmitAsync(TimeCode[..20].ToArray());
        await transport.EmitAsync(TimeCode[20..].ToArray());
        await transport.EmitAsync(Encoding.Latin1.GetBytes(UccmPrompt));

        Transaction transaction = await pending.WaitAsync(s_testTimeout);

        Assert.Equal([UccmIdentity], transaction.Lines);
        Assert.Equal(TimeCode.ToArray(), Assert.Single(transaction.BinaryFrames));
    }

    /// <summary>
    /// A family that declares no frames keeps the bytes it always had.
    /// </summary>
    /// <remarks>
    /// The guard on the widening. <see cref="BinaryFrameGrammar.None"/> is what auto-detect runs
    /// with, and at a wrong baud rate the stream is noise in which a marker followed 43 bytes later
    /// by a terminator turns up by chance — swallowing 44 bytes of it would be a silent change to
    /// the one path #470 proved.
    /// </remarks>
    [Fact]
    public async Task AFamilyDeclaringNoFramesKeepsEveryByte()
    {
        await using FakeTransport transport = new();
        await transport.OpenAsync();
        LineProtocol protocol = new(transport, new FakeTimeProvider(), prompt: Uccm);

        Task<Transaction> pending = protocol.ExecuteAsync("*IDN?");

        Assert.Equal("*IDN?", await transport.ReadCommandAsync().AsTask().WaitAsync(s_testTimeout));
        await transport.EmitAsync(TimeCodeThenText(UccmIdentity + "\r\n"));
        await transport.EmitAsync(Encoding.Latin1.GetBytes(UccmPrompt));

        Transaction transaction = await pending.WaitAsync(s_testTimeout);

        // The frame is still glued to the identity, exactly as it was before #481 - which is the
        // point: nothing changed for a family that did not ask for it.
        Assert.Empty(transaction.BinaryFrames);
        Assert.EndsWith(UccmIdentity, Assert.Single(transaction.Lines), StringComparison.Ordinal);
        Assert.NotEqual(UccmIdentity, transaction.Lines[0]);
    }


    /// <summary>
    /// A frame split across two <i>transactions</i> does not corrupt the second one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the case that survived the first fix, and only hardware showed it.</b> A frame
    /// trails the prompt, so a read routinely ends with the prompt matched and the frame's first
    /// bytes behind it. The transaction completes — correctly — and those bytes wait for the next
    /// read. But the next transaction opened with <c>DiscardStaleInput</c>, which threw the frame's
    /// <i>head</i> away; the rest then arrived with no marker in front of it, nothing recognised it,
    /// and it landed on the front of the next reply.
    /// </para>
    /// <para>
    /// Measured on a Trimble UCCM-P on 12 Sep 2026 with the frame grammar confirmed active: the
    /// poller's lock-state reading came back with binary in front of it about four times a minute.
    /// Every unit test written before this one wrote a whole frame inside one transaction, so none
    /// of them could see it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFrameSplitAcrossTwoTransactionsDoesNotCorruptTheSecond()
    {
        await using FakeTransport transport = new();
        await transport.OpenAsync();
        LineProtocol protocol = UccmProtocol(transport);

        // First transaction: ends on the prompt, with the frame only part-way arrived.
        Task<Transaction> first = protocol.ExecuteAsync("*IDN?");
        Assert.Equal("*IDN?", await transport.ReadCommandAsync().AsTask().WaitAsync(s_testTimeout));
        await transport.EmitAsync(UccmIdentity + "\r\n");
        await transport.EmitAsync(PromptThen(TimeCode[..12]));

        Transaction one = await first.WaitAsync(s_testTimeout);
        Assert.Equal([UccmIdentity], one.Lines);

        // Second transaction: the rest of the frame arrives first, then the reply.
        Task<Transaction> second = protocol.ExecuteAsync("LED:GPSL?");
        Assert.Equal("LED:GPSL?", await transport.ReadCommandAsync().AsTask().WaitAsync(s_testTimeout));
        await transport.EmitAsync(TimeCode[12..].ToArray());
        await transport.EmitAsync("1\r\n");
        await transport.EmitAsync(Encoding.Latin1.GetBytes(UccmPrompt));

        Transaction two = await second.WaitAsync(s_testTimeout);

        // The reading is the reading, with no binary on the front of it.
        Assert.Equal(["1"], two.Lines);

        // And the frame, reassembled across the boundary, is reported once and whole.
        Assert.Equal(TimeCode.ToArray(), Assert.Single(two.BinaryFrames));
    }

    private static byte[] PromptThen(ReadOnlySpan<byte> frame)
    {
        byte[] prompt = Encoding.Latin1.GetBytes(UccmPrompt);
        byte[] both = new byte[prompt.Length + frame.Length];

        prompt.CopyTo(both, 0);
        frame.CopyTo(both.AsSpan(prompt.Length));

        return both;
    }

    private static byte[] TimeCodeThenText(string text)
    {
        byte[] tail = Encoding.Latin1.GetBytes(text);
        byte[] both = new byte[TimeCode.Length + tail.Length];

        TimeCode.CopyTo(both);
        tail.CopyTo(both.AsSpan(TimeCode.Length));

        return both;
    }
}
