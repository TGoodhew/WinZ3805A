using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Drivers.Nmea;
using WinZ3805A.Device.Models;
using WinZ3805A.Simulation;

namespace WinZ3805A.Tests.Nmea;

/// <summary>
/// The NMEA 0183 driver against simulated cycles (#310): recognition by hearing, the fast sweep,
/// the full parse, and the never-throw rule.
/// </summary>
/// <remarks>
/// The simulator stands in for a capture here because no real talker has been captured yet;
/// #309's capture of the BG7TBL is what these expectations get compared against, and whatever
/// disagrees is folded back into both.
/// </remarks>
public sealed class NmeaDriverTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private static NmeaDriver Driver(FakeTimeProvider clock) => new(clock);

    private static (FakeTimeProvider Clock, NmeaDriver Driver, NmeaTalkerSimulator Talker) Bench(TimeSpan? age = null)
    {
        FakeTimeProvider clock = new(Start);
        NmeaTalkerSimulator talker = new(clock);
        if (age is TimeSpan elapsed)
        {
            clock.Advance(elapsed);
        }

        return (clock, Driver(clock), talker);
    }

    private static IReadOnlyList<string?> Sweep(NmeaDriver driver, IReadOnlyList<string> cycle)
    {
        // What the listener answers each fast-tier key with: the discriminator's newest line, and
        // every line of the key in the cycle, joined as a Transaction's Text is.
        return driver.Plan.FastTier
            .Select(key => string.Join('\n', cycle.Where(line => driver.ClassifyLine(line) == key)))
            .Select(text => text.Length == 0 ? null : text)
            .ToList();
    }

    // -------------------------------------------------------------------------------------
    // Recognition by hearing
    // -------------------------------------------------------------------------------------

    [Fact]
    public void ACycleIsOverheardAsAGpsTalker()
    {
        (_, NmeaDriver driver, NmeaTalkerSimulator talker) = Bench();

        DeviceIdentity identity = Assert.IsType<DeviceIdentity>(driver.Overhear(talker.NextCycle()));

        Assert.Equal(NmeaDriver.FamilyName, identity.Manufacturer);
        Assert.Equal("GP talker", identity.Model);
        Assert.Equal(ReceiverModel.Unknown, identity.Receiver);
        Assert.True(driver.Recognises(identity));
    }

    [Fact]
    public void OneValidSentenceAmongNoiseIsEnough()
    {
        (_, NmeaDriver driver, NmeaTalkerSimulator talker) = Bench();

        Assert.NotNull(driver.Overhear(["\0ÿ", "scpi > ", talker.NextCycle()[0], "junk"]));
    }

    [Fact]
    public void ASmartClockIsNotClaimed()
    {
        (_, NmeaDriver driver, _) = Bench();

        Assert.False(driver.Recognises(DeviceIdentity.Parse("SYMMETRICOM,Z3805A,3625A02931,1.01.03-A")));
        Assert.False(driver.Recognises(null));
        Assert.Null(driver.Overhear(["SYMMETRICOM,Z3805A,3625A02931,1.01.03-A", "scpi > "]));
    }

    /// <summary>The identity survives the session's string form, which is how it is logged and shown.</summary>
    [Fact]
    public void TheIdentityRoundTripsThroughItsStringForm()
    {
        DeviceIdentity identity = NmeaDriver.IdentityFor("GN");
        string text = $"{identity.Manufacturer},{identity.Model},{identity.SerialNumber},{identity.FirmwareRevision}";

        Assert.Equal(identity, DeviceIdentity.Parse(text));
    }

    // -------------------------------------------------------------------------------------
    // Classification
    // -------------------------------------------------------------------------------------

    [Fact]
    public void EveryPlanKeyIsProducedByACycle()
    {
        (_, NmeaDriver driver, NmeaTalkerSimulator talker) = Bench();

        HashSet<string> keys = talker.NextCycle().Select(driver.ClassifyLine).OfType<string>().ToHashSet(StringComparer.Ordinal);

        foreach (string key in driver.Plan.FastTier)
        {
            Assert.Contains(key, keys);
        }
    }

    [Theory]
    [InlineData("GP", "RMC", "$--RMC")]
    [InlineData("GN", "GGA", "$--GGA")]
    [InlineData("GL", "GSV", "$--GSV")]
    public void AValidSentenceOfAKnownKindHasItsKey(string talker, string identifier, string key)
    {
        // Built by the codec rather than typed with a checksum computed by hand, which is how two
        // of this file's first expectations were wrong.
        string line = NmeaSentence.Format(talker, identifier, "120000.00", "V", "", "", "", "", "", "", "290826", "", "");

        Assert.Equal(key, Driver(new FakeTimeProvider(Start)).ClassifyLine(line));
    }

    [Theory]
    [InlineData("$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*48")]
    [InlineData("$GPTXT,01,01,02,u-blox ag - www.u-blox.com*50")]
    [InlineData("$PUBX,00,081350.00,4717.113210,N,00833.915187,E,546.589,G3,2.1,2.0,0.007,77.52,0.007,,0.92,1.19,0.77,9,0,0*5F")]
    [InlineData("$HCHDM,238,M")]
    [InlineData("SYMMETRICOM,Z3805A,3625A02931,1.01.03-A")]
    public void AWrongChecksumOrAnUnknownOrForeignSentenceIsNotClassified(string line) =>
        Assert.Null(Driver(new FakeTimeProvider(Start)).ClassifyLine(line));

    // -------------------------------------------------------------------------------------
    // The fast sweep
    // -------------------------------------------------------------------------------------

    [Fact]
    public void ColdIsPowerUpWithTheStrongSatellitesTracked()
    {
        (_, NmeaDriver driver, NmeaTalkerSimulator talker) = Bench();

        SweepInterpretation sweep = driver.InterpretSweep(Sweep(driver, talker.NextCycle()));

        Assert.Null(sweep.Rejection);
        Assert.Equal(NmeaDriver.NoFixToken, sweep.Readings.SyncState);
        Assert.Equal(6, sweep.Readings.SatellitesTracked);
        Assert.Null(sweep.Readings.Tfom);
        Assert.Null(sweep.Readings.Ffom);
        Assert.Null(sweep.Readings.TimeIntervalNanoseconds);
        Assert.Null(sweep.Readings.EfcPercent);
    }

    [Fact]
    public void AFixIsLockedWithEveryHeardSatelliteTracked()
    {
        (_, NmeaDriver driver, NmeaTalkerSimulator talker) = Bench(TimeSpan.FromSeconds(45));

        SweepInterpretation sweep = driver.InterpretSweep(Sweep(driver, talker.NextCycle()));

        Assert.Null(sweep.Rejection);
        Assert.Equal(NmeaDriver.FixToken, sweep.Readings.SyncState);
        Assert.Equal(8, sweep.Readings.SatellitesTracked);
    }

    [Fact]
    public void ASweepWhoseBoundaryIsNotRmcIsRejectedWithWhatWasSeen()
    {
        (_, NmeaDriver driver, NmeaTalkerSimulator talker) = Bench();
        List<string> cycle = talker.NextCycle().ToList();

        SweepInterpretation sweep = driver.InterpretSweep([cycle[1], cycle[1], cycle[2]]);

        Assert.NotNull(sweep.Rejection);
        Assert.Contains("GGA", sweep.Rejection, StringComparison.Ordinal);
        Assert.Contains("not an RMC", sweep.Rejection, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingHeardYetIsRejectedNotInvented()
    {
        (_, NmeaDriver driver, _) = Bench();

        SweepInterpretation sweep = driver.InterpretSweep([null, null, null, null]);

        Assert.NotNull(sweep.Rejection);
        Assert.Null(sweep.Readings.SyncState);
        Assert.Null(sweep.Readings.SatellitesTracked);
    }

    // -------------------------------------------------------------------------------------
    // The full parse
    // -------------------------------------------------------------------------------------

    [Fact]
    public void AThreeDimensionalCycleFillsPositionTimeAndSatellites()
    {
        (FakeTimeProvider clock, NmeaDriver driver, NmeaTalkerSimulator talker) = Bench(TimeSpan.FromSeconds(45));

        ReceiverStatus status = driver.Parse(talker.NextCycleText());

        Assert.Empty(status.ParseWarnings);
        Assert.Equal("GPS fix (3D)", status.ModeDetail);
        Assert.True(status.GpsOnePpsValid);
        Assert.False(status.DeviceTimeIsProvisional);

        GeoPosition position = Assert.IsType<GeoPosition>(status.Position);
        Assert.Equal(47.6205, position.LatitudeDegrees!.Value, 4);
        Assert.Equal(-122.3493, position.LongitudeDegrees!.Value, 4);
        Assert.Equal(56.0, position.HeightMetres);
        Assert.Equal(HeightDatum.Msl, status.HeightDatum);

        Assert.Equal(clock.GetUtcNow().ToUnixTimeSeconds(), status.DeviceDateTime!.Value.ToUnixTimeSeconds());
        Assert.Equal(status.DeviceDateTime, status.CorrectedDateTime);
        Assert.Equal(TimeScale.Utc, status.TimeScale);
        Assert.Equal(0, status.WeekRolloverEpochs);

        Assert.Equal(8, status.Tracked.Count);
        Assert.Equal(2, status.NotTracked.Count);
        Assert.Equal([1, 7], status.NotTracked.Select(s => s.Prn).Order());
        Assert.Equal(SignalStrengthKind.CarrierToNoise, status.SignalStrengthKind);
        TrackedSatellite prn3 = Assert.Single(status.Tracked, s => s.Prn == 3);
        Assert.Equal(38, prn3.SignalStrength);
        Assert.InRange(prn3.ElevationDegrees!.Value, 60, 75);

        Assert.Equal(clock.GetUtcNow(), status.CapturedAt);
    }

    /// <summary>HP's fields stay absent: a talker has no oscillator to discipline.</summary>
    [Fact]
    public void WhatATalkerDoesNotSayIsLeftUnsaid()
    {
        (_, NmeaDriver driver, NmeaTalkerSimulator talker) = Bench(TimeSpan.FromSeconds(45));

        ReceiverStatus status = driver.Parse(talker.NextCycleText());

        Assert.Null(status.Tfom);
        Assert.Null(status.Ffom);
        Assert.Null(status.OnePpsTiNanoseconds);
        Assert.Null(status.HoldoverPredictedSeconds);
        Assert.Null(status.AntennaDelayNanoseconds);
        Assert.Equal(SmartClockMode.Unknown, status.Mode);
        Assert.Equal(OutputValidity.Unknown, status.Outputs);
        Assert.Equal(PositionMode.Unknown, status.PositionMode);
        Assert.Empty(status.HealthItems);
    }

    [Fact]
    public void AColdCycleHasNoPositionAndAProvisionalTime()
    {
        (_, NmeaDriver driver, NmeaTalkerSimulator talker) = Bench();

        ReceiverStatus status = driver.Parse(talker.NextCycleText());

        Assert.Equal("no fix", status.ModeDetail);
        Assert.False(status.GpsOnePpsValid);
        Assert.Null(status.Position);
        Assert.True(status.DeviceTimeIsProvisional);
        Assert.NotNull(status.DeviceDateTime);
        Assert.Equal(6, status.Tracked.Count);
        Assert.Equal(4, status.NotTracked.Count);
    }

    [Fact]
    public void ACorruptPageIsIgnoredWithAWarning()
    {
        (_, NmeaDriver driver, NmeaTalkerSimulator talker) = Bench(TimeSpan.FromSeconds(45));
        string cycle = talker.NextCycleText().Replace("$GPGSV,3,2,", "$GPGSV,3,9,", StringComparison.Ordinal);

        ReceiverStatus status = driver.Parse(cycle);

        Assert.Contains(status.ParseWarnings, warning => warning.Contains("checksum", StringComparison.Ordinal));
        Assert.Contains(status.ParseWarnings, warning => warning.Contains("GSV page", StringComparison.Ordinal));
        Assert.True(status.Tracked.Count < 8);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\0\0\0")]
    [InlineData("scpi > ")]
    [InlineData("$GPRMC,\n$GPGGA,,,,,,,,,,,,,,*56\n$GPGSV,1,1,00*79")]
    public void ParsingAnythingNeverThrows(string? response)
    {
        (_, NmeaDriver driver, _) = Bench();

        ReceiverStatus status = driver.Parse(response);

        Assert.NotNull(status);
        Assert.NotNull(status.ParseWarnings);
    }

    // -------------------------------------------------------------------------------------
    // The catalog and the safety model
    // -------------------------------------------------------------------------------------

    [Fact]
    public void TheCatalogIsReadsOnlyAndNothingIsBlocked()
    {
        (_, NmeaDriver driver, _) = Bench();

        Assert.All(driver.Commands, command => Assert.True(command.IsQuery));
        Assert.All(driver.Commands, command => Assert.Equal(SafetyTier.Safe, command.Tier));
        Assert.NotNull(driver.Find("$--GGA"));
        Assert.NotNull(driver.Find(" $--gsv "));
        Assert.NotNull(driver.Find(PollPlan.WholeCycle));
        Assert.Null(driver.Find(":SYST:STAT?"));
        Assert.False(driver.IsBlocked("$PUBX,41,1,0007,0003,9600,0"));
        Assert.False(driver.IsBlocked(null));
    }

    [Fact]
    public void TheLinkIsBroadcastAndTheSequenceStartsAtTheStandardsRate()
    {
        (_, NmeaDriver driver, _) = Bench();

        Assert.Equal(LinkStyle.Broadcast, driver.Link);
        Assert.Equal(4800, driver.AutoDetectSequence[0].BaudRate);
        Assert.Equal(TimeSpan.FromSeconds(3), driver.TimeoutFor("$--RMC"));
        Assert.True(driver.Cadence.Full > driver.Cadence.Fast);
    }
}
