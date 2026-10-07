using System.Text;

using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Drivers.Uccm;
using WinZ3805A.Device.Models;
using WinZ3805A.Device.Transport;
using WinZ3805A.Simulation;

namespace WinZ3805A.Tests.Uccm;

/// <summary>
/// The simulator's wire output, through the transport's real framing and line splitting (#738).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these go through <see cref="LineProtocol"/> rather than straight to a parser.</b> The
/// defect lived between the two. The simulator's port and pipe modes wrote each time code as a line
/// of hex text, which no module sends; the driver lifts a <i>binary</i> frame out of the byte stream
/// before splitting lines, so the text went straight past that and arrived as a line of its own.
/// Polling <c>LED:GPSL?</c>, the application sometimes took that line for the answer — an unknown
/// sync state — and the primary window showed a connected, locked receiver as Disconnected. Every
/// test that handed <see cref="UccmModuleSimulator.Respond"/> to a parser passed throughout, because
/// a parser skips the hex-text form by design.
/// </para>
/// <para>
/// So the bytes here are <see cref="UccmModuleSimulator.RespondOnWire"/> and
/// <see cref="UccmModuleSimulator.TimeCodeFrame"/> — exactly what <c>Program.cs</c> writes — and the
/// protocol is built with the driver's own <see cref="IReceiverDriver.Prompt"/> and
/// <see cref="IReceiverDriver.BinaryFrames"/>, as the session builds it.
/// </para>
/// <para>
/// <b>Tried against the old wire form, deliberately.</b> With <see cref="AskAsync"/> writing
/// <c>Respond</c>'s text and <c>TimeCodeLine</c> instead, every test below that touches the wire
/// fails — and the lamp's transaction comes back with the hex text as its first line, ahead of the
/// echo and the <c>1</c>, which is the issue's log reproduced.
/// </para>
/// </remarks>
public sealed class UccmSimulatorWireTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan s_testTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A locked frame from the 13 Sep 2026 sitting, <c>frames-13sep2026.txt</c> line 7, verbatim.
    /// </summary>
    private static ReadOnlySpan<byte> CapturedLockedFrame =>
    [
        0xC5, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x28, 0x1C, 0x52, 0x00,
        0x00, 0x20, 0x60, 0xC1, 0x91, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x57, 0xD0, 0x5B, 0xDE, 0x00, 0x12,
        0x60, 0x04, 0x45, 0x80, 0x00, 0x00, 0x00, 0x00, 0x62, 0xF4, 0xCA,
    ];

    // ---- the frame itself ---------------------------------------------------------------------------

    /// <summary>The frame is laid out as the captured one is, wherever the capture is constant.</summary>
    /// <remarks>
    /// A locked Trimble UCCM-P against a locked frame off the bench: every byte must agree except the
    /// four that carry the time and the two that carry the unidentified checksum, which the simulator
    /// leaves zero. Offsets 32 to 36 included — leap, PPS, antenna, lock and date — because for the
    /// locked state the simulator's values are the measured ones.
    /// </remarks>
    [Fact]
    public void ALockedTrimbleFrameMatchesTheCapturedOneOutsideTheClockAndTheChecksum()
    {
        UccmModuleSimulator module = new(UccmVendor.Trimble, UccmVariant.UccmP, new FakeTimeProvider(Start));

        byte[] frame = module.TimeCodeFrame();

        Assert.Equal(CapturedLockedFrame.Length, frame.Length);
        for (int offset = 0; offset < frame.Length; offset++)
        {
            if (offset is (>= 27 and <= 30) or 41 or 42)
            {
                continue;
            }

            Assert.True(
                CapturedLockedFrame[offset] == frame[offset],
                $"offset {offset}: captured 0x{CapturedLockedFrame[offset]:X2}, simulated 0x{frame[offset]:X2}");
        }

        Assert.Equal(0, frame[41]);
        Assert.Equal(0, frame[42]);
    }

    /// <summary>The frame is one the transport's grammar and the driver's decoder both accept.</summary>
    [Fact]
    public void TheFrameIsOneTheDriverFramesAndDecodes()
    {
        FakeTimeProvider clock = new(Start);
        UccmModuleSimulator module = new(UccmVendor.Trimble, UccmVariant.UccmP, clock);
        IReceiverDriver driver = new UccmDriver(clock);

        byte[] frame = module.TimeCodeFrame();

        Assert.True(driver.BinaryFrames.IsFrame(frame));
        UccmTimeCode? code = UccmTimeCode.TryParse(frame.AsSpan());
        Assert.NotNull(code);
        Assert.Equal(Start, code.UtcTime);
        Assert.Equal(UccmVendor.Trimble, code.SuggestedVendor);
    }

    /// <summary><see cref="UccmModuleSimulator.Respond"/>'s hex text is the same frame, rendered.</summary>
    [Fact]
    public void TheHexTextIsTheSameFrameRendered()
    {
        UccmModuleSimulator module = new(UccmVendor.Symmetricom, UccmVariant.Uccm, new FakeTimeProvider(Start));

        Assert.Equal(
            Convert.ToHexString(module.TimeCodeFrame()),
            module.TimeCodeLine().Replace(" ", string.Empty, StringComparison.Ordinal));
    }

    // ---- the wire, through the real framing ---------------------------------------------------------

    /// <summary>
    /// The defect itself: the lamp's answer is the lamp's answer, with a time code on either side.
    /// </summary>
    /// <remarks>
    /// <b>The arrangement is the harshest the program can produce.</b> A ticker frame lands after the
    /// command and before the reply, <c>--interleave</c> puts another between the echo and the value,
    /// and a third trails the prompt as every measured one does. Under the old wire form each of those
    /// was a line of text in the reply.
    /// </remarks>
    [Theory]
    [InlineData(UccmVendor.Trimble, UccmVariant.UccmP)]
    [InlineData(UccmVendor.Trimble, UccmVariant.Uccm)]
    [InlineData(UccmVendor.Symmetricom, UccmVariant.UccmP)]
    [InlineData(UccmVendor.Symmetricom, UccmVariant.Uccm)]
    public async Task TheLampsAnswerIsNotATimeCode(UccmVendor vendor, UccmVariant variant)
    {
        FakeTimeProvider clock = new(Start);
        UccmDriver driver = new(clock);
        UccmModuleSimulator module = new(vendor, variant, clock) { InterleaveTimeCode = true };

        await using FakeTransport transport = new();
        await transport.OpenAsync();
        LineProtocol protocol = Protocol(transport, driver);

        Transaction transaction = await AskAsync(protocol, transport, module, driver, UccmCommands.LockLed);

        Assert.Equal(TransactionOutcome.Completed, transaction.Outcome);
        Assert.Equal(["1", "COMMAND COMPLETE"], transaction.Lines);
        Assert.NotEmpty(transaction.BinaryFrames);
        Assert.Equal(ReceiverMode.Locked, driver.InterpretSyncState(ScalarAnswer(transaction)));
    }

    /// <summary>
    /// A whole fast sweep and a status read, in every vendor, variant and state, with nothing
    /// time-code-shaped reaching a line and every reply still read.
    /// </summary>
    [Fact]
    public async Task NoTimeCodeReachesALineInAnyVendorVariantOrState()
    {
        foreach (UccmVendor vendor in new[] { UccmVendor.Symmetricom, UccmVendor.Trimble })
        {
            foreach (UccmVariant variant in Enum.GetValues<UccmVariant>())
            {
                foreach (UccmSimulatedState state in Enum.GetValues<UccmSimulatedState>())
                {
                    FakeTimeProvider clock = new(Start);
                    UccmDriver driver = new(clock);
                    UccmModuleSimulator module = new(vendor, variant, clock)
                    {
                        State = state,
                        AntennaConnected = state != UccmSimulatedState.Holdover,
                        InterleaveTimeCode = true,
                    };

                    await using FakeTransport transport = new();
                    await transport.OpenAsync();
                    LineProtocol protocol = Protocol(transport, driver);

                    List<string?> answers = [];
                    foreach (string command in driver.Plan.FastTier)
                    {
                        Transaction transaction = await AskAsync(protocol, transport, module, driver, command);
                        AssertNoTimeCodeText(transaction, $"{vendor} {variant} {state} {command}");
                        answers.Add(transaction.Text);
                    }

                    SweepInterpretation sweep = driver.InterpretSweep(answers);
                    Assert.Null(sweep.Rejection);
                    Assert.Equal(state == UccmSimulatedState.Locked ? "1" : "0", answers[0]?.Split('\n')[0]);

                    Transaction status = await AskAsync(protocol, transport, module, driver, driver.Plan.FullStatus);
                    AssertNoTimeCodeText(status, $"{vendor} {variant} {state} status");
                    Assert.Equal(module.SatelliteCount, driver.Parse(status.Text).Tracked.Count);
                }
            }
        }
    }

    /// <summary>
    /// The status reply on the wire carries no time code in its text, so the clock it shows comes
    /// from the frame the transport lifted out — the path a real module takes.
    /// </summary>
    [Fact]
    public async Task TheStatusClockComesFromTheLiftedFrame()
    {
        FakeTimeProvider clock = new(Start);
        UccmDriver driver = new(clock);
        UccmModuleSimulator module = new(UccmVendor.Trimble, UccmVariant.UccmP, clock) { SatelliteCount = 9 };

        await using FakeTransport transport = new();
        await transport.OpenAsync();
        LineProtocol protocol = Protocol(transport, driver);

        Transaction transaction = await AskAsync(protocol, transport, module, driver, UccmCommands.Status);
        ReceiverStatus status = driver.Parse(transaction.Text);

        AssertNoTimeCodeText(transaction, "status");
        Assert.Equal(Start, status.DeviceDateTime);
        Assert.Equal(3, status.Tfom);
        Assert.Equal(9, status.Tracked.Count);
        Assert.Empty(status.ParseWarnings);
    }

    private static LineProtocol Protocol(FakeTransport transport, IReceiverDriver driver) =>
        new(transport, new FakeTimeProvider(), prompt: driver.Prompt, frames: driver.BinaryFrames);

    /// <summary>
    /// One transaction as the program runs it: a ticker frame, the wire reply, a trailing frame —
    /// and the frames handed to the driver as the session hands them.
    /// </summary>
    private static async Task<Transaction> AskAsync(
        LineProtocol protocol,
        FakeTransport transport,
        UccmModuleSimulator module,
        IReceiverDriver driver,
        string command)
    {
        Task<Transaction> pending = protocol.ExecuteAsync(command);

        string sent = await transport.ReadCommandAsync().AsTask().WaitAsync(s_testTimeout);
        await transport.EmitAsync(module.TimeCodeFrame());
        await transport.EmitAsync(module.RespondOnWire(sent));

        Transaction transaction = await pending.WaitAsync(s_testTimeout);

        // One trailing the prompt, as every measured one did. The next transaction reads it as a
        // leftover, which is how the old hex-text line came to sit in front of the lamp's answer.
        await transport.EmitAsync(module.TimeCodeFrame());
        driver.Observe(transaction.BinaryFrames);
        return transaction;
    }

    private static string? ScalarAnswer(Transaction transaction) => transaction.FirstLine;

    private static void AssertNoTimeCodeText(Transaction transaction, string context)
    {
        Assert.True(
            transaction.Outcome == TransactionOutcome.Completed,
            $"{context}: the transaction ended {transaction.Outcome}");

        foreach (string line in transaction.Lines)
        {
            Assert.False(UccmTimeCode.IsTimeCodeLine(line), $"{context}: a time code reached a line: {line}");
            Assert.False(
                line.Contains("C5 00", StringComparison.Ordinal) || line.Contains(" CA", StringComparison.Ordinal),
                $"{context}: time-code text reached a line: {line}");
            Assert.True(
                line.All(c => c is >= ' ' and <= '~'),
                $"{context}: a non-printable byte reached a line: {Convert.ToHexString(Encoding.Latin1.GetBytes(line))}");
        }
    }
}
