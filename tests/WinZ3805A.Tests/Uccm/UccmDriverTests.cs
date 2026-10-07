using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Drivers.Uccm;
using WinZ3805A.Device.Models;

namespace WinZ3805A.Tests.Uccm;

/// <summary>
/// The UCCM driver's contract (#416): recognition, the reply conventions, the sweep, and the
/// never-throw rule.
/// </summary>
public sealed class UccmDriverTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static UccmDriver Driver() => new(new FakeTimeProvider(Start));

    private static DeviceIdentity Identity(string model) =>
        new("Symmetricom", model, "SN123", "1.0", ReceiverModel.Unknown);

    // ---- recognition -----------------------------------------------------------------------------

    [Theory]
    [InlineData("UCCM")]
    [InlineData("UCCM-P")]
    [InlineData("58540A UCCM")]
    public void AModelNamingAUccmIsClaimed(string model) =>
        Assert.True(Driver().Recognises(Identity(model)));

    [Theory]
    [InlineData("Z3805A")]
    [InlineData("58503B")]
    [InlineData("")]
    public void AnythingElseIsLeftToADriverThatAssumesLess(string model) =>
        Assert.False(Driver().Recognises(Identity(model)));

    [Fact]
    public void NullIdentityIsNotClaimed() => Assert.False(Driver().Recognises(null));

    [Fact]
    public void TheVendorCannotComeFromTheManufacturerFieldBecauseBothVendorsSellThem()
    {
        // #418's first section, as a test: recognition must not key on the manufacturer, because
        // the same model name arrives from two of them.
        UccmDriver driver = Driver();

        Assert.True(driver.Recognises(new("Trimble", "UCCM-P", "S", "F", ReceiverModel.Unknown)));
        Assert.True(driver.Recognises(new("Symmetricom", "UCCM-P", "S", "F", ReceiverModel.Unknown)));
        Assert.Equal(UccmVendor.Unknown, driver.Profile.Vendor);
    }

    // ---- the reply conventions -------------------------------------------------------------------

    [Fact]
    public void TheCommandEchoIsNotMistakenForTheAnswer()
    {
        // The receiver repeats the question before answering it. A reader that takes the first line
        // gets the mnemonic where it expected a number, every time.
        const string reply = "SYNC:TINT?\r\n-1.23E-9\r\nCOMMAND COMPLETE\r\n";

        Assert.Equal("-1.23E-9", UccmReply.FirstPayload(reply, "SYNC:TINT?"));
    }

    [Fact]
    public void AnEchoIsOnlyRecognisedAgainstACommandWeActuallySent()
    {
        // Never by shape. A line that merely looks command-like could be a legitimate answer, and
        // silently discarding an answer is worse than passing an echo to a parser that ignores it.
        const string reply = "SYNC:TINT?\r\n-1.23E-9\r\n";

        Assert.Equal("SYNC:TINT?", UccmReply.FirstPayload(reply, sent: null));
    }

    [Fact]
    public void AnUnsolicitedTimeCodeInTheMiddleOfAReplyIsNotReadAsTheValue()
    {
        string timeCode = string.Join(
            ' ',
            Enumerable.Range(0, 44).Select(i => (i == 0 ? 0xC5 : 0x11)
                .ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
        string reply = $"SYNC:TINT?\r\n{timeCode}\r\n-1.23E-9\r\nCOMMAND COMPLETE\r\n";

        Assert.Equal("-1.23E-9", UccmReply.FirstPayload(reply, "SYNC:TINT?"));
        Assert.Single(UccmReply.TimeCodes(reply));
    }

    [Theory]
    [InlineData("COMMAND ERROR")]
    [InlineData("UNDEFINED HEADER")]
    [InlineData("INVALID PARAMETER")]
    [InlineData("Data corrupt or stale")]
    public void TheReceiversErrorRepliesAreReadAsErrorsRatherThanValues(string text) =>
        Assert.True(UccmReply.IsError($"LED:GPSL?\r\n{text}\r\n", "LED:GPSL?"));

    [Fact]
    public void AnOrdinaryAnswerIsNotAnError() =>
        Assert.False(UccmReply.IsError("LED:GPSL?\r\n1\r\nCOMMAND COMPLETE\r\n", "LED:GPSL?"));

    // ---- the sweep --------------------------------------------------------------------------------

    [Fact]
    public void AGoodSweepReadsTheLockStateTheIntervalAndTheControlVoltage()
    {
        SweepInterpretation result = Driver().InterpretSweep(
        [
            "LED:GPSL?\r\n1\r\nCOMMAND COMPLETE",
            "SYNC:TINT?\r\n-1.23E-9\r\nCOMMAND COMPLETE",
            "DIAG:ROSC:EFC:REL?\r\n12.5\r\nCOMMAND COMPLETE",
        ]);

        Assert.Null(result.Rejection);
        Assert.Equal("1", result.Readings.SyncState);
        Assert.Equal(-1.23, result.Readings.TimeIntervalNanoseconds!.Value, 6);
        Assert.Equal(12.5, result.Readings.EfcPercent!.Value, 6);
    }

    [Fact]
    public void ASweepWhoseDiscriminatorErroredIsRejectedWithASentenceSayingWhy()
    {
        // A guard that drops readings silently is worse than no guard (#209).
        SweepInterpretation result = Driver().InterpretSweep(
        [
            "LED:GPSL?\r\nUNDEFINED HEADER\r\n",
            "SYNC:TINT?\r\n-1.23E-9\r\n",
            null,
        ]);

        Assert.NotNull(result.Rejection);
        Assert.Contains("LED:GPSL?", result.Rejection, StringComparison.Ordinal);
    }

    // ---- #209's rule, applied to the lamp (#739) --------------------------------------------------

    /// <summary>
    /// A locked frame from the 13 Sep 2026 sitting, written as the hex text the simulator sent it as.
    /// </summary>
    /// <remarks>
    /// The real module sends these 44 bytes as binary and the transport lifts them out before line
    /// splitting; the simulator sent them as a line of text (#738), and one taken as the lamp's
    /// answer put "Disconnected" on the main window of a connected session.
    /// </remarks>
    private const string TimeCodeAsText =
        "C5 00 80 00 00 00 00 28 1C 52 00 00 20 60 C1 91 00 00 00 00 00 00 " +
        "00 00 00 00 00 57 D0 B0 D0 00 12 60 04 45 80 00 00 00 00 A5 97 CA";

    /// <summary>
    /// A time code's hex text in the lamp's place is not a lock state, and the sweep is refused.
    /// </summary>
    /// <remarks>
    /// Three shapes, because the app's own log for #738 showed all three: the whole line, which the
    /// reply grammar recognises as a time code and so leaves the lamp with no answer at all; the
    /// line with its first character lost to the previous read; and a tail starting mid-frame.
    /// </remarks>
    [Theory]
    [InlineData(TimeCodeAsText)]
    [InlineData("5 00 80 00 00 00 00 28 1C 52 00 00 20 60 C1 91 00 00 00 00 00 00 00 00 00 00 00 57 D0 B0 D0 00 12 60 04 45 80 00 00 00 00 A5 97 CA")]
    [InlineData("F0 7B A9 00 12 60 04 45 80 00 00 00 00 A5 97 CA")]
    public void ATimeCodeTakenAsTheLampsAnswerIsRejected(string stray)
    {
        SweepInterpretation result = Driver().InterpretSweep(
        [
            $"{stray}\r\nCommand complete\r\n",
            "-1.23E-9\r\nCommand complete\r\n",
            "12.5\r\nCommand complete\r\n",
        ]);

        // A guard that drops readings silently is worse than no guard (#209): the sentence names
        // the query, and what was read still comes back for the poller's log.
        Assert.NotNull(result.Rejection);
        Assert.Contains(UccmCommands.LockLed, result.Rejection, StringComparison.Ordinal);
        Assert.Equal(-1.23, result.Readings.TimeIntervalNanoseconds!.Value, 6);
        Assert.Equal(12.5, result.Readings.EfcPercent!.Value, 6);
    }

    /// <summary>
    /// Every lamp answer the driver already recognised is still a reading.
    /// </summary>
    /// <remarks>
    /// The set is <c>InterpretSyncState</c>'s and nothing new: <c>0</c> and <c>1</c> as a Trimble
    /// UCCM-P answers them, measured, with and without the echo Heather describes; and the free
    /// text Heather says a plain UCCM prints, which nobody here has seen and which stays accepted on
    /// her citation rather than being narrowed by a guess in the other direction.
    /// </remarks>
    [Theory]
    [InlineData("0\r\nCommand complete\r\n", ReceiverMode.PowerUp)]
    [InlineData("1\r\nCommand complete\r\n", ReceiverMode.Locked)]
    [InlineData("LED:GPSL?\r\n1\r\nCOMMAND COMPLETE", ReceiverMode.Locked)]
    [InlineData("LED:GPSL?\r\n0\r\nCOMMAND COMPLETE", ReceiverMode.PowerUp)]
    [InlineData("Normal\r\nCommand complete\r\n", ReceiverMode.Locked)]
    [InlineData("Initializing\r\nCommand complete\r\n", ReceiverMode.PowerUp)]
    [InlineData("Holdover\r\nCommand complete\r\n", ReceiverMode.Holdover)]
    public void EveryLampAnswerTheDriverRecognisesIsAccepted(string reply, ReceiverMode expected)
    {
        UccmDriver driver = Driver();

        SweepInterpretation result = driver.InterpretSweep([reply, null, null]);

        Assert.Null(result.Rejection);
        Assert.Equal(expected, driver.InterpretSyncState(result.Readings.SyncState));
    }

    /// <summary>
    /// Every lamp answer the module gave through the 13 Sep 2026 transitions is accepted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only sitting that saw the lamp in more than one state: <c>0</c> from a cold power-up until
    /// the module had a fix, then <c>1</c>, holdover included. A rule that refused any of these
    /// would put the last good reading on screen in place of a real transition.
    /// </para>
    /// <para>
    /// One probe of the 27 is skipped and counted: at 10:24:28 the previous broadcast's 44 binary
    /// bytes arrived ahead of the answer, and the harness wrote them out as dots. In the application
    /// the transport lifts such a frame out before the driver sees a line (see
    /// <c>BinaryFrameWiringTests</c>), so the text in the file is not what the driver would read.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryLampAnswerInTheTransitionCaptureIsAccepted()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory, "Uccm", "Captures", "transitions-13sep2026.replies.txt");
        Assert.True(File.Exists(path), $"The capture is missing from the test output: {path}");

        List<List<string>> probes = [];
        List<string>? current = null;
        foreach (string line in File.ReadLines(path))
        {
            if (line == $"---- {UccmCommands.LockLed}")
            {
                current = [];
                probes.Add(current);
            }
            else if (current is not null && (line.StartsWith("UCCM-P >", StringComparison.Ordinal) || line.StartsWith("----", StringComparison.Ordinal)))
            {
                current = null;
            }
            else
            {
                current?.Add(line);
            }
        }

        HashSet<string> seen = [];
        int skipped = 0;
        foreach (List<string> reply in probes)
        {
            if (reply.Any(line => line.Contains("....", StringComparison.Ordinal)))
            {
                skipped++;
                continue;
            }

            UccmDriver driver = Driver();
            SweepInterpretation result = driver.InterpretSweep([string.Join("\r\n", reply), null, null]);

            Assert.Null(result.Rejection);
            Assert.NotEqual(ReceiverMode.Disconnected, driver.InterpretSyncState(result.Readings.SyncState));
            seen.Add(result.Readings.SyncState!);
        }

        Assert.Equal(27, probes.Count);
        Assert.Equal(1, skipped);
        Assert.Equal(2, seen.Count);
        Assert.Contains("0", seen);
        Assert.Contains("1", seen);
    }

    /// <summary>
    /// In holdover the marker cannot vouch for a stray line, but it still speaks for a silent lamp.
    /// </summary>
    /// <remarks>
    /// The marked token always contains <c>HOLD</c>, so judging only the token would accept any
    /// stray line whenever the time code says holdover. The frame is the 10:26:56 one from the
    /// 13 Sep 2026 sitting, fourteen minutes into a genuine holdover.
    /// </remarks>
    [Fact]
    public void InHoldoverTheLampIsStillJudgedOnItsOwn()
    {
        UccmDriver driver = Driver();
        driver.Observe([[
            0xC5, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x28, 0x1C, 0x52, 0x00,
            0x00, 0x20, 0x60, 0xC1, 0x91, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x57, 0xD0, 0xB0, 0x62, 0x00, 0x12,
            0x60, 0x0C, 0x4F, 0x90, 0x00, 0x00, 0x00, 0x00, 0xC7, 0x74, 0xCA,
        ]]);

        SweepInterpretation stray = driver.InterpretSweep(["F0 7B A9 00 12 60 04 45 80\r\nCommand complete\r\n"]);
        SweepInterpretation lit = driver.InterpretSweep(["1\r\nCommand complete\r\n"]);
        SweepInterpretation silent = driver.InterpretSweep([null]);

        Assert.NotNull(stray.Rejection);
        Assert.Null(lit.Rejection);
        Assert.Equal(ReceiverMode.Holdover, driver.InterpretSyncState(lit.Readings.SyncState));
        Assert.Null(silent.Rejection);
        Assert.Equal(ReceiverMode.Holdover, driver.InterpretSyncState(silent.Readings.SyncState));
    }

    [Fact]
    public void ASweepWithNoLampAnswerAndNoHoldoverIsRejected()
    {
        // The SmartClock refuses a sweep whose sync state never arrived; so does this family, unless
        // the time code has something to say on its own.
        SweepInterpretation result = Driver().InterpretSweep([null, "-1.23E-9\r\nCommand complete\r\n"]);

        Assert.NotNull(result.Rejection);
        Assert.Contains("(empty)", result.Rejection, StringComparison.Ordinal);
    }

    [Fact]
    public void FieldsThisFamilyDoesNotReportAreAbsentRatherThanZero()
    {
        // A UCCM's fast sweep carries no TFOM, FFOM or satellite count. §11.1: never invent a value
        // to fill a shape.
        SweepInterpretation result = Driver().InterpretSweep(["LED:GPSL?\r\n1\r\n", null, null]);

        Assert.Null(result.Readings.Tfom);
        Assert.Null(result.Readings.Ffom);
        Assert.Null(result.Readings.SatellitesTracked);
        Assert.Null(result.Readings.TimeIntervalNanoseconds);
    }

    // ---- mode mapping -----------------------------------------------------------------------------

    [Theory]
    [InlineData("1", ReceiverMode.Locked)]
    [InlineData("Normal", ReceiverMode.Locked)]
    [InlineData("0", ReceiverMode.PowerUp)]
    [InlineData("Initializing", ReceiverMode.PowerUp)]
    [InlineData("Holdover", ReceiverMode.Holdover)]
    // The lamp still reads 1 - locked - throughout genuine holdover, so the driver marks the token
    // from the time code. Both shapes below are what InterpretSweep can now produce.
    [InlineData("1 HOLDOVER", ReceiverMode.Holdover)]
    [InlineData("HOLDOVER", ReceiverMode.Holdover)]
    public void TheLampMapsOntoTheModesItCanActuallyDistinguish(string token, ReceiverMode expected) =>
        Assert.Equal(expected, Driver().InterpretSyncState(token));

    /// <summary>
    /// The holdover marker beats the lamp, whichever way round the words fall.
    /// </summary>
    /// <remarks>
    /// <b>The order of the tests inside <c>InterpretSyncState</c> is load-bearing, and this is what
    /// says so.</b> A plain UCCM answers the lamp as free text rather than as <c>0</c> or <c>1</c>,
    /// so a marked token can read <c>NORMAL HOLDOVER</c> — and the lamp test used to come first,
    /// which would match <c>NORMAL</c> and report a coasting receiver as locked. That is the whole
    /// defect reintroduced by a line ordering, with no other symptom, so it gets its own test rather
    /// than a comment.
    /// </remarks>
    [Theory]
    [InlineData("NORMAL HOLDOVER")]
    [InlineData("HOLDOVER NORMAL")]
    [InlineData("1 HOLDOVER")]
    public void TheHoldoverMarkerBeatsALampThatStillClaimsNormal(string token) =>
        Assert.Equal(ReceiverMode.Holdover, Driver().InterpretSyncState(token));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("something nobody has seen")]
    public void AnUnrecognisedTokenIsDisconnectedAndNeverAGuess(string? token) =>
        Assert.Equal(ReceiverMode.Disconnected, Driver().InterpretSyncState(token));

    // ---- the never-throw rule ---------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\0\0\0")]
    [InlineData("C5")]
    [InlineData("COMMAND ERROR")]
    [InlineData("PRN EL AZ SS\r\nnot a satellite row\r\nELEV MASK")]
    public void ParseNeverThrowsAndSaysWhatItCouldNotRead(string? response)
    {
        ReceiverStatus status = Driver().Parse(response);

        Assert.NotNull(status);
        Assert.Equal(Start, status.CapturedAt);
    }

    [Fact]
    public void InterpretSweepNeverThrowsOnAnyShapeOfAnswerList()
    {
        UccmDriver driver = Driver();

        Assert.NotNull(driver.InterpretSweep([]));
        Assert.NotNull(driver.InterpretSweep([null, null, null, null, null]));
        Assert.NotNull(driver.InterpretSweep(["\0", "", "   "]));
    }

    // ---- the catalog ------------------------------------------------------------------------------

    [Fact]
    public void TheCatalogIsQueriesOnlyWhichIsWhyNothingNeedsExcluding()
    {
        UccmDriver driver = Driver();

        Assert.All(driver.Commands, command => Assert.True(command.IsQuery));
        Assert.All(driver.Commands, command => Assert.Equal(SafetyTier.Safe, command.Tier));
        Assert.False(driver.IsBlocked("SYST:STAT"));
    }

    [Fact]
    public void EveryCommandThePlanNamesResolvesThroughFind()
    {
        // The interface requires it, and an index that drifted from the list would poll a command
        // the receiver has never been offered.
        UccmDriver driver = Driver();

        foreach (string mnemonic in driver.Plan.FastTier)
        {
            Assert.NotNull(driver.Find(mnemonic));
        }

        Assert.NotNull(driver.Find(driver.Plan.FullStatus));
    }

    [Fact]
    public void TheDiscriminatorIsFirstInTheFastTier() =>
        Assert.Equal(UccmCommands.LockLed, Driver().Plan.FastTier[0]);
}
