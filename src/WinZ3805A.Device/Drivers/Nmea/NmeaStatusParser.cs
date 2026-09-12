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
        NmeaSentence? gns = null;
        NmeaSentence? gsa = null;
        NmeaSentence? zda = null;
        List<NmeaSentence> gsv = [];
        List<NmeaSentence> txt = [];

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
                case "GNS":
                    gns = sentence;
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
                case "TXT":
                    txt.Add(sentence);
                    break;
                default:
                    break;
            }
        }

        if (rmc is null && gga is null && gns is null)
        {
            warnings.Add("the cycle carried no RMC, GGA or GNS sentence, so there is no fix data");
        }

        // GGA first, because its single digit is the most specific thing on the wire; then GNS,
        // whose mode string says as much and per constellation; then RMC's valid/void flag, which
        // can only say whether there is a fix at all (#429).
        string? gnsMode = gns?.Field(5);
        int quality = ParseInt(gga?.Field(5))
            ?? GnsQuality(gnsMode)
            ?? (rmc?.Field(1) == "A" ? 1 : 0);

        bool hasFix = quality > 0;
        string? gsaMode = gsa?.Field(1);

        (IReadOnlyList<TrackedSatellite> tracked, IReadOnlyList<PredictedSatellite> notTracked) = Satellites(gsv, warnings);
        DateTimeOffset? time = Time(rmc, gga, zda, warnings);
        GeoPosition? position = Position(gga, gns, rmc, hasFix, warnings);

        return new ReceiverStatus
        {
            // The constellation count comes from GNS alone. A GGA quality digit cannot say how many
            // systems are contributing, and GSA's system id could be made to, but that is a second
            // source for one fact and this parser has already been bitten by taking two sentences'
            // word for one thing - see Time, and the 24-hour error it used to build at midnight.
            ModeDetail = ModeDetail(quality, gsaMode, gga is null ? ContributingSystems(gnsMode) : 1),
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
            Dop = Dop(gsa),

            // GGA only. GNS has no satellites-used field, and inventing one from the GSA slots would
            // be counting a different thing — those are the satellites offered to the solution, not
            // the ones it kept.
            SatellitesUsed = ParseInt(gga?.Field(6)),
            DifferentialAgeSeconds = ParseDouble(gga?.Field(12)),
            DifferentialStationId = Text(gga?.Field(13)),
            Banner = Banner(txt),
            CapturedAt = capturedAt,
            ParseWarnings = warnings,
        };
    }

    /// <summary>
    /// The three dilution figures from a <c>GSA</c> sentence, or <see langword="null"/> if none
    /// parsed (#435).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fields 15, 16 and 17 in the standard's one-based numbering, which is 14, 15 and 16 here — the
    /// twelve satellite slots occupy 2 through 13 whether or not they are filled, so the offsets are
    /// fixed and an empty slot does not shift them. A receiver that truncates the empty slots would
    /// break this, and none of the three on the bench does; a warning is not raised for it because a
    /// short <c>GSA</c> would fail its checksum first.
    /// </para>
    /// <para>
    /// Returns <see langword="null"/> rather than an empty record when nothing parsed, so "the
    /// receiver did not say" and "the receiver said nothing useful" do not have to be told apart
    /// downstream.
    /// </para>
    /// </remarks>
    private static DilutionOfPrecision? Dop(NmeaSentence? gsa)
    {
        if (gsa is null)
        {
            return null;
        }

        DilutionOfPrecision dop = new()
        {
            Position = ParseDouble(gsa.Field(14)),
            Horizontal = ParseDouble(gsa.Field(15)),
            Vertical = ParseDouble(gsa.Field(16)),
        };

        return dop.IsEmpty ? null : dop;
    }

    /// <summary>
    /// What a cycle's <c>$GxTXT</c> sentences say about the receiver itself (#515).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>TXT</c> is four fields — total messages, this message's number, a type, and free text — and
    /// it is the free text that carries everything. u-blox writes <c>KEY=value</c> for most of it and
    /// a bare <c>HW UBX-M8130 00080000</c> for the hardware, so both shapes are read.
    /// </para>
    /// <para>
    /// <b>Only the notice type is trusted.</b> Field 2 is <c>00</c> error, <c>01</c> warning,
    /// <c>02</c> notice, <c>07</c> user. The banner is a notice; an error-class <c>TXT</c> is the
    /// receiver complaining about something and filing it as though it were an identity would be
    /// reading a fault as a fact. Anything that is not a notice is ignored here and left to whoever
    /// wants to surface faults.
    /// </para>
    /// <para>
    /// Returns <see langword="null"/> when nothing usable was said, so an ordinary cycle — which
    /// carries no <c>TXT</c> at all — does not overwrite a banner already remembered.
    /// </para>
    /// </remarks>
    private static TalkerBanner? Banner(List<NmeaSentence> txt)
    {
        const string Notice = "02";

        TalkerBanner banner = TalkerBanner.None;
        foreach (NmeaSentence sentence in txt)
        {
            if (sentence.Field(2) != Notice || Text(sentence.Field(3)) is not string said)
            {
                continue;
            }

            banner = banner.MergedWith(BannerLine(said));
        }

        return banner.IsEmpty ? null : banner;
    }

    /// <summary>One line of a talker's banner.</summary>
    private static TalkerBanner? BannerLine(string said)
    {
        if (said.StartsWith("ANTSTATUS=", StringComparison.OrdinalIgnoreCase))
        {
            return new TalkerBanner { Antenna = Antenna(said["ANTSTATUS=".Length..]) };
        }

        if (said.StartsWith("FWVER=", StringComparison.OrdinalIgnoreCase))
        {
            return new TalkerBanner { Firmware = Text(said["FWVER=".Length..]) };
        }

        if (said.StartsWith("PROTVER=", StringComparison.OrdinalIgnoreCase))
        {
            return new TalkerBanner { ProtocolVersion = Text(said["PROTVER=".Length..]) };
        }

        // "HW UBX-M8130 00080000" — the part number, without the ROM checksum that follows it. The
        // checksum identifies a build rather than a receiver and would make every comparison unique.
        if (said.StartsWith("HW ", StringComparison.OrdinalIgnoreCase))
        {
            string rest = said[3..].Trim();
            int space = rest.IndexOf(' ', StringComparison.Ordinal);
            return new TalkerBanner { Hardware = Text(space < 0 ? rest : rest[..space]) };
        }

        return null;
    }

    /// <summary>u-blox's <c>ANTSTATUS</c> words.</summary>
    private static AntennaState Antenna(string value) => value.Trim().ToUpperInvariant() switch
    {
        "OK" => AntennaState.Ok,
        "INIT" => AntennaState.Initialising,
        "SHORT" => AntennaState.ShortCircuit,
        "OPEN" => AntennaState.OpenCircuit,
        "DONTKNOW" => AntennaState.DoNotKnow,
        _ => AntennaState.Unknown,
    };

    /// <summary>An NMEA field as text, with an empty field read as absent.</summary>
    private static string? Text(string? field) =>
        string.IsNullOrWhiteSpace(field) ? null : field.Trim();

    /// <summary>The words for the fix, as the GGA quality indicator and the GSA mode give them.</summary>
    /// <param name="quality">The GGA quality indicator, or its equivalent from <see cref="GnsQuality"/>.</param>
    /// <param name="gsaMode">GSA's fix mode — <c>2</c> for 2D, <c>3</c> for 3D.</param>
    /// <param name="systems">
    /// How many constellations are contributing, where that is known. One — the default — keeps the
    /// wording the GGA path has always produced.
    /// </param>
    /// <remarks>
    /// <b>"GPS fix" becomes "GNSS fix" when more than one constellation is contributing (#429).</b>
    /// Only GNS says so, and calling a GPS + BeiDou fix a GPS fix is simply wrong. The phrase gets
    /// shorter rather than longer, which matters: this is the medallion's sub-line, on the window
    /// §9.1 designs to be glanceable. <b>Which</b> constellations are contributing is not put here —
    /// the Satellites page names each satellite's constellation since #424, and that is the page for
    /// the question.
    /// </remarks>
    public static string ModeDetail(int quality, string? gsaMode, int systems = 1)
    {
        string system = systems > 1 ? "GNSS" : "GPS";
        string fix = quality switch
        {
            0 => "no fix",
            1 => $"{system} fix",
            2 => $"differential {system} fix",
            _ => $"fix (quality {quality})",
        };

        return gsaMode switch
        {
            "2" when quality > 0 => fix + " (2D)",
            "3" when quality > 0 => fix + " (3D)",
            _ => fix,
        };
    }

    /// <summary>
    /// GNS's per-constellation mode string read as a GGA quality indicator, or
    /// <see langword="null"/> when there is no mode string to read (#429).
    /// </summary>
    /// <remarks>
    /// <para>
    /// GNS field 5 carries one character per constellation, in the standard's order — GPS, GLONASS,
    /// Galileo, BeiDou, QZSS. The VK-162 sends <c>DN</c> and the forM8N <c>ANNN</c>, which is the
    /// whole of what this project has observed; the rest of the alphabet is the standard's.
    /// </para>
    /// <para>
    /// <b>The best contributing constellation wins, by the precedence below rather than by the
    /// numeric quality.</b> Mapping each letter to its GGA equivalent and taking the maximum reads
    /// well until a receiver reports estimated dead reckoning (6) beside a differential fix (2), and
    /// answers that the fix is the dead-reckoned one. The order here is trustworthiness, which is
    /// what the answer is asked for.
    /// </para>
    /// <para>
    /// This is why 720 GNS sentences from the VK-162 change what the window says: every one of them
    /// reads <c>DN</c> — GPS differential, GLONASS no fix — and with GNS unparsed the fix quality
    /// fell back to RMC's valid flag, which can only say 1. The receiver was reporting a
    /// differential fix and the application was calling it a plain one.
    /// </para>
    /// </remarks>
    /// <param name="mode">GNS's mode string, or <see langword="null"/>.</param>
    public static int? GnsQuality(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return null;
        }

        // Trustworthiness first, and the GGA quality each letter corresponds to second.
        foreach ((char letter, int quality) in (ReadOnlySpan<(char, int)>)
            [('R', 4), ('F', 5), ('P', 3), ('D', 2), ('A', 1), ('E', 6), ('M', 7), ('S', 8)])
        {
            if (mode.IndexOf(letter, StringComparison.Ordinal) >= 0)
            {
                return quality;
            }
        }

        // Every constellation said N, or said something this revision of the standard does not
        // define. Either way no constellation is contributing, which is a fix of quality 0 and not
        // an absent answer - so it is returned rather than deferred to RMC.
        return 0;
    }

    /// <summary>How many constellations GNS says are contributing — a mode character that is not <c>N</c>.</summary>
    /// <param name="mode">GNS's mode string, or <see langword="null"/>.</param>
    public static int ContributingSystems(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return 0;
        }

        int systems = 0;
        foreach (char letter in mode)
        {
            if (letter is not ('N' or 'n') && !char.IsWhiteSpace(letter))
            {
                systems++;
            }
        }

        return systems;
    }

    /// <summary>
    /// Which constellation a GSV page's talker says a satellite belongs to (#424).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The talker is the only thing in a GSV page that says this — the sentence carries no system
    /// field, and NMEA 4.11's trailing signal id names a frequency rather than a constellation. So
    /// <c>GN</c>, which a few receivers use for GSV, is answered
    /// <see cref="SatelliteConstellation.Unknown"/>: it means "combined", and guessing from the
    /// number would be inventing the very attribution this method exists to read.
    /// </para>
    /// <para>
    /// <b>The one number-based rule is the standard's own.</b> NMEA reserves 33–64 for satellite-based
    /// augmentation inside the GPS talker, and that is not a hypothesis here: four of the VK-162
    /// captures in the corpus carry <c>$GPGSV</c> satellites 46 and 48, which are WAAS. Numbers
    /// outside that range in a <c>GP</c> page are left as GPS rather than guessed at, because a
    /// receiver using raw SBAS numbering is one nobody here has seen.
    /// </para>
    /// </remarks>
    /// <param name="talker">The sentence's two-letter talker.</param>
    /// <param name="prn">The satellite number, which decides SBAS inside the GPS talker.</param>
    public static SatelliteConstellation ConstellationFor(string? talker, int prn) => talker switch
    {
        "GP" => prn is >= 33 and <= 64 ? SatelliteConstellation.Sbas : SatelliteConstellation.Gps,
        "GL" => SatelliteConstellation.Glonass,
        "GA" => SatelliteConstellation.Galileo,
        "GB" or "BD" => SatelliteConstellation.BeiDou,
        "GQ" => SatelliteConstellation.Qzss,
        "GI" => SatelliteConstellation.NavIC,
        _ => SatelliteConstellation.Unknown,
    };

    private static (IReadOnlyList<TrackedSatellite>, IReadOnlyList<PredictedSatellite>) Satellites(List<NmeaSentence> pages, List<string> warnings)
    {
        List<TrackedSatellite> tracked = [];
        List<PredictedSatellite> inView = [];

        // KEYED BY CONSTELLATION AND NUMBER, NOT BY NUMBER ALONE (#424). This was a HashSet<int>,
        // and against a receiver that numbers per constellation the second claimant of a number was
        // dropped - measured at 1,217 of 1,800 cycles in form8n-gps-beidou-outdoors.nmea, where GPS
        // 4 and BeiDou 4 are in view together. The user-visible symptom was the bad part: the sky
        // plot and the satellite count under-reported, which reads as poor reception rather than as
        // a parsing choice, and sends someone onto the roof.
        HashSet<SatelliteId> seen = [];

        foreach (NmeaSentence page in pages)
        {
            // Fields 0-2 are the page count, the page number and the total in view; then up to
            // four groups of PRN, elevation, azimuth, SNR. A group's SNR is blank when the
            // satellite is in view but not being tracked.
            for (int index = 3; index + 3 < page.Fields.Count + 1 && index < page.Fields.Count; index += 4)
            {
                int? prn = ParseInt(page.Field(index));
                if (prn is null)
                {
                    continue;
                }

                SatelliteConstellation constellation = ConstellationFor(page.Talker, prn.Value);
                if (!seen.Add(new SatelliteId(constellation, prn.Value)))
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
                        Constellation = constellation,
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
                        Constellation = constellation,
                        ElevationDegrees = elevation,
                        AzimuthDegrees = azimuth,
                    });
                }
            }
        }

        // Page accounting is PER CONSTELLATION, because a multi-constellation talker runs a
        // separate GSV cycle for each with its own paging - $GPGSV 1..3 and $GLGSV 1..2 interleaved
        // in one second. NmeaSentence.Key strips the talker on purpose, so they arrive here as one
        // run of pages; counting them against the first page's total compared five against three
        // and warned on every cycle from a receiver that was working perfectly (#417). A permanent
        // stream of spurious warnings is the parser's own version of a gate that cries wolf.
        foreach (IGrouping<string, NmeaSentence> constellation in
                 pages.GroupBy(page => page.Talker, StringComparer.Ordinal))
        {
            int? declaredPages = ParseInt(constellation.First().Field(0));
            int actual = constellation.Count();
            if (declaredPages is int expected && actual != expected)
            {
                warnings.Add($"the {constellation.Key} cycle carried {actual} GSV page(s) of {expected}");
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
        // THE TIME AND THE DATE MUST COME FROM THE SAME SENTENCE (#420).
        //
        // They used to be chosen independently - the time from whichever sentence had one, the date
        // from ZDA and failing that from RMC. A cycle takes tens of milliseconds to reach the wire,
        // so one straddling midnight has sentences on both sides of it, and a ZDA carrying a time
        // but no readable date paired its 23:59:59 with RMC's date for the following day: a
        // 24-hour error built from two sentences that were each correct. Narrow, and silent, and
        // only ever at midnight, which is the worst combination to debug from a field report.
        string? hhmmss = null;
        int? year = null;
        int? month = null;
        int? day = null;

        if (zda is not null &&
            ParseInt(zda.Field(3)) is int zdaYear &&
            ParseInt(zda.Field(2)) is int zdaMonth &&
            ParseInt(zda.Field(1)) is int zdaDay)
        {
            (hhmmss, year, month, day) = (zda.Field(0), zdaYear, zdaMonth, zdaDay);
        }
        else if (rmc?.Field(8) is { Length: 6 } ddmmyy && ParseInt(ddmmyy[4..]) is int yy)
        {
            hhmmss = rmc.Field(0);
            day = ParseInt(ddmmyy[..2]);
            month = ParseInt(ddmmyy[2..4]);
            year = 2000 + yy;
        }
        else
        {
            // No sentence carries both. Take a time so the "time but no date" warning below can
            // say so, which is more useful than reporting nothing at all.
            hhmmss = zda?.Field(0) ?? rmc?.Field(0) ?? gga?.Field(0);
        }

        if (hhmmss is null || hhmmss.Length < 6)
        {
            return null;
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

    private static GeoPosition? Position(NmeaSentence? gga, NmeaSentence? gns, NmeaSentence? rmc, bool hasFix, List<string> warnings)
    {
        if (!hasFix)
        {
            return null;
        }

        // GNS lays its position out exactly as GGA does - time, then latitude, hemisphere,
        // longitude, hemisphere - so it slots in beside it rather than needing its own reader. RMC
        // starts one field later, which is what `first` is for.
        NmeaSentence? source = gga ?? gns ?? rmc;
        int first = gga is not null || gns is not null ? 1 : 2;
        if (source is null)
        {
            return null;
        }

        double? latitude = Angle(source.Field(first), source.Field(first + 1), degreeDigits: 2);
        double? longitude = Angle(source.Field(first + 2), source.Field(first + 3), degreeDigits: 3);

        // ALTITUDE IS FIELD 8 OF BOTH (#429). GGA reads time, lat, N/S, lon, E/W, quality, satellites
        // in use, HDOP, altitude; GNS reads time, lat, N/S, lon, E/W, mode, satellites in use, HDOP,
        // altitude. Both are orthometric height in metres, so HeightDatum stays Msl either way, and
        // a receiver sending GNS and no GGA is no longer a position with no height.
        double? height = ParseDouble(gga?.Field(8)) ?? ParseDouble(gns?.Field(8));

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

            // GGA only, and field 10 rather than 8's altitude (#435). GNS lays its position out like
            // GGA's up to the height, but the standard does not give it a separation field, so
            // reading one from the same offset would be inventing a number from whatever follows.
            GeoidSeparationMetres = ParseDouble(gga?.Field(10)),
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
