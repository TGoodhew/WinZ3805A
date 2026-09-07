using System.Globalization;

using WinZ3805A.Device.Drivers.Nmea;

namespace WinZ3805A.Simulation;

/// <summary>Where the simulated receiver is in its start-up.</summary>
public enum FixPhase
{
    /// <summary>Satellites in view, none used; GGA quality 0, RMC status V.</summary>
    NoFix = 0,

    /// <summary>A first fix with three satellites; GSA mode 2.</summary>
    TwoDimensional,

    /// <summary>A full fix; GSA mode 3, altitude reported.</summary>
    ThreeDimensional,
}

/// <summary>
/// A window during which the receiver has no fix, whatever it had before (#420).
/// </summary>
/// <remarks>
/// <b>The simulator's phases only ever moved forward — power-up, 2D, 3D — so a fix had never been
/// taken away from the driver.</b> A receiver that loses its fix and regains it is ordinary
/// behaviour: an antenna knocked, a van parked alongside, a building passed. It was the largest
/// untested ordinary case, and it is the one a user is most likely to meet.
/// </remarks>
/// <param name="StartsAfter">How long after start-up the fix goes.</param>
/// <param name="Duration">How long it stays gone.</param>
public readonly record struct NmeaOutage(TimeSpan StartsAfter, TimeSpan Duration);

/// <summary>
/// A second constellation reporting its own GSV cycle alongside the first (#417, #420).
/// </summary>
/// <remarks>
/// A multi-constellation receiver emits <b>a separate GSV run per constellation</b>, each with its
/// own page numbering, interleaved in the same second. Every talker this project had tested against
/// was one of two, so the interleaving had never been produced at all.
/// </remarks>
/// <param name="Talker">The talker identifier — <c>GL</c> GLONASS, <c>GA</c> Galileo, <c>GB</c> BeiDou, <c>GQ</c> QZSS.</param>
/// <param name="FirstPrn">
/// The first satellite number. NMEA 4.10 gives each constellation its own range — GLONASS 65-96,
/// SBAS 33-64 — and setting this into another constellation's range is how #424's collision is
/// reproduced deliberately.
/// </param>
/// <param name="Count">How many satellites it reports.</param>
public readonly record struct NmeaConstellation(string Talker, int FirstPrn, int Count);

/// <summary>
/// A GPS receiver that exists only as sentences: one NMEA 0183 cycle per call, scripted from
/// power-up through a 2D fix to a 3D one (#310).
/// </summary>
/// <remarks>
/// <para>
/// This is the tutorial's receiver on the bench. A reader without hardware runs it in-process
/// against the driver's tests, or through <c>tools/NmeaSimulator</c> into one end of a serial-port
/// pair with the packaged application on the other, and sees every step of
/// <c>docs/adding-a-receiver.md</c> work before touching a real unit. It lives under
/// <c>tools/</c>, not in the Device library: the driver must not depend on its simulator, and a
/// reader who wants only the driver copies one folder and never sees this one.
/// </para>
/// <para>
/// <b>What it deliberately gets right.</b> Sentences carry the checksum every real talker sends,
/// in the order a u-blox module sends them (RMC, GGA, GSA, the GSV pages, ZDA), with up to four
/// satellites per GSV page and the page count in every page; the fix state moves through the
/// three phases a cold start moves through; satellites drift across the sky slowly enough to be
/// plausible and fast enough to be visible; time advances with the injected clock, so a test can
/// step it. <b>What it does not pretend to be:</b> a particular product. There are no proprietary
/// sentences, no lock or holdover state — NMEA has none — and no serial quirks. No real talker
/// has been captured (#309, the BG7TBL, was deferred: its port carries no NMEA); when one is,
/// this is the thing that capture will be compared against.
/// </para>
/// <para>
/// Deterministic for a given clock and options: two runs from the same start produce the same
/// bytes, which is what lets a test assert a value rather than a shape.
/// </para>
/// </remarks>
public sealed class NmeaTalkerSimulator
{
    /// <summary>The satellite set: PRN, starting elevation, starting azimuth, and signal-to-noise once tracked.</summary>
    private static readonly (int Prn, double Elevation, double Azimuth, int Snr)[] Constellation =
    [
        (3, 68, 174, 38),
        (4, 64, 297, 37),
        (6, 22, 308, 35),
        (9, 29, 283, 34),
        (26, 41, 94, 36),
        (31, 41, 55, 39),
        (16, 28, 133, 31),
        (28, 14, 51, 29),
        (1, 8, 199, 0),
        (7, 7, 226, 0),
    ];

    private const int SatellitesPerPage = 4;

    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _startedAt;
    private readonly string _talker;
    private readonly double _latitude;
    private readonly double _longitude;
    private readonly double _height;
    private readonly TimeSpan _fixAfter;
    private readonly TimeSpan _threeDimensionalAfter;
    private readonly IReadOnlyList<NmeaOutage> _outages;
    private readonly TimeSpan _reacquireAfter;
    private readonly IReadOnlyList<NmeaConstellation> _extraConstellations;
    private readonly TimeSpan _sentenceSpacing;

    /// <summary>Creates a receiver that starts cold at the clock's current time.</summary>
    /// <param name="timeProvider">The clock every sentence's time comes from.</param>
    /// <param name="talker">The talker identifier — <c>GP</c> (GPS) by default, <c>GN</c> for a multi-constellation receiver.</param>
    /// <param name="latitudeDegrees">The antenna's latitude, positive north.</param>
    /// <param name="longitudeDegrees">The antenna's longitude, positive east.</param>
    /// <param name="heightMetres">The antenna's height above mean sea level.</param>
    /// <param name="fixAfter">How long after start the first (2D) fix arrives.</param>
    /// <param name="threeDimensionalAfter">How long after start the fix becomes 3D.</param>
    /// <param name="outages">Windows during which the fix is lost, relative to start-up (#420).</param>
    /// <param name="reacquireAfter">
    /// How long after an outage ends the fix is only 2D before returning to 3D. A hot start, not a
    /// cold one: a receiver that has just lost its fix still has almanac and ephemeris, so it comes
    /// back in seconds rather than repeating <paramref name="fixAfter"/>.
    /// </param>
    /// <param name="extraConstellations">Further constellations reporting their own GSV runs (#417).</param>
    /// <param name="sentenceSpacing">
    /// How much time passes between one sentence of a cycle and the next. Zero — the default and
    /// the old behaviour — makes a cycle instantaneous. A real talker takes tens of milliseconds to
    /// put a cycle on the wire, and setting this is what lets a cycle <b>straddle midnight</b>, so
    /// that the sentences carrying the date and the sentences carrying the time disagree.
    /// </param>
    public NmeaTalkerSimulator(
        TimeProvider timeProvider,
        string talker = "GP",
        double latitudeDegrees = 47.6205,
        double longitudeDegrees = -122.3493,
        double heightMetres = 56.0,
        TimeSpan? fixAfter = null,
        TimeSpan? threeDimensionalAfter = null,
        IReadOnlyList<NmeaOutage>? outages = null,
        TimeSpan? reacquireAfter = null,
        IReadOnlyList<NmeaConstellation>? extraConstellations = null,
        TimeSpan? sentenceSpacing = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(talker);

        _timeProvider = timeProvider;
        _startedAt = timeProvider.GetUtcNow();
        _talker = talker;
        _latitude = latitudeDegrees;
        _longitude = longitudeDegrees;
        _height = heightMetres;
        _fixAfter = fixAfter ?? TimeSpan.FromSeconds(20);
        _threeDimensionalAfter = threeDimensionalAfter ?? TimeSpan.FromSeconds(40);

        if (_threeDimensionalAfter < _fixAfter)
        {
            _threeDimensionalAfter = _fixAfter;
        }

        _outages = outages ?? [];
        _reacquireAfter = reacquireAfter ?? TimeSpan.FromSeconds(3);
        _extraConstellations = extraConstellations ?? [];
        _sentenceSpacing = sentenceSpacing ?? TimeSpan.Zero;
    }

    /// <summary>The talker identifier every sentence carries.</summary>
    public string Talker => _talker;

    /// <summary>Where the receiver is in its start-up, at the clock's current time.</summary>
    public FixPhase Phase => PhaseAt(_timeProvider.GetUtcNow());

    /// <summary>Where the receiver is at a given moment, outages included (#420).</summary>
    /// <remarks>
    /// An outage wins over the start-up script, and a reacquisition is a hot start: the fix comes
    /// back as 2D for <c>reacquireAfter</c> and then returns to whatever the script says. That
    /// produces the sequence a real receiver produces — <c>3D → none → 2D → 3D</c> — rather than
    /// snapping straight back to 3D, which would let a driver pass without ever seeing a downgrade.
    /// </remarks>
    private FixPhase PhaseAt(DateTimeOffset when)
    {
        TimeSpan elapsed = when - _startedAt;

        foreach (NmeaOutage outage in _outages)
        {
            TimeSpan ends = outage.StartsAfter + outage.Duration;

            if (elapsed >= outage.StartsAfter && elapsed < ends)
            {
                return FixPhase.NoFix;
            }

            if (elapsed >= ends && elapsed < ends + _reacquireAfter)
            {
                // Never better than the script: an outage during warm-up must not hand out a fix
                // the receiver had not earned yet.
                return Scripted(elapsed) == FixPhase.NoFix ? FixPhase.NoFix : FixPhase.TwoDimensional;
            }
        }

        return Scripted(elapsed);
    }

    private FixPhase Scripted(TimeSpan elapsed) =>
        elapsed >= _threeDimensionalAfter ? FixPhase.ThreeDimensional
            : elapsed >= _fixAfter ? FixPhase.TwoDimensional
            : FixPhase.NoFix;

    /// <summary>Satellites in view with a signal, at the clock's current time.</summary>
    public int SatellitesTracked => Satellites().Count(s => s.Snr > 0);

    /// <summary>Satellites used in the fix, at the clock's current time — none, three, or every tracked one.</summary>
    public int SatellitesUsed => Phase switch
    {
        FixPhase.NoFix => 0,
        FixPhase.TwoDimensional => Math.Min(3, SatellitesTracked),
        _ => SatellitesTracked,
    };

    /// <summary>
    /// One cycle's sentences, without line endings, for the clock's current time: RMC, GGA, GSA,
    /// the GSV pages, ZDA.
    /// </summary>
    public IReadOnlyList<string> NextCycle()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        FixPhase phase = Phase;
        List<(int Prn, int Elevation, int Azimuth, int Snr)> satellites = Satellites();
        bool fixed_ = phase != FixPhase.NoFix;

        // Each sentence carries the time it was put on the wire, not the time the cycle began. With
        // the default zero spacing they are all the same instant and this is the old behaviour; with
        // a real talker's tens of milliseconds, a cycle beginning just before midnight ends after
        // it, and the sentences carrying the date disagree with the sentences carrying the time.
        int pages = PageCount(satellites.Count);
        foreach (NmeaConstellation extra in _extraConstellations)
        {
            pages += PageCount(extra.Count);
        }

        DateTimeOffset rmcAt = now;
        DateTimeOffset ggaAt = now + _sentenceSpacing;
        DateTimeOffset zdaAt = now + (_sentenceSpacing * (3 + pages));

        string time = Hhmmss(rmcAt);
        string date = rmcAt.ToString("ddMMyy", CultureInfo.InvariantCulture);
        (string lat, string ns) = fixed_ ? Latitude(_latitude) : (string.Empty, string.Empty);
        (string lon, string ew) = fixed_ ? Longitude(_longitude) : (string.Empty, string.Empty);

        List<string> cycle =
        [
            NmeaSentence.Format(_talker, "RMC", time, fixed_ ? "A" : "V", lat, ns, lon, ew, fixed_ ? "0.0" : null, fixed_ ? "0.0" : null, date, null, null),
            NmeaSentence.Format(
                _talker, "GGA", Hhmmss(ggaAt), lat, ns, lon, ew,
                fixed_ ? "1" : "0",
                SatellitesUsed.ToString("00", CultureInfo.InvariantCulture),
                fixed_ ? (phase == FixPhase.ThreeDimensional ? "1.2" : "2.4") : null,
                phase == FixPhase.ThreeDimensional ? _height.ToString("0.0", CultureInfo.InvariantCulture) : null,
                "M",
                phase == FixPhase.ThreeDimensional ? "-19.6" : null,
                "M",
                null,
                null),
            Gsa(phase, satellites),
        ];

        cycle.AddRange(Gsv(_talker, satellites));

        // Each further constellation runs its OWN GSV cycle with its own page numbering, which is
        // the shape that broke the parser's page accounting in #417.
        foreach (NmeaConstellation extra in _extraConstellations)
        {
            cycle.AddRange(Gsv(extra.Talker, ConstellationSatellites(extra)));
        }

        cycle.Add(NmeaSentence.Format(
            _talker, "ZDA", Hhmmss(zdaAt),
            zdaAt.ToString("dd", CultureInfo.InvariantCulture),
            zdaAt.ToString("MM", CultureInfo.InvariantCulture),
            zdaAt.ToString("yyyy", CultureInfo.InvariantCulture),
            "00",
            "00"));

        return cycle;
    }

    private static string Hhmmss(DateTimeOffset when) =>
        when.ToString("HHmmss", CultureInfo.InvariantCulture) + ".00";

    private static int PageCount(int satellites) =>
        (satellites + SatellitesPerPage - 1) / SatellitesPerPage;

    /// <summary>
    /// A further constellation's satellites, taken from the same sky and renumbered into its range.
    /// </summary>
    private List<(int Prn, int Elevation, int Azimuth, int Snr)> ConstellationSatellites(NmeaConstellation extra)
    {
        List<(int, int, int, int)> primary = Satellites();
        List<(int, int, int, int)> satellites = new(extra.Count);

        for (int i = 0; i < extra.Count && i < primary.Count; i++)
        {
            (_, int elevation, int azimuth, int snr) = primary[i];

            // Offset around the sky so the two constellations are not drawn on top of each other,
            // which would make a sky plot look right for the wrong reason.
            satellites.Add((extra.FirstPrn + i, elevation, (azimuth + 180) % 360, snr));
        }

        return satellites;
    }

    /// <summary>One cycle as the wire carries it: every sentence followed by CR LF.</summary>
    public string NextCycleText() => string.Concat(NextCycle().Select(sentence => sentence + "\r\n"));

    private List<(int Prn, int Elevation, int Azimuth, int Snr)> Satellites()
    {
        double seconds = (_timeProvider.GetUtcNow() - _startedAt).TotalSeconds;
        FixPhase phase = Phase;
        List<(int, int, int, int)> satellites = new(Constellation.Length);

        for (int i = 0; i < Constellation.Length; i++)
        {
            (int prn, double elevation, double azimuth, int snr) = Constellation[i];

            // A slow drift: a satellite crosses the sky in hours, so a few hundredths of a degree
            // a second is what a plot shows moving without a test having to wait.
            int drifted = (int)Math.Round((azimuth + (seconds * 0.02)) % 360);
            int lifted = (int)Math.Round(Math.Clamp(elevation + (Math.Sin(seconds / 600.0 + i) * 2), 0, 90));

            // Before the first fix the receiver is still acquiring: the strong satellites are heard,
            // the weak ones are not, and the two below the mask never are.
            int heard = snr == 0 ? 0
                : phase == FixPhase.NoFix ? (snr >= 34 ? snr - 6 : 0)
                : snr;

            satellites.Add((prn, lifted, drifted, heard));
        }

        return satellites;
    }

    private string Gsa(FixPhase phase, List<(int Prn, int Elevation, int Azimuth, int Snr)> satellites)
    {
        string mode = phase switch
        {
            FixPhase.ThreeDimensional => "3",
            FixPhase.TwoDimensional => "2",
            _ => "1",
        };

        string?[] fields = new string?[17];
        fields[0] = "A";
        fields[1] = mode;

        int used = SatellitesUsed;
        int slot = 2;
        foreach ((int prn, _, _, int snr) in satellites)
        {
            if (used == 0 || snr == 0 || slot >= 14)
            {
                continue;
            }

            fields[slot++] = prn.ToString("00", CultureInfo.InvariantCulture);
            used--;
        }

        bool fixed_ = phase != FixPhase.NoFix;
        fields[14] = fixed_ ? "2.1" : null;
        fields[15] = fixed_ ? (phase == FixPhase.ThreeDimensional ? "1.2" : "2.4") : null;
        fields[16] = fixed_ ? (phase == FixPhase.ThreeDimensional ? "1.7" : null) : null;

        return NmeaSentence.Format(_talker, "GSA", fields);
    }

    private static IEnumerable<string> Gsv(string talker, List<(int Prn, int Elevation, int Azimuth, int Snr)> satellites)
    {
        int pages = PageCount(satellites.Count);
        for (int page = 0; page < pages; page++)
        {
            List<string?> fields =
            [
                pages.ToString(CultureInfo.InvariantCulture),
                (page + 1).ToString(CultureInfo.InvariantCulture),
                satellites.Count.ToString("00", CultureInfo.InvariantCulture),
            ];

            foreach ((int prn, int elevation, int azimuth, int snr) in satellites.Skip(page * SatellitesPerPage).Take(SatellitesPerPage))
            {
                fields.Add(prn.ToString("00", CultureInfo.InvariantCulture));
                fields.Add(elevation.ToString("00", CultureInfo.InvariantCulture));
                fields.Add(azimuth.ToString("000", CultureInfo.InvariantCulture));
                fields.Add(snr == 0 ? null : snr.ToString("00", CultureInfo.InvariantCulture));
            }

            yield return NmeaSentence.Format(talker, "GSV", [.. fields]);
        }
    }

    /// <summary>Latitude as the standard writes it: <c>ddmm.mmmm</c> and a hemisphere letter.</summary>
    public static (string Value, string Hemisphere) Latitude(double degrees) =>
        (DegreesMinutes(Math.Abs(degrees), 2), degrees < 0 ? "S" : "N");

    /// <summary>Longitude as the standard writes it: <c>dddmm.mmmm</c> and a hemisphere letter.</summary>
    public static (string Value, string Hemisphere) Longitude(double degrees) =>
        (DegreesMinutes(Math.Abs(degrees), 3), degrees < 0 ? "W" : "E");

    private static string DegreesMinutes(double magnitude, int degreeDigits)
    {
        int whole = (int)magnitude;
        double minutes = (magnitude - whole) * 60;
        return whole.ToString(new string('0', degreeDigits), CultureInfo.InvariantCulture)
            + minutes.ToString("00.0000", CultureInfo.InvariantCulture);
    }
}
