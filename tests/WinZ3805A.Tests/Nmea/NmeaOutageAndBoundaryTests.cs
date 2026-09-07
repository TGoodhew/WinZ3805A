using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers.Nmea;
using WinZ3805A.Device.Models;
using WinZ3805A.Simulation;

namespace WinZ3805A.Tests.Nmea;

/// <summary>
/// The three ordinary cases the simulator could not previously produce (#420 stage 1): a fix being
/// taken away and coming back, two constellations at once, and a cycle straddling midnight.
/// </summary>
/// <remarks>
/// These are still synthetic — the driver has never met a real talker (#310) — but they are the
/// cases a generator can anticipate, and #420 puts them first precisely because they are cheap.
/// What is left for hardware is what no generator can anticipate.
/// </remarks>
public sealed class NmeaOutageAndBoundaryTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Appends the NMEA checksum, so a hand-written sentence is not silently rejected.</summary>
    private static string Sentence(string body)
    {
        byte checksum = 0;
        foreach (char c in body)
        {
            checksum ^= (byte)c;
        }

        return $"${body}*{checksum:X2}";
    }

    // ---- fix loss and reacquisition ---------------------------------------------------------------

    [Fact]
    public void AFixThatIsLostAndRegainedGoesThreeDimensionalThenNoneThenTwoThenThree()
    {
        // Until #420 the simulator's phases only moved forward, so A FIX HAD NEVER BEEN TAKEN AWAY
        // from the driver - the single largest untested ordinary case, and the one a user is most
        // likely to meet: an antenna knocked, a van parked alongside, a building passed.
        FakeTimeProvider clock = new(Start);
        NmeaTalkerSimulator talker = new(
            clock,
            fixAfter: TimeSpan.FromSeconds(10),
            threeDimensionalAfter: TimeSpan.FromSeconds(20),
            outages: [new NmeaOutage(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30))],
            reacquireAfter: TimeSpan.FromSeconds(5));

        List<(TimeSpan At, bool Fix, string Detail)> observed = [];
        foreach (int second in new[] { 30, 70, 92, 100 })
        {
            clock.SetUtcNow(Start.AddSeconds(second));
            ReceiverStatus status = NmeaStatusParser.Parse(talker.NextCycleText(), clock.GetUtcNow());
            observed.Add((TimeSpan.FromSeconds(second), status.GpsOnePpsValid, status.ModeDetail ?? string.Empty));
        }

        Assert.True(observed[0].Fix);
        Assert.Contains("3D", observed[0].Detail, StringComparison.Ordinal);

        Assert.False(observed[1].Fix);
        Assert.Contains("no fix", observed[1].Detail, StringComparison.Ordinal);

        Assert.True(observed[2].Fix);
        Assert.Contains("2D", observed[2].Detail, StringComparison.Ordinal);

        Assert.True(observed[3].Fix);
        Assert.Contains("3D", observed[3].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TimeGoesProvisionalWhileTheFixIsGoneAndComesBackWithIt()
    {
        // The parser's own judgement, stated on NmeaStatusParser: before a fix, a module's clock is
        // whatever it last had. Losing the fix must re-assert that, not leave a stale certainty.
        FakeTimeProvider clock = new(Start);
        NmeaTalkerSimulator talker = new(
            clock,
            fixAfter: TimeSpan.FromSeconds(5),
            threeDimensionalAfter: TimeSpan.FromSeconds(5),
            outages: [new NmeaOutage(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(20))]);

        clock.SetUtcNow(Start.AddSeconds(20));
        Assert.False(NmeaStatusParser.Parse(talker.NextCycleText(), clock.GetUtcNow()).DeviceTimeIsProvisional);

        clock.SetUtcNow(Start.AddSeconds(40));
        Assert.True(NmeaStatusParser.Parse(talker.NextCycleText(), clock.GetUtcNow()).DeviceTimeIsProvisional);

        clock.SetUtcNow(Start.AddSeconds(70));
        Assert.False(NmeaStatusParser.Parse(talker.NextCycleText(), clock.GetUtcNow()).DeviceTimeIsProvisional);
    }

    [Fact]
    public void AnOutageDuringWarmUpDoesNotHandOutAFixTheReceiverHadNotEarned()
    {
        FakeTimeProvider clock = new(Start);
        NmeaTalkerSimulator talker = new(
            clock,
            fixAfter: TimeSpan.FromSeconds(60),
            outages: [new NmeaOutage(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5))]);

        clock.SetUtcNow(Start.AddSeconds(12));   // just after the outage, still warming up

        Assert.False(NmeaStatusParser.Parse(talker.NextCycleText(), clock.GetUtcNow()).GpsOnePpsValid);
    }

    // ---- two constellations at once ----------------------------------------------------------------

    [Fact]
    public void TwoConstellationsEachRunTheirOwnGsvPagingAndBothArrive()
    {
        FakeTimeProvider clock = new(Start.AddSeconds(120));
        NmeaTalkerSimulator talker = new(
            clock,
            talker: "GN",
            extraConstellations: [new NmeaConstellation("GL", FirstPrn: 65, Count: 6)]);

        IReadOnlyList<string> cycle = talker.NextCycle();

        Assert.Contains(cycle, s => s.StartsWith("$GNGSV", StringComparison.Ordinal));
        Assert.Contains(cycle, s => s.StartsWith("$GLGSV", StringComparison.Ordinal));

        ReceiverStatus status = NmeaStatusParser.Parse(talker.NextCycleText(), clock.GetUtcNow());

        Assert.Contains(status.Tracked.Concat<object>(status.NotTracked), _ => true);
        Assert.Contains(
            status.Tracked.Select(s => s.Prn).Concat(status.NotTracked.Select(s => s.Prn)),
            prn => prn >= 65);
    }

    [Fact]
    public void AHealthyTwoConstellationReceiverProducesNoPageWarnings()
    {
        // The #417 defect, reproduced from the generator rather than from a hand-written fixture:
        // before that fix this warned on every cycle from a receiver working perfectly.
        FakeTimeProvider clock = new(Start.AddSeconds(120));
        NmeaTalkerSimulator talker = new(
            clock,
            talker: "GN",
            extraConstellations:
            [
                new NmeaConstellation("GL", 65, 6),
                new NmeaConstellation("GA", 301, 5),
            ]);

        ReceiverStatus status = NmeaStatusParser.Parse(talker.NextCycleText(), clock.GetUtcNow());

        Assert.DoesNotContain(status.ParseWarnings, w => w.Contains("GSV page", StringComparison.Ordinal));
    }

    // ---- the midnight boundary -----------------------------------------------------------------------

    [Fact]
    public void ACycleStraddlingMidnightIsReadConsistentlyRatherThanADayOut()
    {
        // #420 calls this "the classic case for disagreeing between them", and it turns out the
        // parser already handles it - because it prefers ONE source wholesale (ZDA's time with
        // ZDA's date, or RMC's with RMC's) rather than mixing a time from one with a date from
        // another. Worth a test that pins that property, because the obvious "improvement" of
        // taking the best available field from each sentence would break it silently and only ever
        // at midnight.
        DateTimeOffset justBefore = new(2026, 9, 6, 23, 59, 59, TimeSpan.Zero);
        FakeTimeProvider clock = new(justBefore);
        NmeaTalkerSimulator talker = new(
            clock,
            fixAfter: TimeSpan.Zero,
            threeDimensionalAfter: TimeSpan.Zero,
            sentenceSpacing: TimeSpan.FromMilliseconds(200));

        string cycle = talker.NextCycleText();
        ReceiverStatus status = NmeaStatusParser.Parse(cycle, clock.GetUtcNow());

        // The cycle genuinely straddles: RMC is on the 6th, ZDA on the 7th.
        Assert.Contains("060926", cycle, StringComparison.Ordinal);
        Assert.Contains(",07,09,2026,", cycle, StringComparison.Ordinal);

        // And the reading is one or the other, never a day out.
        Assert.NotNull(status.DeviceDateTime);
        TimeSpan drift = (status.DeviceDateTime.Value - justBefore).Duration();
        Assert.True(drift < TimeSpan.FromMinutes(1), $"the reading was {drift} from the true time");
    }

    [Fact]
    public void ACycleStraddlingAMonthEndIsAlsoReadConsistently()
    {
        DateTimeOffset justBefore = new(2026, 9, 30, 23, 59, 59, TimeSpan.Zero);
        FakeTimeProvider clock = new(justBefore);
        NmeaTalkerSimulator talker = new(
            clock,
            fixAfter: TimeSpan.Zero,
            threeDimensionalAfter: TimeSpan.Zero,
            sentenceSpacing: TimeSpan.FromMilliseconds(200));

        ReceiverStatus status = NmeaStatusParser.Parse(talker.NextCycleText(), clock.GetUtcNow());

        Assert.NotNull(status.DeviceDateTime);
        Assert.True((status.DeviceDateTime.Value - justBefore).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void ATimeFromOneSentenceIsNeverPairedWithADateFromAnother()
    {
        // The defect this found. A ZDA carrying a time but no readable date used to have its
        // 23:59:59 paired with RMC's date for the FOLLOWING day - a 24-hour error assembled from
        // two sentences that were each perfectly correct, silent, and only ever at midnight.
        //
        // Both are now taken from RMC, which is the sentence that has both.
        string zdaTimeOnly = Sentence("GPZDA,235959.00,,,,00,00");
        string rmcAfter = Sentence("GPRMC,000000.00,A,4737.2300,N,12220.9580,W,0.0,0.0,070926,,,A");

        ReceiverStatus status = NmeaStatusParser.Parse(
            zdaTimeOnly + "\n" + rmcAfter, new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero), status.DeviceDateTime);
    }

    [Fact]
    public void AZdaCarryingBothIsStillPreferredOverRmc()
    {
        // The fix must not have quietly demoted ZDA: it carries a four-digit year where RMC carries
        // two, which is the reason it is preferred in the first place.
        string zda = Sentence("GPZDA,120000.00,06,09,2026,00,00");
        string rmc = Sentence("GPRMC,110000.00,A,4737.2300,N,12220.9580,W,0.0,0.0,050926,,,A");

        ReceiverStatus status = NmeaStatusParser.Parse(
            zda + "\n" + rmc, new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero), status.DeviceDateTime);
    }

    [Fact]
    public void ATimeWithNoDateAnywhereStillSaysSo()
    {
        string ggaOnly = Sentence("GPGGA,120000.00,4737.2300,N,12220.9580,W,1,08,1.2,56.0,M,-19.6,M,,");

        ReceiverStatus status = NmeaStatusParser.Parse(ggaOnly, Start);

        Assert.Null(status.DeviceDateTime);
        Assert.Contains(status.ParseWarnings, w => w.Contains("no date", StringComparison.Ordinal));
    }
}
