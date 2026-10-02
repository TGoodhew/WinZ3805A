using System.Text;

using WinZ3805A.Simulation.SmartClock;

namespace WinZ3805A.Tests.SmartClock;

/// <summary>
/// The simulator's screen writer against every captured screen of the bench Z3805A (#639).
/// </summary>
/// <remarks>
/// <para>
/// <b>Bytes, not readings.</b> Each test states the values a capture shows, by hand, and requires the
/// writer to print the capture exactly: every space, every underscore, every CRLF. The application's
/// parser finds satellite columns by position, so a screen that merely parses to the right readings
/// could still be one the real receiver never prints, and the parser's tests would then be checking
/// it against a fiction.
/// </para>
/// <para>
/// The values are typed out rather than read through the application's parser on purpose. The
/// simulator is meant to be a second reading of the receiver, independent of the first, so that
/// running the parser against it means something; deriving these from the parser would make the two
/// agree by construction.
/// </para>
/// </remarks>
public sealed class StatusScreenWriterTests
{
    private static readonly PositionPanel Original = new()
    {
        Latitude = Lat(47, 31, 18.822),
        Longitude = -Lat(122, 12, 22.152),
        Height = 38.00,
    };

    private static readonly PositionPanel Surveyed = new()
    {
        Latitude = Lat(47, 31, 18.582),
        Longitude = -Lat(122, 12, 22.092),
        Height = 25.20,
    };

    private static readonly string[] CaptureNames =
    [
        "locked-stabilizing.txt",
        "captured/locked-to-gps.txt",
        "captured/locked-to-gps-stabilizing-frequency.txt",
        "captured/locked-to-gps-sub-unit-readings.txt",
        "captured/power-up-gps-acquisition.txt",
        "captured/power-up-fine-freq-adj.txt",
        "captured/surveying-locked-to-gps-stabilizing-frequency.txt",
        "captured/holdover-gps-1pps-invalid.txt",
        "captured/holdover-gps-1pps-invalid-deep.txt",
        "captured/holdover-gps-1pps-invalid-3.txt",
        "captured/recovery-fine-freq-adj.txt",
    ];

    public static TheoryData<string> Captures => new(CaptureNames);

    [Theory]
    [MemberData(nameof(Captures))]
    public void TheWriterReproducesTheCaptureByteForByte(string name)
    {
        string expected = Screen(name);
        string actual = StatusScreenWriter.Write(SnapshotOf(name));

        // Line by line first, so a failure names the line rather than an offset in 2 kB.
        string[] want = expected.Split("\r\n");
        string[] got = actual.Split("\r\n");
        for (int i = 0; i < Math.Min(want.Length, got.Length); i++)
        {
            Assert.True(want[i] == got[i], $"{name} line {i + 1}:\nwant [{want[i]}]\ngot  [{got[i]}]");
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EveryCaptureHasASnapshot()
    {
        // A capture added to the corpus without a snapshot here would be a screen shape nobody has
        // checked the writer against. FixtureCorpusTests globs the folder; so does this.
        string root = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        string[] onDisk = [.. Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];

        string[] covered = [.. CaptureNames.Order(StringComparer.Ordinal)];

        Assert.Equal(onDisk, covered);
    }

    [Fact]
    public void AFailingHealthItemPrintsErrAndTheSummarySaysError()
    {
        // The manual's form (p. 3-18). Never captured, so this pins the guess rather than the receiver.
        string screen = StatusScreenWriter.Write(SnapshotOf("captured/locked-to-gps.txt") with
        {
            Health = new HealthPanel { Ocxo = false },
        });

        string[] lines = screen.Split("\r\n");
        Assert.Equal("HEALTH MONITOR ...................................................... [ Error ]", lines[25]);
        Assert.Equal("Self Test: OK    Int Pwr: OK   Oven Pwr: OK   OCXO: Err  EFC: OK   GPS Rcv: OK ", lines[26]);
        Assert.All(lines[..^1], line => Assert.True(line.Length <= 79, $"[{line}] is {line.Length} wide"));
    }

    /// <summary>The screen bytes of a capture, without the stray reply one of them carries after it.</summary>
    private static string Screen(string name)
    {
        string text = Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

        // power-up-gps-acquisition.txt has a late *IDN? answer as its 28th line, kept as the device's
        // own bytes (Fixtures/README.md). It is not part of the screen.
        int stray = text.IndexOf("scpi > ", StringComparison.Ordinal);
        return stray < 0 ? text : text[..stray];
    }

    private static double Lat(int degrees, int minutes, double seconds) => degrees + (minutes / 60.0) + (seconds / 3600);

    private static TrackedSatellite T(int prn, int el, int az, int cn) => new(prn, el, az, cn);

    private static UntrackedSatellite U(int prn, int el, int az, bool attempting = false) => new(prn, el, az, attempting);

    private static ScreenSnapshot SnapshotOf(string name) => name switch
    {
        "locked-stabilizing.txt" => new()
        {
            Outputs = OutputsSummary.ValidReducedAccuracy,
            Mode = ClockMode.Locked,
            ModeDetail = "stabilizing frequency",
            Tfom = 3,
            Ffom = 1,
            TimeIntervalNanoseconds = -5.4,
            PredictMicroseconds = 2.5,
            GpsOnePpsValid = true,
            Tracked = [T(18, 79, 2, 32)],
            NotTracked = [U(5, 25, 50), U(10, 21, 219), U(15, 42, 108), U(16, 31, 290), U(20, 31, 68), U(23, 53, 215), U(26, 26, 256), U(27, 15, 311), U(29, 41, 143)],
            Time = new DateTime(2006, 12, 27, 14, 45, 2),
            AntennaDelayNanoseconds = 77,
            Position = Original,
        },

        "captured/locked-to-gps.txt" => new()
        {
            Outputs = OutputsSummary.Valid,
            Mode = ClockMode.Locked,
            Tfom = 3,
            Ffom = 0,
            TimeIntervalNanoseconds = 49.8,
            PredictMicroseconds = 2.8,
            GpsOnePpsValid = true,
            Tracked = [T(2, 18, 67, 33), T(7, 29, 116, 34), T(14, 84, 316, 38), T(15, 25, 309, 37), T(17, 41, 171, 33), T(19, 12, 191, 34), T(20, 47, 281, 41), T(22, 67, 256, 37), T(30, 69, 116, 33)],
            NotTracked = [U(1, 11, 92), U(8, 14, 42)],
            Time = new DateTime(2007, 1, 12, 3, 52, 20),
            AntennaDelayNanoseconds = 77,
            Position = Original,
        },

        "captured/locked-to-gps-stabilizing-frequency.txt" => new()
        {
            Outputs = OutputsSummary.ValidReducedAccuracy,
            Mode = ClockMode.Locked,
            ModeDetail = "stabilizing frequency",
            Tfom = 3,
            Ffom = 1,
            TimeIntervalNanoseconds = -20.9,
            PredictMicroseconds = 2.0,
            GpsOnePpsValid = true,
            Tracked = [T(7, 41, 108, 37), T(8, 24, 47, 32), T(14, 75, 253, 37), T(15, 16, 318, 33), T(17, 28, 175, 34), T(20, 42, 297, 37), T(22, 55, 242, 40), T(30, 81, 88, 38)],
            NotTracked = [U(2, 14, 79), U(5, 10, 267)],
            Time = new DateTime(2007, 1, 12, 3, 24, 19),
            AntennaDelayNanoseconds = 77,
            Position = Original,
        },

        "captured/locked-to-gps-sub-unit-readings.txt" => new()
        {
            // Taken by the comparison with the simulator, 2 Oct 2026: both readings a unit down.
            Outputs = OutputsSummary.Valid,
            Mode = ClockMode.Locked,
            Tfom = 3,
            Ffom = 0,
            TimeIntervalNanoseconds = 0.3,
            PredictMicroseconds = 0.8,
            GpsOnePpsValid = true,
            Tracked = [T(1, 47, 222, 39), T(2, 29, 205, 36), T(3, 59, 307, 41), T(4, 28, 259, 36), T(26, 15, 134, 34), T(28, 51, 64, 38), T(31, 65, 123, 39), T(32, 21, 71, 35)],
            NotTracked = [U(17, 5, 291), U(19, 5, 321), U(25, 11, 39)],
            Time = new DateTime(2007, 2, 16, 17, 55, 17),
            AntennaDelayNanoseconds = 60,
            Position = new() { Latitude = Lat(47, 31, 18.546), Longitude = -Lat(122, 12, 22.128), Height = 38.00 },
        },

        "captured/power-up-gps-acquisition.txt" => new()
        {
            Outputs = OutputsSummary.Invalid,
            Mode = ClockMode.PowerUp,
            ModeDetail = "GPS acquisition",
            Tfom = 9,
            Ffom = 3,
            GpsOnePpsValid = false,
            NotTracked = [U(1, 21, 59), U(2, 13, 36), U(14, 57, 70), U(15, 29, 271, true), U(17, 73, 123), U(19, 47, 194, true), U(20, 34, 235, true), U(22, 75, 40, true), U(24, 27, 307, true), U(30, 34, 139)],
            TimeScale = "GPS",
            Time = new DateTime(2007, 1, 12, 5, 10, 4),
            TimeProvisional = true,
            ClockAdvisory = "Invalid: not tracking",
            AntennaDelayNanoseconds = 0,
            Position = Original,
            ElevationMaskDegrees = 0,
        },

        "captured/power-up-fine-freq-adj.txt" => new()
        {
            Outputs = OutputsSummary.Invalid,
            Mode = ClockMode.PowerUp,
            ModeDetail = "fine freq adj",
            ModeTimeIntervalNanoseconds = 108.5,
            Tfom = 9,
            Ffom = 3,
            GpsOnePpsValid = true,
            Tracked = [T(14, 57, 70, 32), T(15, 29, 271, 37), T(17, 73, 123, 35), T(19, 47, 194, 35), T(20, 34, 235, 36), T(22, 75, 40, 34), T(24, 27, 307, 36), T(30, 34, 139, 33)],
            NotTracked = [U(1, 21, 59), U(2, 13, 36)],
            Time = new DateTime(2007, 1, 12, 5, 10, 26),
            TimeProvisional = true,
            AntennaDelayNanoseconds = 77,
            Position = new() { SurveyPercent = 0.3, Latitude = Lat(47, 31, 18.749), Longitude = -Lat(122, 12, 22.145), Height = 35.80 },
        },

        "captured/surveying-locked-to-gps-stabilizing-frequency.txt" => new()
        {
            Outputs = OutputsSummary.ValidReducedAccuracy,
            Mode = ClockMode.Locked,
            ModeDetail = "stabilizing frequency",
            Tfom = 4,
            Ffom = 1,
            TimeIntervalNanoseconds = -22.9,
            PredictMicroseconds = 432.0,
            GpsOnePpsValid = true,
            Tracked = [T(1, 21, 58, 31), T(14, 56, 71, 35), T(15, 29, 270, 37), T(17, 73, 119, 36), T(19, 48, 194, 33), T(20, 33, 234, 37), T(22, 74, 44, 33), T(24, 28, 307, 35)],
            NotTracked = [U(2, 12, 35), U(30, 33, 139)],
            Time = new DateTime(2007, 1, 12, 5, 12, 20),
            AntennaDelayNanoseconds = 77,
            Position = new() { SurveyPercent = 1.9, Latitude = Lat(47, 31, 18.640), Longitude = -Lat(122, 12, 22.168), Height = 30.47 },
        },

        "captured/holdover-gps-1pps-invalid.txt" => Holdover(
            TimeSpan.FromSeconds(3),
            predict: 6.8,
            ffomTime: new DateTime(2007, 1, 12, 15, 52, 54),
            notTracked: [U(2, 8, 302), U(8, 32, 303), U(10, 75, 286), U(15, 19, 46), U(18, 32, 115), U(23, 63, 51), U(24, 19, 78), U(27, 51, 257), U(32, 31, 187)]),

        "captured/holdover-gps-1pps-invalid-deep.txt" => Holdover(
            new TimeSpan(0, 11, 34),
            predict: 5.6,
            ffomTime: new DateTime(2007, 1, 12, 16, 4, 25),
            notTracked: [U(2, 12, 304), U(8, 34, 298), U(10, 78, 306), U(15, 14, 45), U(18, 27, 118), U(23, 59, 55), U(24, 20, 73), U(27, 49, 249), U(32, 36, 187)]),

        "captured/holdover-gps-1pps-invalid-3.txt" => Holdover(
            new TimeSpan(0, 16, 52),
            predict: 5.6,
            ffomTime: new DateTime(2007, 1, 12, 16, 9, 43),
            notTracked: [U(15, 12, 45), U(18, 25, 120), U(23, 56, 57)]) with
        {
            // The antenna back, the mode not yet changed: the GPS side recovers first.
            GpsOnePpsValid = true,
            ClockAdvisory = "Synchronized to UTC",
            Tracked = [T(2, 14, 304, 38), T(8, 35, 295, 38), T(10, 79, 320, 41), T(24, 21, 71, 36), T(27, 48, 245, 39), T(32, 39, 187, 39)],
        },

        "captured/recovery-fine-freq-adj.txt" => new()
        {
            Outputs = OutputsSummary.ValidReducedAccuracy,
            Mode = ClockMode.Recovery,
            ModeDetail = "fine freq adj",
            ModeTimeIntervalNanoseconds = -17.0,
            Tfom = 3,
            Ffom = 2,
            TimeIntervalNanoseconds = -17.9,
            PredictMicroseconds = 5.6,
            PresentMicroseconds = 1.0,
            HoldoverDuration = new TimeSpan(0, 17, 6),
            GpsOnePpsValid = true,
            Tracked = [T(2, 14, 304, 38), T(8, 35, 295, 38), T(10, 79, 320, 41), T(23, 56, 57, 36), T(24, 21, 70, 36), T(27, 48, 245, 38), T(32, 39, 187, 39)],
            NotTracked = [U(15, 12, 45), U(18, 25, 120)],
            Time = new DateTime(2007, 1, 12, 16, 9, 57),
            AntennaDelayNanoseconds = 77,
            Position = Surveyed,
        },

        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No snapshot for this capture."),
    };

    private static ScreenSnapshot Holdover(TimeSpan held, double predict, DateTime ffomTime, IReadOnlyList<UntrackedSatellite> notTracked) => new()
    {
        Outputs = OutputsSummary.ValidReducedAccuracy,
        Mode = ClockMode.Holdover,
        ModeDetail = "GPS 1PPS invalid",
        Tfom = 3,
        Ffom = 2,
        PredictMicroseconds = predict,
        PresentMicroseconds = 1.0,
        HoldoverDuration = held,
        GpsOnePpsValid = false,
        NotTracked = notTracked,
        Time = ffomTime,
        ClockAdvisory = "Invalid: not tracking",
        AntennaDelayNanoseconds = 77,
        Position = Surveyed,
    };
}
