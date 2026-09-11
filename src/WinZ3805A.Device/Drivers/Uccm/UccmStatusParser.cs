using System.Globalization;

using WinZ3805A.Device.Models;

namespace WinZ3805A.Device.Drivers.Uccm;

/// <summary>
/// Reads a UCCM <c>SYST:STAT?</c> reply into the common currency (#416).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not the SmartClock's 80x24 screen and it is not only the hex line either.</b> The
/// reply mixes three things: human-readable state lines (<c>SETTLING</c>, <c>WARMUP</c>,
/// <c>TFOM</c>/<c>FFOM</c>, <c>NO REF</c>, <c>WAIT FOR GPS</c>), a satellite table introduced by a
/// header carrying <c>PRN</c> and <c>EL</c> and closed by an <c>ELEV MASK</c> line, and one or more
/// <c>C5</c> hex time codes which may arrive anywhere in it. Lady Heather's
/// <c>get_uccm_status()</c> reads it as a state machine over those markers and this follows the
/// same shape.
/// </para>
/// <para>
/// Adapted from Lady Heather (heathgps.cpp), MIT licensed, © 2008-2016 Mark S. Sims.
/// </para>
/// <para>
/// <b>Half of it has now met a receiver, and the half that had not was wrong (#416).</b>
/// <c>Uccm/Captures/trimble-uccm-p-first-sitting.txt</c> is a Trimble UCCM-P tracking seven
/// satellites on 11 Sep 2026, and what it returns to <c>SYST:STAT?</c> is an <b>80-column text
/// screen</b> — not the <c>C5</c> hex line #416 predicted from Heather's <c>parse_uccm_time()</c>.
/// The C5 frames are real but unsolicited: they arrive between a reply and its prompt, which is
/// what #470 was opened for. Three things about that screen are measured rather than adapted, and
/// each has a test against the capture:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Rows end <c>0D 0A 00</c>. Read as CRLF lines, every row after the first begins with a NUL, and
/// NUL is not whitespace — so it became the first token of every satellite row and the whole table
/// was discarded as having no PRNs. A tracking receiver reported no satellites and said nothing.
/// </description></item>
/// <item><description>
/// The table is <b>two tables and a panel</b> sharing each row: <c>PRN  El  Az  C/N   PRN  El  Az</c>
/// and then the time and position readings. Splitting a row on whitespace glues one satellite's
/// numbers onto another's.
/// </description></item>
/// <item><description>
/// The screen states its own counts — <c>Tracking: 7</c>, <c>Not Tracking: 4</c> — and they are
/// cross-checked against the rows read, so a firmware whose columns sit elsewhere produces a
/// warning instead of a quietly short list.
/// </description></item>
/// </list>
/// <para>
/// Everything <i>else</i> here is still a reading of a third party's reading: the lock-state bytes,
/// the vendor tables and the time code are unconfirmed, and §11.1's fixture requirement is met for
/// one screen from one unit in one state.
/// </para>
/// <para>
/// It never throws. Everything it cannot make sense of becomes a parse warning and an absent field.
/// </para>
/// </remarks>
public static class UccmStatusParser
{
    /// <summary>Reads a status reply, given what is currently believed about the receiver.</summary>
    /// <param name="response">The whole reply, echo and terminator included.</param>
    /// <param name="capturedAt">The parse stamp, from the driver's <see cref="TimeProvider"/>.</param>
    /// <param name="profile">What the vendor and variant are believed to be.</param>
    public static ReceiverStatus Parse(string? response, DateTimeOffset capturedAt, UccmProfile profile)
    {
        List<string> warnings = [];
        List<TrackedSatellite> tracked = [];
        List<PredictedSatellite> notTracked = [];

        SmartClockMode mode = SmartClockMode.Unknown;
        string? modeDetail = null;
        int? tfom = null;
        int? ffom = null;
        int? elevationMask = null;
        UccmTimeCode? time = null;
        bool inSatelliteTable = false;
        bool sawAnything = false;

        ScreenPanel panel = ScreenPanel.None;
        InfoPanel info = new();
        int? reportedTracking = null;
        int? reportedNotTracking = null;
        bool onePpsValid = false;

        foreach (string raw in SplitLines(response))
        {
            // COLUMNS ARE THE LAYOUT, so nothing is trimmed off the front before they are read: a
            // UCCM-P terminates each screen row `0D 0A 00`, which leaves every row after the first
            // starting with a NUL once the terminator is read as CRLF. `Trim` does not remove it —
            // NUL is not whitespace — so `parts[0]` was the NUL and every satellite row was
            // discarded as having no PRN, which is why a tracking receiver showed no satellites
            // (#416, measured 11 Sep 2026). Blanking controls to spaces fixes the parse and keeps
            // every column where the receiver put it.
            string row = BlankControlCharacters(raw);
            string line = row.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (UccmTimeCode.TryParse(line) is UccmTimeCode code)
            {
                // Last one wins: a status reply long enough to contain two is one during which the
                // clock advanced, and the later reading is the truer one.
                time = code;
                sawAnything = true;
                continue;
            }

            if (UccmReply.Classify(line, sent: null) is UccmLineKind.Complete or UccmLineKind.Error)
            {
                inSatelliteTable = false;
                continue;
            }

            string upper = line.ToUpperInvariant();

            if (inSatelliteTable)
            {
                if (upper.Contains("ELEV", StringComparison.Ordinal) &&
                    upper.Contains("MASK", StringComparison.Ordinal))
                {
                    inSatelliteTable = false;
                    elevationMask = FirstInteger(line);
                    continue;
                }

                // The right-hand panel runs alongside the table rather than after it, so it is read
                // from the same rows before the satellite columns are.
                info.Read(panel.Slice(row, ScreenColumn.Info));

                if (TryReadSatellite(panel.Slice(row, ScreenColumn.Tracked)) is TrackedSatellite satellite)
                {
                    tracked.Add(satellite);
                }

                if (TryReadSatellite(panel.Slice(row, ScreenColumn.NotTracked)) is TrackedSatellite predicted)
                {
                    notTracked.Add(new PredictedSatellite
                    {
                        Prn = predicted.Prn,
                        ElevationDegrees = predicted.ElevationDegrees,
                        AzimuthDegrees = predicted.AzimuthDegrees,
                    });
                }

                continue;
            }

            if (upper.Contains("PRN", StringComparison.Ordinal) && upper.Contains("EL", StringComparison.Ordinal))
            {
                inSatelliteTable = true;
                sawAnything = true;

                // TWO TABLES SIDE BY SIDE, AND A PANEL BESIDE BOTH. The header is
                // `PRN  El  Az  C/N   PRN  El  Az` and the rows carry a tracked satellite, a
                // not-tracked one, and an unrelated line of the time/position panel — so a reader
                // that splits a row on whitespace gets one satellite with another's numbers glued
                // to it. The header's own column positions say where each block starts, which is
                // the only description of the layout the receiver actually gives us.
                panel = ScreenPanel.FromHeader(row);
                info.Read(panel.Slice(row, ScreenColumn.Info));
                continue;
            }

            if (upper.Contains("TRACKING", StringComparison.Ordinal))
            {
                // `Tracking: 7 ____   Not Tracking: 4 ________   Time ___`. Read for the
                // cross-check below rather than for display: the rows are the authority, and a
                // count that disagrees with them means the column arithmetic has met a firmware
                // that lays the screen out differently.
                reportedNotTracking = IntegerAfter(upper, "NOT TRACKING");
                reportedTracking = upper.IndexOf("NOT TRACKING", StringComparison.Ordinal) is 0
                    ? null
                    : IntegerAfter(upper, "TRACKING");
                sawAnything = true;
                continue;
            }

            if (upper.Contains("1PPS VALID", StringComparison.Ordinal))
            {
                // Literal, and the only unambiguous statement of validity on the screen: the
                // receiver prints `[GPS 1PPS Valid]` when the pulse is good.
                onePpsValid = true;
                modeDetail ??= LeadingWords(line);
                sawAnything = true;
                continue;
            }

            if (upper.Contains("TFOM", StringComparison.Ordinal))
            {
                tfom = IntegerAfter(upper, "TFOM");
                ffom = IntegerAfter(upper, "FFOM");
                sawAnything = true;
                continue;
            }

            if (upper.Contains("SETTLING", StringComparison.Ordinal))
            {
                mode = SmartClockMode.Recovery;
                modeDetail = "Settling";
                sawAnything = true;
            }
            else if (upper.Contains("WARMUP", StringComparison.Ordinal))
            {
                mode = SmartClockMode.PowerUp;
                modeDetail = "Warming up";
                sawAnything = true;
            }
            else if (upper.Contains("NO REF", StringComparison.Ordinal) ||
                     upper.Contains("WAIT FOR GPS", StringComparison.Ordinal))
            {
                // Heather maps both of these to its acquiring mode. We have no acquiring member,
                // and PowerUp is the nearest honest one: the receiver is not disciplined and is
                // waiting for GPS, which is what PowerUp describes.
                mode = SmartClockMode.PowerUp;
                modeDetail = upper.Contains("NO REF", StringComparison.Ordinal)
                    ? "No reference"
                    : "Waiting for GPS";
                sawAnything = true;
            }
        }

        // The hex line is the authority on lock, because the text lines only ever say what is
        // WRONG - there is no "LOCKED" line to match. So a reply with a healthy lock byte and no
        // complaint is a locked receiver.
        if (time is not null && mode == SmartClockMode.Unknown)
        {
            mode = ModeFromLockState(time.LockState, profile.Vendor, out string? detail);
            modeDetail ??= detail;
        }

        if (!sawAnything)
        {
            warnings.Add(
                "No UCCM status marker was found in the reply - no time code, no figures of merit, " +
                "no satellite table. The response was either not a status reply or is in a format " +
                "this driver has not met.");
        }

        if (time is not null && !profile.VendorKnown && time.SuggestedVendor == UccmVendor.Unknown)
        {
            warnings.Add(
                $"Vendor is not established and the status bytes do not suggest one " +
                $"(lock {time.LockState:X2}, date {time.DateValidityState:X2}). Vendor-specific " +
                "readings are reported as absent until DIAG:LOOP? settles it.");
        }

        // The rows are the authority and the header line is the cross-check, not the other way
        // round: a firmware that lays the columns out differently produces too few satellites
        // rather than wrong ones, and this is what says so out loud instead of quietly under-
        // reporting a sky full of them.
        if (reportedTracking is int expectedTracking && expectedTracking != tracked.Count)
        {
            warnings.Add(
                $"The screen says {expectedTracking} satellite(s) are tracked and {tracked.Count} " +
                "row(s) could be read. The satellite table's columns are not where this driver " +
                "expects them, so some rows have been dropped.");
        }

        if (reportedNotTracking is int expectedNotTracking && expectedNotTracking != notTracked.Count)
        {
            warnings.Add(
                $"The screen says {expectedNotTracking} satellite(s) are not tracked and " +
                $"{notTracked.Count} row(s) could be read.");
        }

        return new ReceiverStatus
        {
            Mode = mode,
            ModeDetail = modeDetail,
            Tfom = tfom,
            Ffom = ffom,
            Tracked = tracked,
            NotTracked = notTracked,
            ElevationMaskDegrees = elevationMask,
            SignalStrengthKind = SignalStrengthKind.CarrierToNoise,

            // What the screen labelled its own time, rather than an assumption that it is UTC. The
            // time code, when there is one, is the fallback and is UTC by construction.
            TimeScale = info.Time is not null ? info.TimeScale : TimeScale.Utc,
            DeviceDateTime = info.Time ?? time?.UtcTime,

            // Either statement of validity will do, and the text one is the measured half: a
            // UCCM-P prints `[GPS 1PPS Valid]` whether or not a time code was in the reply.
            GpsOnePpsValid = onePpsValid || (time?.AntennaOk == true && mode == SmartClockMode.Locked),
            AntennaDelayNanoseconds = info.AntennaDelayNanoseconds,
            PositionMode = info.PositionMode,
            PositionQualifier = info.PositionMode == PositionMode.Hold
                ? PositionQualifier.Held
                : PositionQualifier.Unknown,
            Position = info.Position,
            HeightDatum = info.HeightDatum,
            LeapPending = time?.LeapPending == true ? LeapSecondPending.Plus : LeapSecondPending.None,
            CapturedAt = capturedAt,
            ParseWarnings = warnings,
        };
    }

    /// <summary>
    /// Turns the manufacturer-dependent lock byte into a mode (#418).
    /// </summary>
    /// <remarks>
    /// <b>The whole point of #418 in one method.</b> Symmetricom says <c>85</c> for locked and
    /// Trimble says <c>45</c>; a driver that knows only one reports the other's healthy receiver as
    /// unlocked, which the medallion would render as a fault. When the vendor is not yet known the
    /// low nibble is used on its own — it is common to both vendors in every value Heather
    /// records — and that is a documented guess rather than a fact.
    /// </remarks>
    private static SmartClockMode ModeFromLockState(int lockState, UccmVendor vendor, out string? detail)
    {
        detail = null;

        (int locked, int settling, int powerUp) = vendor switch
        {
            UccmVendor.Symmetricom => (0x85, 0x8F, -1),
            UccmVendor.Trimble => (0x45, 0x4F, 0x41),
            _ => (-1, -1, -1),
        };

        if (lockState == locked)
        {
            return SmartClockMode.Locked;
        }

        if (lockState == settling)
        {
            detail = "Settling";
            return SmartClockMode.Recovery;
        }

        if (lockState == powerUp)
        {
            detail = "Powering up";
            return SmartClockMode.PowerUp;
        }

        // Vendor unknown, or a byte neither vendor's table names. The low nibble carries the state
        // in every value Heather recorded, for both vendors - 5 locked, F settling, 1 powering up.
        return (lockState & 0x0F) switch
        {
            0x05 => SmartClockMode.Locked,
            0x0F => Detailed(ref detail, "Settling", SmartClockMode.Recovery),
            0x01 => Detailed(ref detail, "Powering up", SmartClockMode.PowerUp),
            _ => SmartClockMode.Unknown,
        };
    }

    private static SmartClockMode Detailed(ref string? detail, string text, SmartClockMode mode)
    {
        detail = text;
        return mode;
    }

    /// <summary>
    /// Reads one row of the satellite table.
    /// </summary>
    /// <remarks>
    /// The column layout differs across firmware — Heather tracks whether a signal-strength column
    /// is present via an <c>SS</c> marker in the header — so this reads positionally from the
    /// leading numbers rather than by fixed columns, and gives up on a row it cannot make sense of
    /// instead of guessing.
    /// </remarks>
    private static TrackedSatellite? TryReadSatellite(string line)
    {
        string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int prn) ||
            prn is <= 0 or > 210)
        {
            return null;
        }

        return new TrackedSatellite
        {
            Prn = prn,
            ElevationDegrees = Number(parts, 1),
            AzimuthDegrees = Number(parts, 2),
            SignalStrength = Number(parts, 3),
        };
    }

    /// <summary>
    /// One column as a whole number, rounded from a decimal where the receiver prints one.
    /// </summary>
    /// <remarks>
    /// The model carries elevation, azimuth and signal strength as integers, which is the
    /// resolution the SmartClock family prints. Rounding rather than refusing keeps a firmware that
    /// prints one decimal place readable; the lost tenth of a degree is below what the sky plot can
    /// draw.
    /// </remarks>
    private static int? Number(string[] parts, int index) =>
        index < parts.Length &&
        double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
        double.IsFinite(value) &&
        value is >= int.MinValue and <= int.MaxValue
            ? (int)Math.Round(value, MidpointRounding.AwayFromZero)
            : null;

    private static int? IntegerAfter(string text, string marker)
    {
        int at = text.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? null : FirstInteger(text[(at + marker.Length)..]);
    }

    private static int? FirstInteger(string text)
    {
        int at = 0;
        while (at < text.Length && !char.IsAsciiDigit(text[at]))
        {
            at++;
        }

        int end = at;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        return end > at &&
               int.TryParse(text.AsSpan(at, end - at), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;
    }

    /// <summary>Which of the three blocks a screen row is divided into (#416).</summary>
    private enum ScreenColumn
    {
        /// <summary>The tracked table: PRN, elevation, azimuth, C/N.</summary>
        Tracked,

        /// <summary>The not-tracked table beside it: PRN, elevation, azimuth.</summary>
        NotTracked,

        /// <summary>The time and position panel to the right of both.</summary>
        Info,
    }

    /// <summary>
    /// Where each block of the satellite table starts, read from the header row (#416).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on a Trimble UCCM-P, whose header is
    /// <c>PRN  El  Az  C/N   PRN  El  Az</c> followed by the time panel's own headings. Two PRN
    /// columns and a panel share every row of the table, so the block boundaries are the only way
    /// to read a row without gluing one satellite's numbers onto another's.
    /// </para>
    /// <para>
    /// <b>Read from the screen rather than hard-coded</b>, because Heather records that the column
    /// layout differs across firmware — and because a fixed offset that is wrong produces confident
    /// nonsense, where a boundary taken from the header either works or leaves the blocks empty and
    /// trips the count cross-check.
    /// </para>
    /// </remarks>
    private readonly record struct ScreenPanel(int TrackedAt, int NotTrackedAt, int InfoAt)
    {
        /// <summary>Nothing located yet: every slice is the whole row, which is the old behaviour.</summary>
        public static ScreenPanel None { get; } = new(0, -1, -1);

        public static ScreenPanel FromHeader(string header)
        {
            int first = header.IndexOf("PRN", StringComparison.OrdinalIgnoreCase);
            if (first < 0)
            {
                return None;
            }

            int second = header.IndexOf("PRN", first + 3, StringComparison.OrdinalIgnoreCase);
            if (second < 0)
            {
                // One table, and whatever else is on the row belongs to the panel. The last column
                // heading of a single table ends where its run of headings does.
                return new ScreenPanel(first, -1, EndOfHeadings(header, first));
            }

            return new ScreenPanel(first, second, EndOfHeadings(header, second));
        }

        /// <summary>One block of a row, or an empty span when this screen has no such block.</summary>
        public string Slice(string row, ScreenColumn column)
        {
            (int from, int to) = column switch
            {
                ScreenColumn.Tracked => (TrackedAt, NotTrackedAt >= 0 ? NotTrackedAt : InfoAt),
                ScreenColumn.NotTracked => (NotTrackedAt, InfoAt),
                _ => (InfoAt, -1),
            };

            if (from < 0 || from >= row.Length)
            {
                return string.Empty;
            }

            int end = to < 0 || to > row.Length ? row.Length : to;
            return end <= from ? string.Empty : row[from..end];
        }

        /// <summary>
        /// Where the headings starting at <paramref name="at"/> stop and the next panel begins.
        /// </summary>
        /// <remarks>
        /// A run of headings is separated by one or two spaces; the panel beside it is separated by
        /// a wide gap. Three spaces is the smallest gap that is not inside the run itself — the
        /// header measured here has <c>C/N   PRN</c> with exactly three.
        /// </remarks>
        private static int EndOfHeadings(string header, int at)
        {
            const int gap = 4;

            for (int index = at; index + gap <= header.Length; index++)
            {
                if (header.AsSpan(index, gap).IsWhiteSpace())
                {
                    int next = index;
                    while (next < header.Length && header[next] == ' ')
                    {
                        next++;
                    }

                    return next >= header.Length ? -1 : next;
                }
            }

            return -1;
        }
    }

    /// <summary>
    /// The time and position panel that runs down the right of the satellite table (#416).
    /// </summary>
    /// <remarks>
    /// Every field is read from its own label, so a row that carries none of them contributes
    /// nothing and a screen that omits one leaves it absent. Measured labels, from a Trimble
    /// UCCM-P: <c>GPS hh:mm:ss dd Mon yyyy</c>, <c>ANT DLY n ns</c>, <c>MODE Hold</c>,
    /// <c>LAT S dd:mm:ss.sss</c>, <c>LON E ddd:mm:ss.sss</c>, <c>HGT +n.nn m (MSL)</c>.
    /// </remarks>
    private sealed class InfoPanel
    {
        private int? _year;
        private int? _month;
        private int? _day;
        private TimeSpan? _timeOfDay;

        public TimeScale TimeScale { get; private set; }

        public double? AntennaDelayNanoseconds { get; private set; }

        public PositionMode PositionMode { get; private set; }

        public HeightDatum HeightDatum { get; private set; }

        public double? Latitude { get; private set; }

        public double? Longitude { get; private set; }

        public double? Height { get; private set; }

        public GeoPosition? Position => Latitude is null && Longitude is null && Height is null
            ? null
            : new GeoPosition
            {
                LatitudeDegrees = Latitude,
                LongitudeDegrees = Longitude,
                HeightMetres = Height,
            };

        public DateTimeOffset? Time => _year is int year && _month is int month && _day is int day &&
                                       _timeOfDay is TimeSpan clock
            ? new DateTimeOffset(year, month, day, clock.Hours, clock.Minutes, clock.Seconds, TimeSpan.Zero)
            : null;

        public void Read(string slice)
        {
            string text = slice.Trim();
            if (text.Length == 0)
            {
                return;
            }

            if (TryReadClock(text))
            {
                return;
            }

            if (text.StartsWith("ANT DLY", StringComparison.OrdinalIgnoreCase))
            {
                AntennaDelayNanoseconds = FirstNumber(text["ANT DLY".Length..]);
                return;
            }

            if (text.StartsWith("MODE", StringComparison.OrdinalIgnoreCase))
            {
                string value = text["MODE".Length..].Trim();
                PositionMode =
                    value.StartsWith("HOLD", StringComparison.OrdinalIgnoreCase) ? PositionMode.Hold :
                    value.StartsWith("SURV", StringComparison.OrdinalIgnoreCase) ? PositionMode.Survey :
                    PositionMode.Unknown;
                return;
            }

            if (text.StartsWith("LAT", StringComparison.OrdinalIgnoreCase))
            {
                Latitude = ReadAngle(text["LAT".Length..], 'S');
                return;
            }

            if (text.StartsWith("LON", StringComparison.OrdinalIgnoreCase))
            {
                Longitude = ReadAngle(text["LON".Length..], 'W');
                return;
            }

            if (text.StartsWith("HGT", StringComparison.OrdinalIgnoreCase))
            {
                Height = FirstNumber(text["HGT".Length..]);
                HeightDatum =
                    text.Contains("MSL", StringComparison.OrdinalIgnoreCase) ? HeightDatum.Msl :
                    text.Contains("GPS", StringComparison.OrdinalIgnoreCase) ? HeightDatum.GpsEllipsoid :
                    HeightDatum.Unknown;
            }
        }

        /// <summary>
        /// <c>GPS      01:40:39     11 Sep 2026</c>, or the same row labelled UTC.
        /// </summary>
        /// <remarks>
        /// The label is kept rather than assumed: GPS time and UTC differ by the leap seconds, and
        /// a reading that says which it is can be corrected later, where one that silently claims
        /// UTC cannot.
        /// </remarks>
        private bool TryReadClock(string text)
        {
            TimeScale scale =
                text.StartsWith("GPS", StringComparison.OrdinalIgnoreCase) ? TimeScale.Gps :
                text.StartsWith("UTC", StringComparison.OrdinalIgnoreCase) ? TimeScale.Utc :
                TimeScale.Unknown;

            if (scale == TimeScale.Unknown)
            {
                return false;
            }

            string[] parts = text[3..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1 || !TimeSpan.TryParseExact(parts[0], @"hh\:mm\:ss", CultureInfo.InvariantCulture, out TimeSpan clock))
            {
                return false;
            }

            TimeScale = scale;
            _timeOfDay = clock;

            if (parts.Length >= 4 &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int day) &&
                DateTime.TryParseExact(parts[2], "MMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime month) &&
                int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int year))
            {
                _day = day;
                _month = month.Month;
                _year = year;
            }

            return true;
        }

        /// <summary>
        /// <c>S  34:32:39.019</c> as signed decimal degrees, negative in <paramref name="negative"/>.
        /// </summary>
        private static double? ReadAngle(string text, char negative)
        {
            string value = text.Trim();
            if (value.Length == 0)
            {
                return null;
            }

            int sign = 1;
            if (char.IsLetter(value[0]))
            {
                sign = char.ToUpperInvariant(value[0]) == char.ToUpperInvariant(negative) ? -1 : 1;
                value = value[1..].Trim();
            }

            string[] parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double degrees) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            {
                return null;
            }

            return sign * (degrees + (minutes / 60d) + (seconds / 3600d));
        }

        private static double? FirstNumber(string text)
        {
            foreach (string token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
                    double.IsFinite(value))
                {
                    return value;
                }
            }

            return null;
        }
    }

    /// <summary>The words before the first run of dots or the first bracket, for a state line.</summary>
    private static string? LeadingWords(string line)
    {
        int end = line.IndexOfAny(['.', '[']);
        string words = (end < 0 ? line : line[..end]).Trim();
        return words.Length == 0 ? null : words;
    }

    /// <summary>
    /// Every control character as a space, so column positions survive (#416).
    /// </summary>
    /// <remarks>
    /// A UCCM-P ends each screen row <c>0D 0A 00</c>. Read as CRLF-delimited lines that leaves a
    /// NUL at the head of every row but the first, and NUL is not whitespace — so it neither trims
    /// away nor splits, and it became the first token of every satellite row.
    /// </remarks>
    private static string BlankControlCharacters(string line)
    {
        if (!line.Any(char.IsControl))
        {
            return line;
        }

        return string.Create(line.Length, line, static (destination, source) =>
        {
            for (int index = 0; index < source.Length; index++)
            {
                destination[index] = char.IsControl(source[index]) ? ' ' : source[index];
            }
        });
    }

    private static IEnumerable<string> SplitLines(string? response) =>
        string.IsNullOrEmpty(response)
            ? []
            : response.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
}
