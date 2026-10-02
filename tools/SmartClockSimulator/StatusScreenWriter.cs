using System.Globalization;
using System.Text;

namespace WinZ3805A.Simulation.SmartClock;

/// <summary>
/// Prints a <see cref="ScreenSnapshot"/> the way the bench Z3805A prints <c>:SYST:STAT?</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every width here was measured, not chosen.</b> The receiver's screen is 27 lines of fixed
/// columns: a left panel 46 characters wide and a right panel after it, each line ending CRLF and
/// carrying its trailing spaces. The application's parser finds the satellite columns by where the
/// header row puts them, so a column one character out is a different screen, not a cosmetic slip.
/// <c>StatusScreenWriterTests</c> rebuilds every capture under <c>tests/WinZ3805A.Tests/Fixtures/</c>
/// from its values and requires identical bytes.
/// </para>
/// <para>
/// Where a field has only ever been seen in one shape, the other shapes are guesses and are marked
/// so at the point they are made: microsecond time intervals, a holdover longer than 99 minutes, a
/// day of the month below ten, and the failing health line. The bench unit settles each of them the
/// first time it shows one.
/// </para>
/// </remarks>
public static class StatusScreenWriter
{
    /// <summary>The width of every full line.</summary>
    private const int Width = 79;

    /// <summary>Where the right-hand panel starts.</summary>
    private const int RightColumn = 46;

    /// <summary>How many rows the satellite tables share with the time and position panels.</summary>
    private const int TableRows = 12;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Prints the screen: 27 lines, each ending CRLF, with no prompt.</summary>
    public static string Write(ScreenSnapshot screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        StringBuilder text = new(2200);
        void Line(string line) => text.Append(line).Append("\r\n");
        void Row(string left, string right) => Line(left.PadRight(RightColumn) + right);

        Line("------------------------------- Receiver Status -------------------------------");
        Line(Banner("SYNCHRONIZATION", OutputsText(screen.Outputs)));
        Line("SmartClock Mode ___________________________   Reference Outputs _______________");

        Row(ModeRow(screen, ClockMode.Locked), $"TFOM     {screen.Tfom}             FFOM     {screen.Ffom}");
        Row(ModeRow(screen, ClockMode.Recovery), TimeIntervalLine(screen.TimeIntervalNanoseconds));
        Row(ModeRow(screen, ClockMode.Holdover), "HOLD THR " + screen.HoldThresholdMicroseconds.ToString("0.000", Invariant) + " us");
        Row(ModeRow(screen, ClockMode.PowerUp), "Holdover Uncertainty ____________");
        Row(string.Empty, screen.PredictMicroseconds is double predict
            ? "Predict  " + Uncertainty(predict) + "/initial 24 hrs"
            : "Predict  --");

        // The one row whose right panel is not empty when it has nothing to say: with no Present
        // figure the bench unit prints two spaces there, so the line is 48 characters, not 46. Seen
        // on every capture taken outside holdover.
        Row(
            screen.HoldoverDuration is TimeSpan held ? "                 Holdover Duration: " + Duration(held) : string.Empty,
            screen.PresentMicroseconds is double present ? "Present  " + Uncertainty(present) : "  ");

        Line(Banner("ACQUISITION", screen.GpsOnePpsValid ? "GPS 1PPS Valid" : "GPS 1PPS Invalid"));
        Line(
            ("Tracking: " + screen.Tracked.Count.ToString(Invariant) + " ").PadRight(16, '_') + "   " +
            ("Not Tracking: " + screen.NotTracked.Count.ToString(Invariant) + " ").PadRight(24, '_') + "   " +
            "Time ____________________________");

        string header = (screen.Tracked.Count > 0 ? "PRN  El  Az  C/N   " : new string(' ', 19)) + "PRN  El  Az";
        Row(header, TimeLine(screen));

        string[] right = RightPanel(screen);
        for (int row = 0; row < TableRows; row++)
        {
            Row(SatelliteRow(screen, row), right[row]);
        }

        bool attempting = screen.NotTracked.Any(s => s.Attempting);
        Row(
            "ELEV MASK " + screen.ElevationMaskDegrees.ToString(Invariant).PadLeft(2) + " deg" + (attempting ? "   *attempting to track" : string.Empty),
            string.Empty);

        // The failing forms are the manual's (p. 3-18) and have never been captured. Each item keeps
        // its column, so a three-letter Err takes one of the spaces after it.
        HealthPanel health = screen.Health;
        Line(Banner("HEALTH MONITOR", health.AllOk ? "OK" : "Error"));
        Line(
            Item("Self Test", health.SelfTest, 17) +
            Item("Int Pwr", health.InternalPower, 14) +
            Item("Oven Pwr", health.OvenPower, 15) +
            Item("OCXO", health.Ocxo, 11) +
            Item("EFC", health.Efc, 10) +
            Item("GPS Rcv", health.GpsReceiver, 12));

        return text.ToString();
    }

    /// <summary>A section heading: the name, a run of dots, and the verdict in brackets, 79 wide.</summary>
    private static string Banner(string name, string verdict)
    {
        string bracket = "[ " + verdict + " ]";
        return name + " " + new string('.', Width - name.Length - bracket.Length - 2) + " " + bracket;
    }

    private static string OutputsText(OutputsSummary outputs) => outputs switch
    {
        OutputsSummary.Valid => "Outputs Valid",
        OutputsSummary.ValidReducedAccuracy => "Outputs Valid/Reduced Accuracy",
        _ => "Outputs Invalid",
    };

    /// <summary>One of the four mode rows, with the marker and detail only on the active one.</summary>
    private static string ModeRow(ScreenSnapshot screen, ClockMode mode)
    {
        bool active = screen.Mode == mode;
        string label = mode switch
        {
            // "Locked" alone when inactive, "Locked to GPS" when active: both captured.
            ClockMode.Locked => active ? "Locked to GPS" : "Locked",
            ClockMode.Recovery => "Recovery",
            ClockMode.Holdover => "Holdover",
            _ => "Power-up",
        };

        if (!active)
        {
            return "   " + label;
        }

        string text = screen.ModeDetail is null ? label : label + ": " + screen.ModeDetail;
        if (screen.ModeTimeIntervalNanoseconds is double ti)
        {
            // "Power-up: fine freq adj   [TI +108.5 ns]" and "Recovery: fine freq adj   [TI  -17.0 ns]":
            // the bracket starts at a fixed column and the value is right-aligned in six.
            text = text.PadRight(26) + "[TI " + Interval(ti).PadLeft(9) + "]";
        }

        return ">> " + text;
    }

    /// <summary>
    /// A time interval in the unit that keeps it at one or more: <c>+300 ps</c>, <c>+108.5 ns</c>,
    /// <c>+1.296 us</c>.
    /// </summary>
    /// <remarks>
    /// The receiver works to 0.1 ns. Below a nanosecond it prints whole picoseconds, which the bench
    /// unit did on 2 Oct 2026 (<c>+300 ps</c>, <c>locked-to-gps-sub-unit-readings.txt</c>); from there
    /// up, nanoseconds to one decimal, as every other capture shows. The microsecond form is the
    /// manual's sample screen (<c>[TI +1.296 us]</c>, p. 3-20) and has not been seen on a wire.
    /// </remarks>
    private static string Interval(double nanoseconds) => Math.Abs(nanoseconds) switch
    {
        >= 1000 => Signed(nanoseconds / 1000, "0.000") + " us",
        >= 1 => Signed(nanoseconds, "0.0") + " ns",
        _ => Signed(Math.Round(nanoseconds * 1000), "0") + " ps",
    };

    /// <summary>
    /// A holdover uncertainty: <c>2.5 us</c> to one decimal, or whole nanoseconds below a microsecond
    /// (<c>800 ns</c>, seen on the bench unit 2 Oct 2026).
    /// </summary>
    private static string Uncertainty(double microseconds) => microseconds >= 1
        ? microseconds.ToString("0.0", Invariant) + " us"
        : Math.Round(microseconds * 1000).ToString("0", Invariant) + " ns";

    private static string TimeIntervalLine(double? nanoseconds) =>
        nanoseconds is double ti ? "1PPS TI " + Interval(ti) + " relative to GPS" : "1PPS TI  --";

    private static string Signed(double value, string format) =>
        (value < 0 ? "-" : "+") + Math.Abs(value).ToString(format, Invariant);

    /// <summary>
    /// <c> 0m 03s</c>, <c>17m 06s</c>: minutes right-aligned in two, seconds zero-padded.
    /// </summary>
    /// <remarks>
    /// Captured up to 17 minutes. What the receiver prints past 99 minutes is unknown; this keeps
    /// counting minutes, which is the least surprising guess and is flagged as one.
    /// </remarks>
    private static string Duration(TimeSpan held)
    {
        long minutes = (long)held.TotalMinutes;
        return minutes.ToString(Invariant).PadLeft(2) + "m " + held.Seconds.ToString("00", Invariant) + "s";
    }

    /// <summary><c>UTC      03:52:20     12 Jan 2007</c>, or with <c>(?)</c> in place of four spaces.</summary>
    /// <remarks>The day is zero-padded below ten; no capture has shown one.</remarks>
    private static string TimeLine(ScreenSnapshot screen) =>
        screen.TimeScale.PadRight(9) +
        screen.Time.ToString("HH:mm:ss", Invariant) +
        (screen.TimeProvisional ? " (?) " : "     ") +
        screen.Time.ToString("dd MMM yyyy", Invariant);

    /// <summary>The right panel's twelve rows beside the satellite tables.</summary>
    private static string[] RightPanel(ScreenSnapshot screen)
    {
        PositionPanel position = screen.Position;
        bool surveying = position.SurveyPercent is not null;
        string qualifier = surveying ? "AVG " : string.Empty;

        string[] rows = new string[TableRows];
        Array.Fill(rows, string.Empty);
        rows[0] = "GPS 1PPS " + screen.ClockAdvisory;
        rows[1] = "ANT DLY  " + screen.AntennaDelayNanoseconds.ToString(Invariant) + " ns";
        rows[2] = "Position ________________________";
        rows[3] = position.SurveyPercent is double percent
            ? "MODE     Survey: " + percent.ToString("0.0", Invariant).PadLeft(6) + "% complete"
            : "MODE     Hold";
        rows[5] = (qualifier + "LAT").PadRight(9) + Angle(position.Latitude, 'N', 'S');
        rows[6] = (qualifier + "LON").PadRight(9) + Angle(position.Longitude, 'E', 'W');
        rows[7] = (qualifier + "HGT").PadRight(9) +
            Signed(position.Height, "0.00").PadLeft(15) + " m  (" + position.HeightDatum + ")";
        return rows;
    }

    /// <summary><c>N  47:31:18.822</c>: hemisphere, degrees right-aligned in three, then minutes and seconds.</summary>
    private static string Angle(double degrees, char positive, char negative)
    {
        long milliseconds = (long)Math.Round(Math.Abs(degrees) * 3_600_000);
        long whole = milliseconds / 3_600_000;
        long minutes = milliseconds / 60_000 % 60;
        double seconds = milliseconds % 60_000 / 1000.0;
        return (degrees < 0 ? negative : positive) + " " +
            whole.ToString(Invariant).PadLeft(3) + ":" +
            minutes.ToString("00", Invariant) + ":" +
            seconds.ToString("00.000", Invariant);
    }

    /// <summary>One row of the two satellite tables, side by side.</summary>
    private static string SatelliteRow(ScreenSnapshot screen, int row)
    {
        string tracked = new(' ', 16);
        if (row < screen.Tracked.Count)
        {
            TrackedSatellite t = screen.Tracked[row];
            tracked = Field(t.Prn, 3) + Field(t.Elevation, 4) + Field(t.Azimuth, 4) + Field(t.CarrierToNoise, 5);
        }

        if (row >= screen.NotTracked.Count)
        {
            return row < screen.Tracked.Count ? tracked : string.Empty;
        }

        UntrackedSatellite u = screen.NotTracked[row];
        string prn = (u.Attempting ? "*" : string.Empty) + u.Prn.ToString(Invariant);
        return tracked + prn.PadLeft(6) + Field(u.Elevation, 4) + Field(u.Azimuth, 4);
    }

    private static string Field(int value, int width) => value.ToString(Invariant).PadLeft(width);

    private static string Item(string label, bool ok, int width) =>
        (label + ": " + (ok ? "OK" : "Err")).PadRight(width);
}
