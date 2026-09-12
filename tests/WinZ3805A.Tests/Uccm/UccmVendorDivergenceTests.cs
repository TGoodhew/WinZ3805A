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
/// <para>
/// <b>Half of it has now met hardware and half has not, and the two are kept apart on purpose.</b>
/// The Trimble fixtures are the shape a real <c>TRIMBLE,57964-80,40896646,V2.0.1.6-01</c> sends,
/// <see cref="MeasuredTrimbleLoop"/> byte for byte from the 12 Sep 2026 capture. <b>Every
/// Symmetricom fixture here is still synthesised from Lady Heather's comments</b> — no Symmetricom
/// module has been on the bench, so those tests pin our reading of a description, not of a receiver.
/// </para>
/// </remarks>
public sealed class UccmVendorDivergenceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static UccmDriver Driver() => new(new FakeTimeProvider(Start));

    // ---- 1. DIAG:LOOP? answers in two entirely different shapes ---------------------------------

    /// <summary>
    /// A Trimble loop reply in the shape the bench module actually sends.
    /// </summary>
    /// <remarks>
    /// <b>The <c>LINK0:</c> line is not decoration.</b> Every reply measured from
    /// <c>TRIMBLE,57964-80,40896646,V2.0.1.6-01</c> begins with one, and nothing in Heather's
    /// description mentions it. It is well-formed text carrying a plausible float, so a parser that
    /// treats the first payload line as the header, or counts lines from the top, reads the reply
    /// wrong and does so silently. This fixture carried the header first until 13 Sep 2026, which
    /// meant the one line most able to break the parse was the one never exercised.
    /// </remarks>
    private const string TrimbleLoop = """
        DIAG:LOOP?
        LINK0: [-1.00E+00]
          DAC_LINK   DAC_AVG   DAC_GPS  FREQ_CORR  LINK_OFF  FREQ_DIFF  FREQ_FRAC
          32768      32770     32768    1.5E-11    0.0       4.2E-11    0.25
        COMMAND COMPLETE
        """;

    /// <summary>The 12 Sep 2026 bench reply, byte for byte from the capture.</summary>
    private const string MeasuredTrimbleLoop = """
        LINK0: [-1.00E+00]
        DAC_LINK    DAC_AVG    DAC_GPS   FREQ_CORR  LINK_OFF   FREQ_DIFF  FREQ_FRAC
        +5.91E-08  +5.90E-08  +5.91E-08  +0.00E+00  -6.47E-12  -6.47E-12  -2.57E-12
        Command complete
        UCCM-P >
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

    [Theory]
    [InlineData("2.00E-7", true)]
    [InlineData("-2.00E-7", true)]
    [InlineData("2.01E-7", false)]
    [InlineData("-2.01E-7", false)]
    public void TheBandIncludesItsOwnEdge(string value, bool kept)
    {
        // Heather's test is `< -2.00E-7` and `> 2.00E-7`, so the limit itself is IN. Nothing pinned
        // that until now: changing `>` to `>=` would move the boundary and every other test here
        // would still pass.
        UccmLoopReading reading = UccmLoopReading.Parse(
            TrimbleLoop.Replace("4.2E-11", value, StringComparison.Ordinal));

        Assert.Equal(kept, reading.FrequencyDifference is not null);
        Assert.Equal(!kept, reading.FrequencyDifferenceRejected);
    }

    // ---- 3a. what the bench module actually sends ------------------------------------------------

    [Fact]
    public void TheMeasuredBenchReplyIsReadCorrectlyEndToEnd()
    {
        // The 12 Sep 2026 capture, byte for byte. Synthesised fixtures agree with whatever they
        // were written from; this one agrees with a receiver.
        UccmLoopReading reading = UccmLoopReading.Parse(MeasuredTrimbleLoop);

        Assert.Equal(UccmVendor.Trimble, reading.Vendor);
        Assert.Equal(-6.47E-12, reading.FrequencyDifference!.Value, 15);
        Assert.Equal(-0.00647, reading.OscillatorOffsetPpb!.Value, 6);
        Assert.False(reading.FrequencyDifferenceRejected);
        Assert.Null(reading.TemperatureCorrection);
    }

    [Fact]
    public void TheLinkLineBeforeTheHeaderIsNotReadAsData()
    {
        // It is well-formed text carrying a plausible float, sitting exactly where a naive parser
        // looks for the header. Reading it would be silent.
        UccmLoopReading reading = UccmLoopReading.Parse(MeasuredTrimbleLoop);

        Assert.Equal(-6.47E-12, reading.FrequencyDifference!.Value, 15);
        Assert.NotEqual(-1.0, reading.FrequencyDifference!.Value);
    }

    // ---- 3b. a zero frequency correction tells us nothing ----------------------------------------

    [Fact]
    public void AZeroFrequencyCorrectionIsUnknownDiscipliningRatherThanFalse()
    {
        // MEASURED, and the reason this changed. The bench Trimble reports FREQ_CORR as +0.00E+00
        // in every sitting while locked - LED:GPSL? answering 1 and the lock byte reading 0x45 - so
        // reading zero as "not disciplining" called a disciplined receiver undisciplined every time.
        // Heather hedges the same field, `// always 0.0?`, and never derives a boolean from it.
        UccmLoopReading measured = UccmLoopReading.Parse(MeasuredTrimbleLoop);

        Assert.Equal(0.0, measured.FrequencyCorrection!.Value);
        Assert.Null(measured.Disciplining);
    }

    [Fact]
    public void ANonZeroFrequencyCorrectionStillImpliesDisciplining()
    {
        UccmLoopReading reading = UccmLoopReading.Parse(TrimbleLoop);

        Assert.Equal(1.5E-11, reading.FrequencyCorrection!.Value, 15);
        Assert.True(reading.Disciplining);
    }

    [Fact]
    public void SymmetricomNeverClaimsToKnowWhetherItIsDisciplining() =>
        Assert.Null(UccmLoopReading.Parse(SymmetricomLoop).Disciplining);

    // ---- 3c. a status screen is not a loop reply -------------------------------------------------

    [Fact]
    public void AStatusScreenArrivingHereIsRefusedRatherThanPartlyRead()
    {
        // Heather makes the same check and abandons its loop parse on it. #481 has already shown
        // this family delivering replies with foreign bytes in front of them, so "the reply I got
        // is not the one I asked for" is a real state, and §11.1 says that is no reading at all.
        UccmLoopReading reading = UccmLoopReading.Parse(
            MeasuredTrimbleLoop + "\nSERIAL NUMBER  40896646\n");

        Assert.Equal(UccmVendor.Unknown, reading.Vendor);
        Assert.Null(reading.FrequencyDifference);
        Assert.Null(reading.FrequencyCorrection);
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
