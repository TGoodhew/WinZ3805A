using WinZ3805A.Device.Drivers.Nmea;
using WinZ3805A.Device.Models;

namespace WinZ3805A.Tests.Nmea;

/// <summary>
/// A multi-constellation talker, which emits a separate GSV cycle per constellation (#417, #420).
/// </summary>
/// <remarks>
/// <para>
/// Every talker this driver has been tested against emits one constellation — the simulator sends
/// <c>GP</c> or <c>GN</c>, one at a time. A GPS + GLONASS receiver interleaves <c>$GPGSV</c> and
/// <c>$GLGSV</c> in the same second, each with <b>its own page numbering</b>, and
/// <c>NmeaSentence.Key</c> deliberately strips the talker — <c>"$--" + identifier</c> — so both
/// land under one key and arrive at the parser as one run of pages.
/// </para>
/// <para>
/// Whether that is right depends on the field. For the satellite <i>list</i> it is: a user wants
/// every satellite in view regardless of constellation. For the <i>page accounting</i> it is not,
/// and that is what these tests pin.
/// </para>
/// </remarks>
public sealed class NmeaMixedConstellationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 20, 0, 0, TimeSpan.Zero);

    /// <summary>Appends the NMEA checksum, so these read as a real talker's output.</summary>
    private static string Sentence(string body)
    {
        byte checksum = 0;
        foreach (char c in body)
        {
            checksum ^= (byte)c;
        }

        return $"${body}*{checksum:X2}";
    }

    /// <summary>
    /// A cycle from a GPS + GLONASS receiver: three GPS GSV pages and two GLONASS ones.
    /// </summary>
    private static string MixedCycle() => string.Join('\n',
    [
        Sentence("GNRMC,200000.00,A,3726.2550,N,12210.5017,W,0.02,0.00,060926,,,A"),
        Sentence("GNGGA,200000.00,3726.2550,N,12210.5017,W,1,11,0.9,25.4,M,-32.0,M,,"),
        Sentence("GPGSV,3,1,11,01,40,083,42,02,17,308,38,03,07,344,35,04,22,228,40"),
        Sentence("GPGSV,3,2,11,05,50,140,45,06,33,046,39,07,12,190,33,08,60,270,47"),
        Sentence("GPGSV,3,3,11,09,25,120,36,10,45,015,41,11,08,300,30"),
        Sentence("GLGSV,2,1,05,65,30,100,38,66,55,200,44,67,15,050,31,68,40,280,40"),
        Sentence("GLGSV,2,2,05,69,20,160,34"),
    ]);

    [Fact]
    public void EverySatelliteFromBothConstellationsReachesTheStatus()
    {
        // The behaviour that is right: a user wants what is in the sky, not what is in one system.
        ReceiverStatus status = NmeaStatusParser.Parse(MixedCycle(), Now);

        int total = status.Tracked.Count + status.NotTracked.Count;

        Assert.Equal(16, total);
        Assert.Contains(status.Tracked, s => s.Prn == 1);
        Assert.Contains(status.Tracked, s => s.Prn == 65);
    }

    [Fact]
    public void ThePageAccountingIsPerConstellationAndDoesNotWarnOnAHealthyReceiver()
    {
        // THE DEFECT THIS FILE EXISTS FOR. The page total is read from the FIRST page - three, for
        // GPS - and compared against the count of ALL pages, five. Before the fix that produced
        // "the cycle carried 5 GSV page(s) of 3" on every single cycle, for ever, from a receiver
        // that is working perfectly. §11.1 warnings surface on the Diagnostics page, so this is the
        // parser's own version of a gate that cries wolf: a permanent stream of spurious warnings
        // teaches a user to ignore the one that matters.
        ReceiverStatus status = NmeaStatusParser.Parse(MixedCycle(), Now);

        Assert.DoesNotContain(status.ParseWarnings, w => w.Contains("GSV page", StringComparison.Ordinal));
    }

    [Fact]
    public void AConstellationShortOfAPageStillWarnsAndNamesTheTalker()
    {
        // The check must keep working per constellation, or fixing the false alarm above would have
        // thrown away the real one with it.
        string missingPage = string.Join('\n',
        [
            Sentence("GNRMC,200000.00,A,3726.2550,N,12210.5017,W,0.02,0.00,060926,,,A"),
            Sentence("GPGSV,3,1,11,01,40,083,42,02,17,308,38,03,07,344,35,04,22,228,40"),
            Sentence("GPGSV,3,2,11,05,50,140,45,06,33,046,39,07,12,190,33,08,60,270,47"),
            Sentence("GLGSV,2,1,05,65,30,100,38,66,55,200,44,67,15,050,31,68,40,280,40"),
            Sentence("GLGSV,2,2,05,69,20,160,34"),
        ]);

        ReceiverStatus status = NmeaStatusParser.Parse(missingPage, Now);

        Assert.Contains(
            status.ParseWarnings,
            w => w.Contains("GSV", StringComparison.Ordinal) && w.Contains("GP", StringComparison.Ordinal));
        Assert.DoesNotContain(
            status.ParseWarnings,
            w => w.Contains("GSV", StringComparison.Ordinal) && w.Contains("GL", StringComparison.Ordinal));
    }

    [Fact]
    public void ASingleConstellationCycleIsUnaffected()
    {
        string single = string.Join('\n',
        [
            Sentence("GPRMC,200000.00,A,3726.2550,N,12210.5017,W,0.02,0.00,060926,,,A"),
            Sentence("GPGSV,2,1,05,01,40,083,42,02,17,308,38,03,07,344,35,04,22,228,40"),
            Sentence("GPGSV,2,2,05,05,50,140,45"),
        ]);

        ReceiverStatus status = NmeaStatusParser.Parse(single, Now);

        Assert.Equal(5, status.Tracked.Count + status.NotTracked.Count);
        Assert.DoesNotContain(status.ParseWarnings, w => w.Contains("GSV page", StringComparison.Ordinal));
    }

    [Fact]
    public void AReceiverSendingGnsInsteadOfGgaStillShowsAFixAndAPosition()
    {
        // #417 feared that a multi-constellation receiver emitting GNS in place of GGA would show
        // "no fix at all while the receiver is perfectly happy". IT DOES NOT, and the premise is
        // worth correcting rather than carrying: the fix quality falls back to RMC's status field,
        // and RMC also carries the position. GNS is unparsed, so what is actually lost is the
        // ALTITUDE and the per-constellation mode detail - an enhancement, not a correctness bug.
        string gnsCycle = string.Join('\n',
        [
            Sentence("GNRMC,200000.00,A,3726.2550,N,12210.5017,W,0.02,0.00,060926,,,A"),
            Sentence("GNGNS,200000.00,3726.2550,N,12210.5017,W,AAN,11,0.9,25.4,-32.0,,"),
            Sentence("GPGSV,1,1,02,01,40,083,42,02,17,308,38"),
        ]);

        ReceiverStatus status = NmeaStatusParser.Parse(gnsCycle, Now);

        Assert.True(status.GpsOnePpsValid);
        Assert.NotNull(status.Position);
        Assert.Equal(37.4375833, status.Position.LatitudeDegrees!.Value, 5);
        Assert.False(status.DeviceTimeIsProvisional);

        // What the gap actually costs.
        Assert.Null(status.Position.HeightMetres);
    }

    /// <summary>
    /// The same claim, against a receiver that really was sending GNS and no GGA (#429).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>#429 said this configuration's reachability was unknown</b> — "no receiver we can test
    /// against has ever been seen to omit `GGA`", and whether one could be configured that way "is
    /// unknown". It is reachable, and trivially: `UBX-CFG-MSG` enables GNS and disables GGA on the
    /// VK-162 already in the corpus. `vk162-gns-no-gga.nmea` is twelve minutes of the result — 720
    /// GNS sentences and **not one GGA**.
    /// </para>
    /// <para>
    /// So the test above is no longer the only evidence. This one asserts the same three things
    /// against bytes a receiver actually sent, which is the difference between believing the
    /// fallback works and knowing it.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheRealReceiverSendingGnsAndNoGgaStillShowsAFixAndAPosition()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory, "Nmea", "Captures", "vk162-gns-no-gga.nmea");
        Assert.True(File.Exists(path), $"{path} is missing; the capture is the evidence.");

        string[] lines = File.ReadAllText(path).Split('\n');

        // The receiver's own bytes, not a hand-written cycle: one RMC and the GNS that follows it.
        int rmc = Array.FindIndex(lines, l => l.StartsWith("$GPRMC", StringComparison.Ordinal) && l.Contains(",A,", StringComparison.Ordinal));
        Assert.True(rmc >= 0, "the capture holds no RMC reporting a valid fix.");

        string cycle = string.Join('\n', lines.Skip(rmc).Take(8).Select(l => l.TrimEnd('\r')));
        Assert.DoesNotContain("$GPGGA", cycle, StringComparison.Ordinal);
        Assert.Contains("$GPGNS", cycle, StringComparison.Ordinal);

        ReceiverStatus status = NmeaStatusParser.Parse(cycle, Now);

        // The fear #417 raised, refuted against hardware: the fix survives GGA's absence.
        Assert.True(status.GpsOnePpsValid);
        Assert.NotNull(status.Position);
        Assert.InRange(status.Position.LatitudeDegrees!.Value, 47.0, 48.0);
        Assert.InRange(status.Position.LongitudeDegrees!.Value, -123.0, -122.0);

        // And the cost, also against hardware. Every one of the 720 GNS sentences carries an
        // altitude in field 10; none of it reaches the model, because GNS is unparsed.
        Assert.Null(status.Position.HeightMetres);
    }

    [Fact]
    public void TwoConstellationsNumberingTheirSatellitesTheSameWayLoseOne()
    {
        // NOT A FIX - A RECORD OF WHAT HAPPENS TODAY, so the behaviour is visible rather than
        // discovered by a user (#424).
        //
        // NMEA 4.10 gives each constellation its own satellite-number range - GPS 1-32, SBAS 33-64,
        // GLONASS 65-96 - and a conforming receiver never collides. Receivers exist that number
        // per constellation instead and rely on the talker to disambiguate, and against one of
        // those the parser's dedupe is a plain `HashSet<int>` of PRNs, so the second constellation's
        // satellite 1 is silently dropped: the sky plot shows fewer satellites than are being
        // tracked, which reads as poor reception rather than as a parsing choice.
        //
        // Changing it means either duplicate PRNs in the model or a wider satellite identity, and
        // both are model changes that want a real receiver in front of them - a fix aimed at a
        // receiver nobody has seen is a guess with tests attached. Pinned here so the day one turns
        // up, this test names the cause.
        string colliding = string.Join('\n',
        [
            Sentence("GNRMC,200000.00,A,3726.2550,N,12210.5017,W,0.02,0.00,060926,,,A"),
            Sentence("GPGSV,1,1,02,01,40,083,42,02,17,308,38"),
            Sentence("GLGSV,1,1,02,01,55,200,44,03,15,050,31"),
        ]);

        ReceiverStatus status = NmeaStatusParser.Parse(colliding, Now);

        // Four satellites were reported; three survive, because both talkers claimed number 1.
        Assert.Equal(3, status.Tracked.Count + status.NotTracked.Count);
        Assert.Equal(42, status.Tracked.Single(s => s.Prn == 1).SignalStrength);
    }

    [Fact]
    public void TalkersBeyondGpAndGnAreUnderstood()
    {
        // GL GLONASS, GA Galileo, GB BeiDou, GQ QZSS. Everything we have ever tested against is one
        // of two talkers, so this is the cheapest possible check that the other four are not
        // silently discarded.
        foreach (string talker in new[] { "GL", "GA", "GB", "GQ" })
        {
            string cycle = string.Join('\n',
            [
                Sentence("GNRMC,200000.00,A,3726.2550,N,12210.5017,W,0.02,0.00,060926,,,A"),
                Sentence($"{talker}GSV,1,1,02,11,40,083,42,12,17,308,38"),
            ]);

            ReceiverStatus status = NmeaStatusParser.Parse(cycle, Now);

            Assert.True(
                status.Tracked.Count + status.NotTracked.Count == 2,
                $"talker {talker} contributed no satellites");
        }
    }

    /// <summary>
    /// A real receiver colliding, and the parser dropping one of the two (#424).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>NOT A FIX - A RECORD OF WHAT HAPPENS TODAY</b>, exactly as
    /// <see cref="TwoConstellationsNumberingTheirSatellitesTheSameWayLoseOne"/> is. The difference is
    /// the evidence. That test builds a collision to show what would happen, which is why #424 could
    /// only ever call the defect "latent, not active" and reachable in principle. This one replays
    /// <c>form8n-gps-beidou-outdoors.nmea</c>: the forM8N on its own patch antenna outdoors, where
    /// BeiDou actually tracks.
    /// </para>
    /// <para>
    /// It is neither latent nor rare. That receiver numbers BeiDou per constellation - 6, 20, 23, 24,
    /// 25, all inside the GPS 1-32 range - and relies on the <c>GB</c> and <c>GP</c> talkers to tell
    /// them apart. GPS 4 and BeiDou 4 are in view together in <b>1,217 of the sitting's 1,800
    /// cycles</b>, and the parser's <c>HashSet&lt;int&gt;</c> of PRNs keeps the first and discards the
    /// second in every one of them.
    /// </para>
    /// <para>
    /// <b>It is also the only capture in the corpus that collides at all.</b> The other seven have
    /// zero such cycles between them - the VK-162 cannot collide, its GLONASS PRNs being 67-85 - so
    /// deleting this file would take the whole of #424's evidence with it, and the assertion below
    /// says so rather than passing vacuously.
    /// </para>
    /// </remarks>
    [Fact]
    public void ARealReceiverCollidesAndTheParserDropsASatellite()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory, "Nmea", "Captures", "form8n-gps-beidou-outdoors.nmea");
        Assert.True(File.Exists(path), $"{path} is missing; the capture is the evidence.");

        string[] lines = File.ReadAllText(path).Split('\n');

        int cycles = 0;
        int colliding = 0;
        string? first = null;
        int reportedInFirst = 0;

        List<string> current = [];
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd('\r');
            if (line.Length < 9 || line[0] != '$')
            {
                continue;
            }

            // RMC is this driver's cycle boundary, so a cycle is the run of sentences after one.
            if (line.AsSpan(3, 3) is "RMC")
            {
                if (current.Count > 0)
                {
                    cycles++;
                    int reported = Reported(current, out bool clashed);
                    if (clashed)
                    {
                        colliding++;
                        if (first is null)
                        {
                            first = string.Join('\n', current);
                            reportedInFirst = reported;
                        }
                    }
                }

                current = [line];
                continue;
            }

            current.Add(line);
        }

        Assert.Equal(1800, cycles);
        Assert.True(
            colliding >= 1000,
            $"only {colliding} of {cycles} cycles collided, where this capture was measured at 1,217.");
        Assert.NotNull(first);

        // What the receiver reported, against what the parser kept.
        ReceiverStatus status = NmeaStatusParser.Parse(first, Now);
        Assert.Equal(reportedInFirst - 1, status.Tracked.Count + status.NotTracked.Count);

        // And the loss is invisible: the number survives, once, so nothing on screen suggests a
        // satellite is missing. That is the part of #424 that sends someone onto the roof.
        int keptFour = status.Tracked.Count(satellite => satellite.Prn == 4)
            + status.NotTracked.Count(satellite => satellite.Prn == 4);
        Assert.Equal(1, keptFour);
    }

    /// <summary>
    /// How many satellites a cycle's GSV pages report, counting a number claimed by two talkers as
    /// the two satellites it is, and whether any such pair was present.
    /// </summary>
    private static int Reported(IEnumerable<string> cycle, out bool collided)
    {
        Dictionary<string, HashSet<int>> byTalker = new(StringComparer.Ordinal);

        foreach (string line in cycle)
        {
            int star = line.LastIndexOf('*');
            if (star < 0 || line.AsSpan(3, 3) is not "GSV")
            {
                continue;
            }

            string[] fields = line[1..star].Split(',');
            string talker = fields[0][..2];

            // Three header fields, then four per satellite: number, elevation, azimuth, C/N. This
            // receiver's PROTVER 18 output appends a signal id, and the bound leaves it alone
            // because a satellite block needs all four of its fields to be one.
            for (int i = 4; i + 3 < fields.Length; i += 4)
            {
                if (int.TryParse(fields[i], out int prn) && prn > 0)
                {
                    if (!byTalker.TryGetValue(talker, out HashSet<int>? seen))
                    {
                        seen = [];
                        byTalker[talker] = seen;
                    }

                    seen.Add(prn);
                }
            }
        }

        collided = byTalker.Values
            .SelectMany(set => set)
            .GroupBy(prn => prn)
            .Any(group => group.Count() > 1);

        return byTalker.Values.Sum(set => set.Count);
    }
}
