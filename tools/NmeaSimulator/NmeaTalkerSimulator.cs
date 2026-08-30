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
/// sentences, no lock or holdover state — NMEA has none — and no serial quirks. The BG7TBL's own
/// behaviour is #309's to capture, and this is the thing that capture will be compared against.
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

    /// <summary>Creates a receiver that starts cold at the clock's current time.</summary>
    /// <param name="timeProvider">The clock every sentence's time comes from.</param>
    /// <param name="talker">The talker identifier — <c>GP</c> (GPS) by default, <c>GN</c> for a multi-constellation receiver.</param>
    /// <param name="latitudeDegrees">The antenna's latitude, positive north.</param>
    /// <param name="longitudeDegrees">The antenna's longitude, positive east.</param>
    /// <param name="heightMetres">The antenna's height above mean sea level.</param>
    /// <param name="fixAfter">How long after start the first (2D) fix arrives.</param>
    /// <param name="threeDimensionalAfter">How long after start the fix becomes 3D.</param>
    public NmeaTalkerSimulator(
        TimeProvider timeProvider,
        string talker = "GP",
        double latitudeDegrees = 47.6205,
        double longitudeDegrees = -122.3493,
        double heightMetres = 56.0,
        TimeSpan? fixAfter = null,
        TimeSpan? threeDimensionalAfter = null)
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
    }

    /// <summary>The talker identifier every sentence carries.</summary>
    public string Talker => _talker;

    /// <summary>Where the receiver is in its start-up, at the clock's current time.</summary>
    public FixPhase Phase
    {
        get
        {
            TimeSpan elapsed = _timeProvider.GetUtcNow() - _startedAt;
            return elapsed >= _threeDimensionalAfter ? FixPhase.ThreeDimensional
                : elapsed >= _fixAfter ? FixPhase.TwoDimensional
                : FixPhase.NoFix;
        }
    }

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

        string time = now.ToString("HHmmss", CultureInfo.InvariantCulture) + ".00";
        string date = now.ToString("ddMMyy", CultureInfo.InvariantCulture);
        (string lat, string ns) = fixed_ ? Latitude(_latitude) : (string.Empty, string.Empty);
        (string lon, string ew) = fixed_ ? Longitude(_longitude) : (string.Empty, string.Empty);

        List<string> cycle =
        [
            NmeaSentence.Format(_talker, "RMC", time, fixed_ ? "A" : "V", lat, ns, lon, ew, fixed_ ? "0.0" : null, fixed_ ? "0.0" : null, date, null, null),
            NmeaSentence.Format(
                _talker, "GGA", time, lat, ns, lon, ew,
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

        cycle.AddRange(Gsv(satellites));
        cycle.Add(NmeaSentence.Format(
            _talker, "ZDA", time,
            now.ToString("dd", CultureInfo.InvariantCulture),
            now.ToString("MM", CultureInfo.InvariantCulture),
            now.ToString("yyyy", CultureInfo.InvariantCulture),
            "00",
            "00"));

        return cycle;
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

    private IEnumerable<string> Gsv(List<(int Prn, int Elevation, int Azimuth, int Snr)> satellites)
    {
        int pages = (satellites.Count + SatellitesPerPage - 1) / SatellitesPerPage;
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

            yield return NmeaSentence.Format(_talker, "GSV", [.. fields]);
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
