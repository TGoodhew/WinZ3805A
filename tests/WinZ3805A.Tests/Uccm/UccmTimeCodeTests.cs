using System.Globalization;
using WinZ3805A.Device.Drivers.Uccm;

namespace WinZ3805A.Tests.Uccm;

/// <summary>
/// The <c>C5</c> time-code line (#416).
/// </summary>
/// <remarks>
/// <b>These fixtures are synthesised, not captured.</b> They encode what Lady Heather's comments
/// say the bytes mean, so they check that this driver reads the reference's claims correctly — they
/// cannot check that the claims are true. When a UCCM is on the bench, a capture replaces them and
/// whatever disagrees is a finding rather than a test failure to be edited away.
/// </remarks>
public sealed class UccmTimeCodeTests
{
    /// <summary>Builds a time-code line from the values that matter, zero elsewhere.</summary>
    private static string Line(
        long gpsSeconds = 0,
        int leap = 18,
        int pps = 0x60,
        int antenna = 0x04,
        int lockState = 0x85,
        int dateValidity = 0x40)
    {
        int[] values = new int[44];
        values[0] = 0xC5;
        values[27] = (int)((gpsSeconds >> 24) & 0xFF);
        values[28] = (int)((gpsSeconds >> 16) & 0xFF);
        values[29] = (int)((gpsSeconds >> 8) & 0xFF);
        values[30] = (int)(gpsSeconds & 0xFF);
        values[32] = leap;
        values[33] = pps;
        values[34] = antenna;
        values[35] = lockState;
        values[36] = dateValidity;

        return string.Join(' ', values.Select(v => v.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void TheGpsSecondCounterIsReadMostSignificantByteFirst()
    {
        // 6 Jan 1980 plus one day.
        UccmTimeCode? code = UccmTimeCode.TryParse(Line(gpsSeconds: 86_400, leap: 0));

        Assert.NotNull(code);
        Assert.Equal(86_400, code.GpsSeconds);
        Assert.Equal(new DateTimeOffset(1980, 1, 7, 0, 0, 0, TimeSpan.Zero), code.GpsTime);
    }

    [Fact]
    public void UtcIsGpsTimeLessTheLeapOffset()
    {
        UccmTimeCode? code = UccmTimeCode.TryParse(Line(gpsSeconds: 86_400, leap: 18));

        Assert.NotNull(code);
        Assert.Equal(18, code.LeapSecondOffset);
        Assert.Equal(new DateTimeOffset(1980, 1, 6, 23, 59, 42, TimeSpan.Zero), code.UtcTime);
    }

    [Fact]
    public void AZeroLeapOffsetIsAbsentRatherThanZeroSoUtcCannotBeStated()
    {
        // Heather refuses to adopt a zero offset, and the offset has not been zero since 1980.
        // Reporting UTC from it would be a fabricated value, which §11.1 forbids.
        UccmTimeCode? code = UccmTimeCode.TryParse(Line(gpsSeconds: 86_400, leap: 0));

        Assert.NotNull(code);
        Assert.Null(code.LeapSecondOffset);
        Assert.Null(code.UtcTime);
    }

    [Theory]
    [InlineData(0x04, true)]
    [InlineData(0x06, true)]
    [InlineData(0x0C, false)]
    [InlineData(0x00, null)]
    public void TheAntennaByteDecodesToHealthOrToNothing(int antenna, bool? expected)
    {
        UccmTimeCode? code = UccmTimeCode.TryParse(Line(antenna: antenna));

        Assert.NotNull(code);
        Assert.Equal(expected, code.AntennaOk);
    }

    [Theory]
    [InlineData(0x60, false)]
    [InlineData(0x62, true)]
    [InlineData(0x41, false)]
    [InlineData(0x43, true)]
    public void LeapPendingIsOneBitOfThePpsByteIncludingWhereThatIsProbablyWrong(int pps, bool expected)
    {
        // 0x43 appears in Heather's own recorded power-up sequence, where a leap second is not
        // plausibly pending - so this reads true spuriously while warming up. Asserted as it is,
        // rather than papered over, because the alternative is inventing a rule no observation
        // supports. The hardware sitting settles it.
        UccmTimeCode? code = UccmTimeCode.TryParse(Line(pps: pps));

        Assert.NotNull(code);
        Assert.Equal(expected, code.LeapPending);
    }

    [Theory]
    [InlineData(0x85, 0x40, UccmVendor.Symmetricom)]
    [InlineData(0x8F, 0x50, UccmVendor.Symmetricom)]
    [InlineData(0x45, 0x80, UccmVendor.Trimble)]
    [InlineData(0x4F, 0x90, UccmVendor.Trimble)]
    [InlineData(0x41, 0x90, UccmVendor.Trimble)]
    public void TheLockAndDateBytesTogetherSuggestAVendor(int lockState, int date, UccmVendor expected)
    {
        UccmTimeCode? code = UccmTimeCode.TryParse(Line(lockState: lockState, dateValidity: date));

        Assert.NotNull(code);
        Assert.Equal(expected, code.SuggestedVendor);
    }

    [Fact]
    public void TheTwoBytesDisagreeingSuggestsNothingRatherThanPickingOne()
    {
        // The high nibbles run OPPOSITE ways on the two bytes - Symmetricom is 8 on the lock byte
        // and 4 on the date byte, Trimble the reverse - so "high nibble means vendor" is not a rule
        // that generalises. A disagreement is the case that would silently invert the answer.
        UccmTimeCode? code = UccmTimeCode.TryParse(Line(lockState: 0x85, dateValidity: 0x80));

        Assert.NotNull(code);
        Assert.Equal(UccmVendor.Unknown, code.SuggestedVendor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SYNC:TINT? ")]
    [InlineData("C5 00 01")]
    [InlineData("C5 ZZ 01 02")]
    public void AnythingThatIsNotAWholeTimeCodeIsNoReadingAtAll(string? line)
    {
        // A time built from a partly-read counter is wrong rather than absent, and wrong is the one
        // thing a clock display must not be.
        Assert.Null(UccmTimeCode.TryParse(line));
    }

    [Fact]
    public void TheLineIsFoundWhenItIsEmbeddedInSomethingElse()
    {
        // Heather has a dedicated path for a time code arriving in the middle of another reply.
        Assert.NotNull(UccmTimeCode.TryParse("SOME PREFIX " + Line(gpsSeconds: 1_000)));
    }

    [Fact]
    public void EveryValueSurvivesTheParseIncludingTheOnesNobodyHasDecoded()
    {
        // Two thirds of these have no known meaning. A field report about odd firmware is only
        // actionable if the raw line survived.
        UccmTimeCode? code = UccmTimeCode.TryParse(Line());

        Assert.NotNull(code);
        Assert.Equal(44, code.Values.Count);
        Assert.Equal(0xC5, code.Values[0]);
    }

    // ---- #481: the shape the hardware actually sends ---------------------------------------------

    /// <summary>
    /// A real frame, byte for byte, from the 12 Sep 2026 sitting.
    /// </summary>
    /// <remarks>
    /// <b>The first captured fixture in this file.</b> Everything above it is synthesised from Lady
    /// Heather's comments and can only check that this driver reads her claims correctly. This one
    /// came off COM3 from <c>TRIMBLE,57964-80,40896646,V2.0.1.6-01</c> and is in the repository at
    /// <c>tests/WinZ3805A.Tests/Uccm/Captures/bench-12sep2026.txt</c>, trailing the <c>*IDN?</c>
    /// reply. The class remarks say a capture replaces the synthesised fixtures and that whatever
    /// disagrees is a finding: nothing disagreed.
    /// </remarks>
    private static ReadOnlySpan<byte> CapturedFrame =>
    [
        0xC5, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x28, 0x1C, 0x52, 0x00,
        0x00, 0x20, 0x60, 0xC1, 0x91, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x57, 0xCF, 0x27, 0xA4, 0x00, 0x12,
        0x60, 0x04, 0x45, 0x80, 0x00, 0x00, 0x00, 0x00, 0x10, 0xAA, 0xCA,
    ];

    /// <summary>
    /// The captured frame parses, and its clock is the one the receiver was keeping.
    /// </summary>
    /// <remarks>
    /// <b>Heather's indices 27 to 30 are hereby confirmed against hardware.</b> The frame reads
    /// <c>0x57CF27A4</c> — 1 473 193 892 seconds from the GPS epoch, which is 2026-09-11 20:31:32.
    /// The capture was written within twenty seconds of that, out of 1.47 billion, and nothing but
    /// a correct epoch and a correct byte order lands that close.
    /// </remarks>
    [Fact]
    public void TheCapturedFrameCarriesTheReceiversOwnGpsClock()
    {
        UccmTimeCode? code = UccmTimeCode.TryParse(CapturedFrame);

        Assert.NotNull(code);
        Assert.Equal(0x57CF27A4, code.GpsSeconds);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 11, 20, 31, 32, TimeSpan.Zero),
            code.GpsTime);
    }

    /// <summary>
    /// The leap-second field reads 18, which is what GPS − UTC actually was.
    /// </summary>
    /// <remarks>
    /// <b>Heather's index 32 is confirmed too, and this one settles a question that had been open.</b>
    /// GPS − UTC has stood at 18 s since 2017, and this receiver answers none of the
    /// <c>:PTIM:LEAP</c> queries, so the offset had no other source on this family. The 12 Sep
    /// session had measured the receiver's clock at +16 s against the host — but <c>W32Time</c> was
    /// stopped on that machine, so the host was not a reference and the session recorded the offset
    /// as unestablished. The receiver states it here itself, and 18 is the right answer.
    /// </remarks>
    [Fact]
    public void TheCapturedFrameReportsTheAccumulatedLeapSeconds()
    {
        UccmTimeCode? code = UccmTimeCode.TryParse(CapturedFrame);

        Assert.NotNull(code);
        Assert.Equal(18, code.LeapSecondOffset);
        Assert.Equal(code.GpsTime.AddSeconds(-18), code.UtcTime);
    }

    /// <summary>
    /// The byte path and the hex-text path read a frame identically.
    /// </summary>
    /// <remarks>
    /// Two entry points onto one field mapping, which is why they share <c>FromValues</c>. Heather
    /// renders codes as hex text in her own logs and the hardware sends bytes; a reading that
    /// depended on which door it came through would be a defect nobody would look for.
    /// </remarks>
    [Fact]
    public void TheByteAndTextPathsAgreeOnTheSameFrame()
    {
        string asText = string.Join(' ', CapturedFrame.ToArray().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

        UccmTimeCode? fromBytes = UccmTimeCode.TryParse(CapturedFrame);
        UccmTimeCode? fromText = UccmTimeCode.TryParse(asText);

        Assert.NotNull(fromBytes);
        Assert.NotNull(fromText);
        Assert.Equal(fromText.GpsSeconds, fromBytes.GpsSeconds);
        Assert.Equal(fromText.LeapSecondOffset, fromBytes.LeapSecondOffset);
        Assert.Equal(fromText.Values, fromBytes.Values);
    }

    /// <summary>
    /// Anything that is not a whole, bounded frame is no reading at all.
    /// </summary>
    /// <remarks>
    /// §11.1: never a throw, and never half a reading. A time assembled from a partly-arrived
    /// counter is wrong rather than absent, and wrong is the one thing a clock must not be.
    /// </remarks>
    [Fact]
    public void AFrameOfTheWrongShapeIsNotAReading()
    {
        Assert.Null(UccmTimeCode.TryParse(CapturedFrame[..43]));
        Assert.Null(UccmTimeCode.TryParse(ReadOnlySpan<byte>.Empty));

        byte[] wrongTerminator = CapturedFrame.ToArray();
        wrongTerminator[^1] = 0x00;
        Assert.Null(UccmTimeCode.TryParse(wrongTerminator));

        byte[] wrongMarker = CapturedFrame.ToArray();
        wrongMarker[0] = 0x00;
        Assert.Null(UccmTimeCode.TryParse(wrongMarker));
    }
}
