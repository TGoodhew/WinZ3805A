using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers.Nmea;
using WinZ3805A.Simulation;

namespace WinZ3805A.Tests.Nmea;

/// <summary>
/// The tutorial's receiver on the bench, held to what a real talker does (#310).
/// </summary>
public sealed class NmeaTalkerSimulatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private static (FakeTimeProvider Clock, NmeaTalkerSimulator Talker) Cold() =>
        (new FakeTimeProvider(Start), new NmeaTalkerSimulator(new FakeTimeProvider(Start)));

    [Fact]
    public void EverySentenceCarriesAValidChecksumAndFitsTheStandard()
    {
        (_, NmeaTalkerSimulator talker) = Cold();

        foreach (string line in talker.NextCycle())
        {
            NmeaSentence sentence = Assert.IsType<NmeaSentence>(NmeaSentence.TryParse(line));
            Assert.True(sentence.ChecksumValid, line);
            Assert.True(line.Length <= NmeaSentence.MaximumLength, $"{line.Length} chars: {line}");
            Assert.Equal("GP", sentence.Talker);
        }
    }

    /// <summary>The order a u-blox sends: RMC first, then GGA, GSA, the GSV pages, ZDA.</summary>
    [Fact]
    public void ACycleIsInTheOrderARealModuleSendsIt()
    {
        (_, NmeaTalkerSimulator talker) = Cold();

        List<string> identifiers = talker.NextCycle().Select(line => NmeaSentence.TryParse(line)!.Identifier).ToList();

        Assert.Equal("RMC", identifiers[0]);
        Assert.Equal("GGA", identifiers[1]);
        Assert.Equal("GSA", identifiers[2]);
        Assert.Equal("ZDA", identifiers[^1]);
        Assert.Equal(3, identifiers.Count(id => id == "GSV"));
    }

    [Fact]
    public void ItStartsColdAndFindsAFixOnSchedule()
    {
        FakeTimeProvider clock = new(Start);
        NmeaTalkerSimulator talker = new(clock, fixAfter: TimeSpan.FromSeconds(20), threeDimensionalAfter: TimeSpan.FromSeconds(40));

        Assert.Equal(FixPhase.NoFix, talker.Phase);
        AssertFix(talker, status: "V", quality: "0", gsaMode: "1", used: 0);

        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(FixPhase.TwoDimensional, talker.Phase);
        AssertFix(talker, status: "A", quality: "1", gsaMode: "2", used: 3);

        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(FixPhase.ThreeDimensional, talker.Phase);
        AssertFix(talker, status: "A", quality: "1", gsaMode: "3", used: talker.SatellitesTracked);
    }

    [Fact]
    public void AltitudeArrivesWithTheThreeDimensionalFix()
    {
        FakeTimeProvider clock = new(Start);
        NmeaTalkerSimulator talker = new(clock, heightMetres: 56.0);

        Assert.Null(Gga(talker).Field(8));

        clock.Advance(TimeSpan.FromSeconds(40));
        Assert.Equal("56.0", Gga(talker).Field(8));
        Assert.Equal("M", Gga(talker).Field(9));
    }

    /// <summary>Ten satellites in view is three GSV pages, each saying so.</summary>
    [Fact]
    public void GsvIsPagedFourToAPage()
    {
        (_, NmeaTalkerSimulator talker) = Cold();

        List<NmeaSentence> pages = talker.NextCycle()
            .Select(line => NmeaSentence.TryParse(line)!)
            .Where(sentence => sentence.Identifier == "GSV")
            .ToList();

        Assert.Equal(3, pages.Count);
        Assert.All(pages, page => Assert.Equal("3", page.Field(0)));
        Assert.Equal(["1", "2", "3"], pages.Select(page => page.Field(1)));
        Assert.All(pages, page => Assert.Equal("10", page.Field(2)));
        Assert.Equal(4 * 4 + 3, pages[0].Fields.Count);
        Assert.Equal(2 * 4 + 3, pages[2].Fields.Count);
    }

    /// <summary>Before the fix the strong satellites are heard and the weak ones are not; two never are.</summary>
    [Fact]
    public void TrackingGrowsWithTheFix()
    {
        FakeTimeProvider clock = new(Start);
        NmeaTalkerSimulator talker = new(clock);

        Assert.Equal(6, talker.SatellitesTracked);
        clock.Advance(TimeSpan.FromSeconds(40));
        Assert.Equal(8, talker.SatellitesTracked);
    }

    [Fact]
    public void TimeComesFromTheClock()
    {
        FakeTimeProvider clock = new(Start);
        NmeaTalkerSimulator talker = new(clock);

        Assert.Equal("120000.00", Rmc(talker).Field(0));
        Assert.Equal("290826", Rmc(talker).Field(8));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("120001.00", Rmc(talker).Field(0));
    }

    [Fact]
    public void TwoTalkersFromTheSameStartSayTheSameThing()
    {
        FakeTimeProvider a = new(Start);
        FakeTimeProvider b = new(Start);

        Assert.Equal(new NmeaTalkerSimulator(a).NextCycle(), new NmeaTalkerSimulator(b).NextCycle());
    }

    [Theory]
    [InlineData(47.6205, "4737.2300", "N")]
    [InlineData(-33.8688, "3352.1280", "S")]
    public void LatitudeIsWrittenAsDegreesAndMinutes(double degrees, string value, string hemisphere) =>
        Assert.Equal((value, hemisphere), NmeaTalkerSimulator.Latitude(degrees));

    [Theory]
    [InlineData(-122.3493, "12220.9580", "W")]
    [InlineData(151.2093, "15112.5580", "E")]
    public void LongitudeIsWrittenAsDegreesAndMinutes(double degrees, string value, string hemisphere) =>
        Assert.Equal((value, hemisphere), NmeaTalkerSimulator.Longitude(degrees));

    [Fact]
    public void TheWireTextEndsEverySentenceWithCrLf()
    {
        (_, NmeaTalkerSimulator talker) = Cold();

        string text = talker.NextCycleText();

        Assert.EndsWith("\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n$", text.Replace("\r\n$", "|", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    private static void AssertFix(NmeaTalkerSimulator talker, string status, string quality, string gsaMode, int used)
    {
        Assert.Equal(status, Rmc(talker).Field(1));
        Assert.Equal(quality, Gga(talker).Field(5));
        Assert.Equal(used.ToString("00", System.Globalization.CultureInfo.InvariantCulture), Gga(talker).Field(6));
        Assert.Equal(gsaMode, Sentence(talker, "GSA").Field(1));
        Assert.Equal(used, talker.SatellitesUsed);
    }

    private static NmeaSentence Rmc(NmeaTalkerSimulator talker) => Sentence(talker, "RMC");

    private static NmeaSentence Gga(NmeaTalkerSimulator talker) => Sentence(talker, "GGA");

    private static NmeaSentence Sentence(NmeaTalkerSimulator talker, string identifier) =>
        talker.NextCycle().Select(line => NmeaSentence.TryParse(line)!).First(sentence => sentence.Identifier == identifier);
}
