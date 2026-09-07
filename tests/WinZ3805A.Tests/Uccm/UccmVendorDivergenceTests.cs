using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers.Uccm;
using WinZ3805A.Device.Models;

namespace WinZ3805A.Tests.Uccm;

/// <summary>
/// The disagreements between Symmetricom and Trimble UCCMs (#418).
/// </summary>
/// <remarks>
/// This is the file that exists because "the same command answers differently depending on who made
/// the module" is the whole risk of this family. Each test names the wrong reading it prevents.
/// Fixtures are synthesised from Lady Heather's comments and have not met hardware.
/// </remarks>
public sealed class UccmVendorDivergenceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static UccmDriver Driver() => new(new FakeTimeProvider(Start));

    // ---- 1. DIAG:LOOP? answers in two entirely different shapes ---------------------------------

    private const string TrimbleLoop = """
        DIAG:LOOP?
          DAC_LINK   DAC_AVG   DAC_GPS  FREQ_CORR  LINK_OFF  FREQ_DIFF  FREQ_FRAC
          32768      32770     32768    1.5E-11    0.0       4.2E-11    0.25
        COMMAND COMPLETE
        """;

    private const string SymmetricomLoop = """
        DIAG:LOOP?
        ------------------------------
        FREQ COR = 3.1E-11
        TEMP COR = 42.5
        COMMAND COMPLETE
        """;

    [Fact]
    public void TrimblesLoopIsSevenPositionalFloatsAndSymmetricomsIsLabelledLines()
    {
        UccmLoopReading trimble = UccmLoopReading.Parse(TrimbleLoop);
        UccmLoopReading symmetricom = UccmLoopReading.Parse(SymmetricomLoop);

        Assert.Equal(UccmVendor.Trimble, trimble.Vendor);
        Assert.Equal(4.2E-11, trimble.FrequencyDifference!.Value, 15);

        Assert.Equal(UccmVendor.Symmetricom, symmetricom.Vendor);
        Assert.Equal(3.1E-11, symmetricom.FrequencyDifference!.Value, 15);
    }

    [Fact]
    public void ReadingTheLoopIsWhatSettlesTheVendor()
    {
        UccmDriver driver = Driver();
        Assert.False(driver.Profile.VendorKnown);

        driver.ReadLoop(TrimbleLoop);

        Assert.Equal(UccmVendor.Trimble, driver.Profile.Vendor);
    }

    // ---- 2. only Symmetricom reports temperature ------------------------------------------------

    [Fact]
    public void TemperatureIsAbsentOnTrimbleRatherThanZero()
    {
        // §11.1: an absent field is absent. A zero here would render as a real 0 °C correction on a
        // module that has no temperature sensor reading to give.
        Assert.Null(UccmLoopReading.Parse(TrimbleLoop).TemperatureCorrection);
        Assert.Equal(42.5, UccmLoopReading.Parse(SymmetricomLoop).TemperatureCorrection!.Value, 6);
    }

    // ---- 3. the oscillator sanity band ----------------------------------------------------------

    [Fact]
    public void TheBogusValueHeatherSawOnTrimbleIsDiscardedAndSaidToHaveBeen()
    {
        // Heather's comment: a bogus -2.79E-7 "shows up occasionally on Trimble". Without the band
        // it reaches the trend store and §9.10.2's chart draws a full-height excursion for an event
        // that never happened.
        UccmLoopReading reading = UccmLoopReading.Parse(
            TrimbleLoop.Replace("4.2E-11", "-2.79E-7", StringComparison.Ordinal));

        Assert.Null(reading.FrequencyDifference);
        Assert.True(reading.FrequencyDifferenceRejected);
    }

    [Fact]
    public void AValueInsideTheBandIsKeptAndNotReportedAsRejected()
    {
        UccmLoopReading reading = UccmLoopReading.Parse(
            TrimbleLoop.Replace("4.2E-11", "1.5E-7", StringComparison.Ordinal));

        Assert.Equal(1.5E-7, reading.FrequencyDifference!.Value, 12);
        Assert.False(reading.FrequencyDifferenceRejected);
    }

    // ---- 4. the lock byte means different things -------------------------------------------------

    private static string StatusWithLock(int lockState, int date) =>
        string.Join(
            ' ',
            Enumerable.Range(0, 44).Select(i => i switch
            {
                0 => 0xC5,
                32 => 18,
                33 => 0x60,
                34 => 0x04,
                35 => lockState,
                36 => date,
                _ => 0,
            }).Select(v => v.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData(UccmVendor.Trimble, 0x45, 0x80, SmartClockMode.Locked)]
    [InlineData(UccmVendor.Trimble, 0x4F, 0x90, SmartClockMode.Recovery)]
    [InlineData(UccmVendor.Trimble, 0x41, 0x90, SmartClockMode.PowerUp)]
    [InlineData(UccmVendor.Symmetricom, 0x85, 0x40, SmartClockMode.Locked)]
    [InlineData(UccmVendor.Symmetricom, 0x8F, 0x50, SmartClockMode.Recovery)]
    public void EachVendorsOwnLockCodesAreReadCorrectly(
        UccmVendor vendor, int lockState, int date, SmartClockMode expected)
    {
        ReceiverStatus status = UccmStatusParser.Parse(
            StatusWithLock(lockState, date), Start, new UccmProfile(vendor, UccmVariant.Unknown));

        Assert.Equal(expected, status.Mode);
    }

    [Fact]
    public void ALockedTrimbleIsNotReportedAsUnlockedWhileTheVendorIsStillUnknown()
    {
        // THE FAILURE THIS FILE EXISTS FOR. A driver knowing only Symmetricom's 85 would call a
        // locked Trimble unlocked, and the medallion would render a fault on a receiver that is
        // working perfectly. Before the vendor is settled, the low nibble - common to both in every
        // value Heather records - carries the state.
        ReceiverStatus status = UccmStatusParser.Parse(
            StatusWithLock(0x45, 0x80), Start, UccmProfile.Unknown);

        Assert.Equal(SmartClockMode.Locked, status.Mode);
    }

    [Fact]
    public void AnUnknownLockByteIsUnknownAndNotAGuessAtLocked()
    {
        ReceiverStatus status = UccmStatusParser.Parse(
            StatusWithLock(0x33, 0x00), Start, UccmProfile.Unknown);

        Assert.Equal(SmartClockMode.Unknown, status.Mode);
    }

    // ---- 5. variant and vendor are two dimensions -------------------------------------------------

    [Fact]
    public void TheVariantIsRecordedSeparatelyFromTheVendor()
    {
        // Heather assigns UCCMP_TYPE on seeing the SYMMETRICOM loop header, inferring a variant
        // from a vendor signature. Its own comments describe both a Symmetricom UCCM-P and a
        // Trimble UCCM-P, so that inference cannot be right for all four combinations.
        UccmDriver driver = Driver();
        driver.ReadLoop(SymmetricomLoop);

        Assert.Equal(UccmVendor.Symmetricom, driver.Profile.Vendor);
        Assert.Equal(UccmVariant.Unknown, driver.Profile.Variant);

        driver.NoteVariant(UccmVariant.UccmP);

        Assert.Equal(UccmVendor.Symmetricom, driver.Profile.Vendor);
        Assert.Equal(UccmVariant.UccmP, driver.Profile.Variant);
    }

    [Fact]
    public void AVendorThatIsNotYetKnownIsSaidToBeUnknownRatherThanAssumed()
    {
        ReceiverStatus status = UccmStatusParser.Parse(
            StatusWithLock(0x33, 0x11), Start, UccmProfile.Unknown);

        Assert.Contains(
            status.ParseWarnings,
            warning => warning.Contains("Vendor is not established", StringComparison.Ordinal));
    }
}
