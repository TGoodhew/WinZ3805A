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
}
