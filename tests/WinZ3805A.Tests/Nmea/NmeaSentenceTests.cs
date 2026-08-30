using WinZ3805A.Device.Drivers.Nmea;

namespace WinZ3805A.Tests.Nmea;

/// <summary>
/// The sentence codec the driver reads with and the simulator writes with (#310).
/// </summary>
/// <remarks>
/// The checksum is checked against two sentences published outside this repository — the
/// standard's own GLL example and the GGA example every NMEA reference quotes — because a codec
/// that computes and checks the same wrong checksum agrees with itself perfectly.
/// </remarks>
public sealed class NmeaSentenceTests
{
    /// <summary>The GLL example quoted in every NMEA reference, checksum <c>31</c>.</summary>
    private const string StandardGll = "$GPGLL,4916.45,N,12311.12,W,225444,A*31";

    /// <summary>The GGA example every reference quotes, checksum <c>47</c>.</summary>
    private const string ReferenceGga = "$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47";

    [Theory]
    [InlineData(StandardGll)]
    [InlineData(ReferenceGga)]
    public void PublishedChecksumsAreAccepted(string sentence)
    {
        NmeaSentence parsed = Assert.IsType<NmeaSentence>(NmeaSentence.TryParse(sentence));

        Assert.True(parsed.HasChecksum);
        Assert.True(parsed.ChecksumValid);
    }

    [Fact]
    public void TheReferenceSentenceIsFormattedByteForByte() =>
        Assert.Equal(
            ReferenceGga,
            NmeaSentence.Format("GP", "GGA", "123519", "4807.038", "N", "01131.000", "E", "1", "08", "0.9", "545.4", "M", "46.9", "M", "", ""));

    [Fact]
    public void TheAddressIsSplitIntoTalkerAndIdentifier()
    {
        NmeaSentence parsed = Assert.IsType<NmeaSentence>(NmeaSentence.TryParse(ReferenceGga));

        Assert.Equal("GP", parsed.Talker);
        Assert.Equal("GGA", parsed.Identifier);
        Assert.Equal("$--GGA", parsed.Key);
        Assert.Equal(14, parsed.Fields.Count);
        Assert.Equal("123519", parsed.Field(0));
        Assert.Equal("08", parsed.Field(6));
    }

    /// <summary>A blank field is "no data", which the standard sends as nothing between two commas.</summary>
    [Fact]
    public void ABlankFieldReadsAsAbsent()
    {
        NmeaSentence parsed = Assert.IsType<NmeaSentence>(NmeaSentence.TryParse(ReferenceGga));

        Assert.Null(parsed.Field(12));
        Assert.Null(parsed.Field(13));
        Assert.Null(parsed.Field(99));
        Assert.Null(parsed.Field(-1));
    }

    [Fact]
    public void AWrongChecksumIsReportedNotHidden()
    {
        NmeaSentence parsed = Assert.IsType<NmeaSentence>(NmeaSentence.TryParse("$GPGLL,4916.45,N,12311.12,W,225444,A*32"));

        Assert.True(parsed.HasChecksum);
        Assert.False(parsed.ChecksumValid);
        Assert.Equal("GLL", parsed.Identifier);
    }

    [Fact]
    public void ASentenceWithoutAChecksumParsesButIsNotEvidence()
    {
        NmeaSentence parsed = Assert.IsType<NmeaSentence>(NmeaSentence.TryParse("$HCHDM,238,M"));

        Assert.False(parsed.HasChecksum);
        Assert.False(parsed.ChecksumValid);
        Assert.Equal("HC", parsed.Talker);
        Assert.Equal("HDM", parsed.Identifier);
    }

    [Fact]
    public void AProprietarySentenceHasATalkerOfP()
    {
        NmeaSentence parsed = Assert.IsType<NmeaSentence>(NmeaSentence.TryParse("$PUBX,00,081350.00,4717.113210,N,00833.915187,E,546.589,G3,2.1,2.0,0.007,77.52,0.007,,0.92,1.19,0.77,9,0,0*5F"));

        Assert.Equal("P", parsed.Talker);
        Assert.Equal("UBX", parsed.Identifier);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("GPGGA,123519*47")]
    [InlineData("$")]
    [InlineData("$GP")]
    [InlineData("$gpgga,1,2*00")]
    [InlineData("$GPGGA,1*2")]
    [InlineData("$GPGGA,1*2*3")]
    [InlineData("scpi > ")]
    [InlineData("SYMMETRICOM,Z3805A,3625A02931,1.01.03-A")]
    public void WhatIsNotASentenceIsNull(string? line) => Assert.Null(NmeaSentence.TryParse(line));

    [Fact]
    public void FormatAndParseRoundTrip()
    {
        string sentence = NmeaSentence.Format("GN", "ZDA", "120000.00", "29", "08", "2026", "00", "00");
        NmeaSentence parsed = Assert.IsType<NmeaSentence>(NmeaSentence.TryParse(sentence));

        Assert.True(parsed.ChecksumValid);
        Assert.Equal("GN", parsed.Talker);
        Assert.Equal(["120000.00", "29", "08", "2026", "00", "00"], parsed.Fields);
    }

    [Fact]
    public void ANullFieldIsSentAsBlank() =>
        Assert.StartsWith("$GPRMC,120000.00,V,,,,*", NmeaSentence.Format("GP", "RMC", "120000.00", "V", null, null, "", ""), StringComparison.Ordinal);

    /// <summary>The line the wire carries is trimmed of its ending before it is read; a stray CR must not spoil the checksum.</summary>
    [Fact]
    public void ATrailingLineEndingIsTolerated()
    {
        NmeaSentence parsed = Assert.IsType<NmeaSentence>(NmeaSentence.TryParse(StandardGll + "\r\n"));

        Assert.True(parsed.ChecksumValid);
        Assert.Equal(StandardGll, parsed.Raw);
    }
}
