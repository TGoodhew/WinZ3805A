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
