using System.Globalization;

using WinZ3805A.Device.Models;

namespace WinZ3805A.Device.Drivers.Nmea;

/// <summary>
/// Reads one NMEA 0183 cycle — RMC, GGA, GSA, the GSV pages, ZDA — into the common currency (#310).
/// </summary>
/// <remarks>
/// <para>
/// This is the broadcast family's counterpart to <c>StatusScreenParser</c>: the same §11.1
/// contract (never throw; an unreadable field is null and the reason is a warning), against a
/// different shape of input. A SmartClock says everything in one screen; a talker says it across
/// several sentences, so the parser is handed the whole of the last complete cycle
/// (<see cref="PollPlan.WholeCycle"/>) and reads each sentence for what it carries.
/// </para>
/// <para>
/// <b>What NMEA does not say is left unsaid.</b> A GPS talker has no disciplined oscillator, so
/// TFOM, FFOM, the 1 PPS time interval, holdover, EFC, the antenna delay and the health monitor
/// are all absent here — null, or the enum's <c>Unknown</c> — and the pages show them as em
/// dashes. The two judgements this parser does make are stated so they can be argued with: a fix
/// means the receiver's 1 PPS is valid, which is what a GPS timing receiver's fix means; and the
/// time is provisional until there is a fix, because before one a module's clock is whatever it
/// last had.
/// </para>
/// </remarks>
public static class NmeaStatusParser
{
    /// <summary>Reads a cycle's sentences. Never throws.</summary>
    /// <param name="response">The cycle's lines, one sentence per line, or anything else.</param>
    /// <param name="capturedAt">When the cycle was read, for provenance.</param>
    public static ReceiverStatus Parse(string? response, DateTimeOffset capturedAt)
    {
        List<string> warnings = [];
        try
        {
            return ParseCore(response, capturedAt, warnings);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The last-resort catch §11.1 asks for. Nothing above should throw; this makes sure
            // that if something does, the poll loop sees a status with a warning rather than dying.
            warnings.Add($"the NMEA parser failed unexpectedly: {exception.GetType().Name}: {exception.Message}");
            return new ReceiverStatus { CapturedAt = capturedAt, ParseWarnings = warnings };
        }
    }

    private static ReceiverStatus ParseCore(string? response, DateTimeOffset capturedAt, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            warnings.Add("no sentences were heard");
            return new ReceiverStatus { CapturedAt = capturedAt, ParseWarnings = warnings };
        }

        NmeaSentence? rmc = null;
        NmeaSentence? gga = null;
        NmeaSentence? gsa = null;
        NmeaSentence? zda = null;
        List<NmeaSentence> gsv = [];

        foreach (string line in response.Split('\n'))
        {
            NmeaSentence? sentence = NmeaSentence.TryParse(line);
            if (sentence is null)
            {
                continue;
            }

            if (!sentence.ChecksumValid)
            {
                warnings.Add($"a {sentence.Identifier} sentence failed its checksum and was ignored");
                continue;
            }

            switch (sentence.Identifier)
            {
                case "RMC":
                    rmc = sentence;
                    break;
                case "GGA":
                    gga = sentence;
                    break;
                case "GSA":
                    gsa = sentence;
                    break;
                case "ZDA":
                    zda = sentence;
                    break;
                case "GSV":
                    gsv.Add(sentence);
                    break;
                default:
                    break;
            }
        }

        if (rmc is null && gga is null)
        {
            warnings.Add("the cycle carried neither an RMC nor a GGA sentence, so there is no fix data");
        }

        int quality = ParseInt(gga?.Field(5)) ?? (rmc?.Field(1) == "A" ? 1 : 0);
        bool hasFix = quality > 0;
        string? gsaMode = gsa?.Field(1);

        (IReadOnlyList<TrackedSatellite> tracked, IReadOnlyList<PredictedSatellite> notTracked) = Satellites(gsv, warnings);
        DateTimeOffset? time = Time(rmc, gga, zda, warnings);
        GeoPosition? position = Position(gga, rmc, hasFix, warnings);

        return new ReceiverStatus
        {
            ModeDetail = ModeDetail(quality, gsaMode),
            GpsOnePpsValid = hasFix,
            Tracked = tracked,
            NotTracked = notTracked,
            SignalStrengthKind = SignalStrengthKind.CarrierToNoise,
            TimeScale = TimeScale.Utc,
            DeviceDateTime = time,
            DeviceTimeIsProvisional = !hasFix,
            WeekRolloverEpochs = 0,
            CorrectedDateTime = time,
            Position = position,
            HeightDatum = position?.HeightMetres is null ? HeightDatum.Unknown : HeightDatum.Msl,
            CapturedAt = capturedAt,
            ParseWarnings = warnings,
        };
    }

    /// <summary>The words for the fix, as the GGA quality indicator and the GSA mode give them.</summary>
    public static string ModeDetail(int quality, string? gsaMode)
    {
        string fix = quality switch
        {
            0 => "no fix",
            1 => "GPS fix",
            2 => "differential GPS fix",
            _ => $"fix (quality {quality})",
        };

        return gsaMode switch
        {
            "2" when quality > 0 => fix + " (2D)",
            "3" when quality > 0 => fix + " (3D)",
            _ => fix,
        };
    }

    private static (IReadOnlyList<TrackedSatellite>, IReadOnlyList<PredictedSatellite>) Satellites(List<NmeaSentence> pages, List<string> warnings)
    {
        List<TrackedSatellite> tracked = [];
        List<PredictedSatellite> inView = [];
        HashSet<int> seen = [];

        foreach (NmeaSentence page in pages)
        {
            // Fields 0-2 are the page count, the page number and the total in view; then up to
            // four groups of PRN, elevation, azimuth, SNR. A group's SNR is blank when the
            // satellite is in view but not being tracked.
            for (int index = 3; index + 3 < page.Fields.Count + 1 && index < page.Fields.Count; index += 4)
            {
                int? prn = ParseInt(page.Field(index));
                if (prn is null || !seen.Add(prn.Value))
                {
                    continue;
                }

                int? elevation = ParseInt(page.Field(index + 1));
                int? azimuth = ParseInt(page.Field(index + 2));
                int? snr = ParseInt(page.Field(index + 3));

                if (snr is int strength && strength > 0)
                {
                    tracked.Add(new TrackedSatellite
                    {
                        Prn = prn.Value,
                        ElevationDegrees = elevation,
                        AzimuthDegrees = azimuth,
                        SignalStrength = strength,
                    });
                }
                else
                {
                    inView.Add(new PredictedSatellite
                    {
                        Prn = prn.Value,
                        ElevationDegrees = elevation,
                        AzimuthDegrees = azimuth,
                    });
                }
            }
        }

        if (pages.Count > 0)
        {
            int? declaredPages = ParseInt(pages[0].Field(0));
            if (declaredPages is int expected && pages.Count != expected)
            {
                warnings.Add($"the cycle carried {pages.Count} GSV page(s) of {expected}");
            }
        }

        return (tracked, inView);
    }

    private static DateTimeOffset? Time(NmeaSentence? rmc, NmeaSentence? gga, NmeaSentence? zda, List<string> warnings)
    {
        // ZDA carries the whole date; RMC carries a two-digit year; GGA carries time alone. The
        // best available wins, and a two-digit year is read as this century - a GPS module's own
        // week-rollover handling is its firmware's business, and one that has it wrong reports a
        // date this parser cannot correct without knowing which module it is.
        string? hhmmss = zda?.Field(0) ?? rmc?.Field(0) ?? gga?.Field(0);
        if (hhmmss is null || hhmmss.Length < 6)
        {
            return null;
        }

        int? year = ParseInt(zda?.Field(3));
        int? month = ParseInt(zda?.Field(2));
        int? day = ParseInt(zda?.Field(1));

        if (year is null && rmc?.Field(8) is { Length: 6 } ddmmyy)
        {
            day = ParseInt(ddmmyy[..2]);
            month = ParseInt(ddmmyy[2..4]);
            year = ParseInt(ddmmyy[4..]) is int yy ? 2000 + yy : null;
        }

        if (year is null || month is null || day is null)
        {
            warnings.Add("the cycle carried a time but no date");
            return null;
        }

        int? hour = ParseInt(hhmmss[..2]);
        int? minute = ParseInt(hhmmss[2..4]);
        int? second = ParseInt(hhmmss[4..6]);
        if (hour is null || minute is null || second is null)
        {
            warnings.Add($"the time field \"{hhmmss}\" could not be read");
            return null;
        }

        try
        {
            return new DateTimeOffset(year.Value, month.Value, day.Value, hour.Value, minute.Value, Math.Min(second.Value, 59), TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            warnings.Add($"the date {year}-{month}-{day} {hhmmss} is not a calendar date");
            return null;
        }
    }

    private static GeoPosition? Position(NmeaSentence? gga, NmeaSentence? rmc, bool hasFix, List<string> warnings)
    {
        if (!hasFix)
        {
            return null;
        }

        NmeaSentence? source = gga ?? rmc;
        int first = gga is not null ? 1 : 2;
        if (source is null)
        {
            return null;
        }

        double? latitude = Angle(source.Field(first), source.Field(first + 1), degreeDigits: 2);
        double? longitude = Angle(source.Field(first + 2), source.Field(first + 3), degreeDigits: 3);
        double? height = gga is not null ? ParseDouble(gga.Field(8)) : null;

        if (latitude is null && longitude is null)
        {
            warnings.Add("the fix carried no position");
            return null;
        }

        return new GeoPosition
        {
            LatitudeDegrees = latitude,
            LongitudeDegrees = longitude,
            HeightMetres = height,
        };
    }

    /// <summary>
    /// The standard's <c>ddmm.mmmm</c> / <c>dddmm.mmmm</c> with a hemisphere letter, as signed
    /// decimal degrees: south and west negative.
    /// </summary>
    public static double? Angle(string? value, string? hemisphere, int degreeDigits)
    {
        if (value is null || value.Length <= degreeDigits || hemisphere is null)
        {
            return null;
        }

        int? degrees = ParseInt(value[..degreeDigits]);
        double? minutes = ParseDouble(value[degreeDigits..]);
        if (degrees is null || minutes is null)
        {
            return null;
        }

        double unsigned = degrees.Value + (minutes.Value / 60.0);
        return hemisphere is "S" or "W" ? -unsigned : unsigned;
    }

    private static int? ParseInt(string? field) =>
        int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

    private static double? ParseDouble(string? field) =>
        double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && !double.IsNaN(value) && !double.IsInfinity(value)
            ? value
            : null;
}
