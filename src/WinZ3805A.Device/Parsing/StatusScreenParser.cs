using System.Globalization;
using System.Text.RegularExpressions;
using WinZ3805A.Device.Models;

namespace WinZ3805A.Device.Parsing;

/// <summary>
/// Turns the receiver's <c>:SYST:STAT?</c> screen into a <see cref="ReceiverStatus"/> (§11).
/// </summary>
/// <remarks>
/// <para>
/// This is the highest-risk component in the project, which is why §15 schedules it before any UI
/// exists. The satellite elevation, azimuth, and signal-strength table has no individual query — it
/// exists only inside this screen — so everything the Satellites page, the sky plot, and the
/// position readout show comes through here.
/// </para>
/// <para>
/// <b>It never throws</b> (§11.1). Every field is attempted independently, a field that will not
/// parse becomes <see langword="null"/>, and the reason is added to
/// <see cref="ReceiverStatus.ParseWarnings"/> for the Diagnostics page. The whole body sits inside
/// one last-resort catch as well, so even a defect in this file degrades to an empty status with a
/// warning rather than tearing down the polling loop.
/// </para>
/// <para>
/// <b>Column positions come from the header row, never from constants</b> (§11.1). The family
/// differs in column labels and widths — <c>C/N</c> on 58503B-class units against <c>SS</c> on
/// 59551A-class units — and values overflow their header token to the left when they need the room
/// (a three-digit azimuth under a two-character <c>Az</c>), so each field runs from just past the
/// previous column's header to the end of its own. That single rule is what makes the table survive
/// a firmware revision that shifts a column by a character.
/// </para>
/// <para>
/// Everything outside the satellite table is found by its label rather than by position, because
/// the labels are unique across the screen and a label scan cannot be broken by a width change at
/// all.
/// </para>
/// </remarks>
/// <param name="timeProvider">
/// Supplies "now" for <see cref="ReceiverStatus.CapturedAt"/> and for the §7.4 week-rollover
/// comparison. Injected rather than read from <see cref="DateTime"/> so fixture tests can pin the
/// clock — the rollover logic is meaningless against a moving one.
/// </param>
public sealed partial class StatusScreenParser(TimeProvider timeProvider)
{
    /// <summary>
    /// The header tokens that make up a satellite column group, besides <c>PRN</c> which opens one.
    /// </summary>
    /// <remarks>
    /// Deliberately a closed set. Scanning stops at the first token that is not one of these, which
    /// is what keeps the right-hand time and position panel — whose text shares these lines — out
    /// of the table's column model.
    /// </remarks>
    private static readonly string[] ColumnLabels = ["El", "Elev", "Az", "Azm", "C/N", "S/N", "SS"];

    /// <summary>The header tokens that mark a column as carrying signal strength.</summary>
    private static readonly string[] StrengthLabels = ["C/N", "S/N", "SS"];

    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>
    /// Parses one complete status screen.
    /// </summary>
    /// <param name="screen">
    /// The response to <c>:SYST:STAT?</c> with the transaction framing already removed — no prompt,
    /// no echoed command. Line endings may be CRLF, CR, or LF; leading and trailing whitespace
    /// <em>within</em> a line is significant and must not have been trimmed.
    /// </param>
    /// <returns>
    /// A populated <see cref="ReceiverStatus"/>. Never <see langword="null"/>, and this method never
    /// throws: an unrecognisable screen yields a status whose fields are all null or default and
    /// whose <see cref="ReceiverStatus.ParseWarnings"/> says so.
    /// </returns>
    public ReceiverStatus Parse(string? screen)
    {
        DateTimeOffset capturedAt = _timeProvider.GetUtcNow();
        List<string> warnings = [];

        try
        {
            if (string.IsNullOrWhiteSpace(screen))
            {
                warnings.Add("The status screen was empty.");
                return new ReceiverStatus { CapturedAt = capturedAt, ParseWarnings = warnings };
            }

            string[] lines = screen.Split(['\r', '\n'], StringSplitOptions.None);
            return ParseLines(lines, capturedAt, warnings);
        }
        catch (Exception exception)
        {
            // §11.1: the parser never throws. A defect here must not take down the polling loop, so
            // the failure is reported through the same channel a bad field would use.
            warnings.Add($"The parser failed unexpectedly and the screen was discarded: {exception.Message}");
            return new ReceiverStatus { CapturedAt = capturedAt, ParseWarnings = warnings };
        }
    }

    private ReceiverStatus ParseLines(string[] lines, DateTimeOffset capturedAt, List<string> warnings)
    {
        (OutputValidity outputs, bool gpsOnePpsValid, bool healthOk) = ParseBanners(lines, warnings);
        (SmartClockMode mode, string? modeDetail) = ParseMode(lines);
        (IReadOnlyList<TrackedSatellite> tracked, IReadOnlyList<PredictedSatellite> notTracked, SignalStrengthKind strengthKind) =
            ParseSatelliteTable(lines, warnings);

        (DateTimeOffset? deviceTime, TimeScale timeScale, bool provisionalTime) = ParseDeviceTime(lines, warnings);
        (int epochs, DateTimeOffset? corrected) = ApplyWeekRollover(deviceTime, capturedAt);
        ClockAdvisory advisory = ParseClockAdvisory(lines, warnings);
        (GeoPosition? position, HeightDatum datum) = ParsePosition(lines, warnings);
        (PositionMode positionMode, double? surveyPercent, SurveySuspendedReason suspended) =
            ParsePositionMode(lines, warnings);

        return new ReceiverStatus
        {
            Outputs = outputs,
            Mode = mode,
            ModeDetail = modeDetail,
            Tfom = FindInteger(lines, TfomPattern()),
            Ffom = FindInteger(lines, FfomPattern()),
            OnePpsTiNanoseconds = FindScaledValue(lines, OnePpsTiPattern(), UnitScale.Nanoseconds),
            HoldThresholdSeconds = FindScaledValue(lines, HoldThresholdPattern(), UnitScale.Seconds),
            HoldoverPredictedSeconds = FindScaledValue(lines, HoldoverPredictPattern(), UnitScale.Seconds),
            HoldoverPresentSeconds = FindScaledValue(lines, HoldoverPresentPattern(), UnitScale.Seconds),

            // Read from the screen since 28 Aug 2026, when pulling the antenna produced the
            // holdover fixture this was waiting for. The label is "Holdover Duration:" and it
            // shares a line with the present uncertainty:
            //
            //     Holdover Duration:  0m 03s   Present  1.0 us
            HoldoverDuration = FindHoldoverDuration(lines),

            GpsOnePpsValid = gpsOnePpsValid,
            Tracked = tracked,
            NotTracked = notTracked,
            ElevationMaskDegrees = FindInteger(lines, ElevationMaskPattern()),
            SignalStrengthKind = strengthKind,

            TimeScale = timeScale,
            DeviceDateTime = deviceTime,
            DeviceTimeIsProvisional = provisionalTime,
            WeekRolloverEpochs = epochs,
            CorrectedDateTime = corrected,
            OnePpsClockAdvisory = advisory,
            AntennaDelayNanoseconds = FindScaledValue(lines, AntennaDelayPattern(), UnitScale.Nanoseconds),
            LeapPending = ParseLeapPending(lines),

            PositionMode = positionMode,
            SurveyPercentComplete = surveyPercent,
            SurveySuspendedReason = suspended,
            Position = position,
            PositionQualifier = ParsePositionQualifier(lines),
            HeightDatum = datum,

            HealthOk = healthOk,
            HealthItems = ParseHealthItems(lines, warnings),

            CapturedAt = capturedAt,
            ParseWarnings = warnings,
        };
    }

    // -----------------------------------------------------------------------------------------
    // Banners
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the three section banners, each of which carries its headline verdict in brackets:
    /// <c>SYNCHRONIZATION ... [ Outputs Valid ]</c>, <c>ACQUISITION ... [ GPS 1PPS Valid ]</c>, and
    /// <c>HEALTH MONITOR ... [ OK ]</c>.
    /// </summary>
    private static (OutputValidity Outputs, bool GpsOnePpsValid, bool HealthOk) ParseBanners(
        string[] lines,
        List<string> warnings)
    {
        OutputValidity outputs = OutputValidity.Unknown;
        bool gpsOnePpsValid = false;
        bool healthOk = false;
        bool sawHealthBanner = false;

        foreach (string line in lines)
        {
            string? annotation = BannerAnnotation(line);
            if (annotation is null)
            {
                continue;
            }

            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("SYNCHRONIZATION", StringComparison.OrdinalIgnoreCase))
            {
                // "Reduced" is tested first because its text contains "Valid" as a substring, so the
                // looser match would swallow it and report full accuracy on a degraded receiver.
                outputs =
                    annotation.Contains("Reduced", StringComparison.OrdinalIgnoreCase) ? OutputValidity.ValidReduced :
                    annotation.Contains("Invalid", StringComparison.OrdinalIgnoreCase) ? OutputValidity.Invalid :
                    annotation.Contains("Valid", StringComparison.OrdinalIgnoreCase) ? OutputValidity.Valid :
                    OutputValidity.Unknown;

                if (outputs == OutputValidity.Unknown)
                {
                    warnings.Add($"Unrecognised synchronization banner: '{annotation}'.");
                }
            }
            else if (trimmed.StartsWith("ACQUISITION", StringComparison.OrdinalIgnoreCase))
            {
                // Same ordering trap as above: "Invalid" contains "Valid".
                gpsOnePpsValid =
                    !annotation.Contains("Invalid", StringComparison.OrdinalIgnoreCase)
                    && annotation.Contains("Valid", StringComparison.OrdinalIgnoreCase);
            }
            else if (trimmed.StartsWith("HEALTH MONITOR", StringComparison.OrdinalIgnoreCase))
            {
                sawHealthBanner = true;
                healthOk = annotation.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (!sawHealthBanner)
        {
            warnings.Add("No health monitor banner was found; health is reported as not OK.");
        }

        return (outputs, gpsOnePpsValid, healthOk);
    }

    /// <summary>Returns the text between the final brackets on a banner line, or null if it is not one.</summary>
    private static string? BannerAnnotation(string line)
    {
        int open = line.LastIndexOf('[');
        int close = line.LastIndexOf(']');
        return open >= 0 && close > open ? line[(open + 1)..close].Trim() : null;
    }

    // -----------------------------------------------------------------------------------------
    // SmartClock mode
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the active SmartClock mode. The screen prints all four modes as a menu and marks the
    /// live one with <c>&gt;&gt;</c>, so the marker is what is searched for — not any mode word,
    /// which would match the three inactive rows just as well.
    /// </summary>
    private static (SmartClockMode Mode, string? Detail) ParseMode(string[] lines)
    {
        foreach (string line in lines)
        {
            string trimmed = line.TrimStart();
            if (!trimmed.StartsWith(">>", StringComparison.Ordinal))
            {
                continue;
            }

            // The mode occupies the left-hand column, and the same physical line carries the
            // reference-outputs panel on its right — the live mode row reads
            // ">> Locked to GPS: stabilizing frequency       TFOM     3             FFOM     1".
            // The panels are separated by a run of three or more spaces while the mode text uses
            // single ones, so cutting at the first wide gap keeps "TFOM 3 FFOM 1" out of the detail
            // without knowing where the column happens to start on this firmware.
            string body = CutAtWideGap(trimmed[2..].Trim());
            int colon = body.IndexOf(':');
            string name = colon >= 0 ? body[..colon].Trim() : body;
            string? detail = colon >= 0 ? CollapseSpaces(body[(colon + 1)..]) : null;

            SmartClockMode mode =
                name.Contains("Locked", StringComparison.OrdinalIgnoreCase) ? SmartClockMode.Locked :
                name.Contains("Recovery", StringComparison.OrdinalIgnoreCase) ? SmartClockMode.Recovery :
                name.Contains("Holdover", StringComparison.OrdinalIgnoreCase) ? SmartClockMode.Holdover :
                name.Contains("Power", StringComparison.OrdinalIgnoreCase) ? SmartClockMode.PowerUp :
                SmartClockMode.Unknown;

            return (mode, string.IsNullOrEmpty(detail) ? null : detail);
        }

        return (SmartClockMode.Unknown, null);
    }

    // -----------------------------------------------------------------------------------------
    // Satellite table
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Parses the acquisition table into its tracked and not-tracked halves, deriving every column
    /// boundary from the header row (§11.1).
    /// </summary>
    private static (IReadOnlyList<TrackedSatellite> Tracked, IReadOnlyList<PredictedSatellite> NotTracked, SignalStrengthKind Kind)
        ParseSatelliteTable(string[] lines, List<string> warnings)
    {
        int headerIndex = FindHeaderRow(lines);
        if (headerIndex < 0)
        {
            warnings.Add("No satellite table header row was found; the acquisition table was skipped.");
            return ([], [], SignalStrengthKind.Unknown);
        }

        IReadOnlyList<ColumnGroup> groups = BuildColumnGroups(lines[headerIndex]);
        if (groups.Count == 0)
        {
            warnings.Add("The satellite table header carried no usable columns.");
            return ([], [], SignalStrengthKind.Unknown);
        }

        // A group that has a signal-strength column is a tracking group; one that has none is a
        // prediction group, because an untracked satellite has no signal to report. That structural
        // difference is what identifies the halves without depending on their order, and §11.1's
        // "count the PRN occurrences" falls out of it: any number of groups on either side works.
        SignalStrengthKind kind = SignalStrengthKind.Unknown;
        foreach (ColumnGroup group in groups)
        {
            if (group.StrengthLabel is not null)
            {
                kind = StrengthKindFor(group.StrengthLabel);
                break;
            }
        }

        List<TrackedSatellite> tracked = [];
        List<PredictedSatellite> notTracked = [];

        for (int i = headerIndex + 1; i < lines.Length && !IsSectionBoundary(lines[i]); i++)
        {
            foreach (ColumnGroup group in groups)
            {
                string? prnText = group.Prn.Slice(lines[i]);

                // The receiver marks a satellite it is trying to acquire with a leading asterisk,
                // and says so in the screen's own legend: "*attempting to track". Parsed as a plain
                // integer that row yields null and the satellite is dropped — so a power-up screen
                // reporting "Not Tracking: 10" produced five, because five of the ten were starred
                // (#4). The marker is a fact about the satellite, not noise: it is kept.
                bool attempting = prnText is not null && prnText.TrimStart().StartsWith('*');
                int? prn = ParseIntOrNull(attempting ? prnText!.TrimStart().TrimStart('*') : prnText);
                if (prn is null)
                {
                    continue;
                }

                int? elevation = ParseIntOrNull(group.Elevation?.Slice(lines[i]));
                int? azimuth = ParseIntOrNull(group.Azimuth?.Slice(lines[i]));

                if (group.StrengthLabel is null)
                {
                    notTracked.Add(new PredictedSatellite
                    {
                        Prn = prn.Value,
                        ElevationDegrees = elevation,
                        AzimuthDegrees = azimuth,
                        AttemptingToTrack = attempting,
                    });
                }
                else
                {
                    tracked.Add(new TrackedSatellite
                    {
                        Prn = prn.Value,
                        ElevationDegrees = elevation,
                        AzimuthDegrees = azimuth,
                        SignalStrength = ParseIntOrNull(group.Strength?.Slice(lines[i])),
                    });
                }
            }
        }

        CrossCheckCounts(lines, tracked.Count, notTracked.Count, warnings);
        return (tracked, notTracked, kind);
    }

    /// <summary>
    /// The header row is the first line carrying a <c>PRN</c> token. Searching for the token rather
    /// than for a whole-line pattern is what allows the row to also carry the right-hand panel's
    /// text, which it does on every real screen.
    /// </summary>
    private static int FindHeaderRow(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            foreach ((string text, _, _) in Tokenize(lines[i]))
            {
                if (text.Equals("PRN", StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Derives the column groups from the header row's token positions.
    /// </summary>
    /// <remarks>
    /// Each field runs from one past the previous column's last character to its own last character.
    /// Extending leftwards into the gap rather than using the header token's own extent is what
    /// handles a value wider than its label — a three-digit azimuth under <c>Az</c> is the case that
    /// occurs on every screen with a satellite east or west of the receiver, and slicing the header's
    /// two characters would silently read 219 as 19.
    /// </remarks>
    private static IReadOnlyList<ColumnGroup> BuildColumnGroups(string headerLine)
    {
        List<ColumnGroup> groups = [];
        List<(string Text, int Start, int End)> tokens = Tokenize(headerLine);

        // Where the previous column ended, so the next one knows how far left it may reach. Starts
        // at -1 so the very first column's field begins at index 0.
        int previousEnd = -1;

        for (int i = 0; i < tokens.Count; i++)
        {
            if (!tokens[i].Text.Equals("PRN", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FieldExtent prn = new(previousEnd + 1, tokens[i].End);
            previousEnd = tokens[i].End;

            FieldExtent? elevation = null;
            FieldExtent? azimuth = null;
            FieldExtent? strength = null;
            string? strengthLabel = null;

            int j = i + 1;
            for (; j < tokens.Count; j++)
            {
                (string text, _, int end) = tokens[j];
                if (!ColumnLabels.Contains(text, StringComparer.OrdinalIgnoreCase))
                {
                    // Not a column label, so the group has ended and this token belongs to the
                    // right-hand panel. Stop rather than skip: scanning on would let a stray word
                    // that happens to read "SS" pull the panel into the table.
                    break;
                }

                FieldExtent extent = new(previousEnd + 1, end);
                previousEnd = end;

                if (StrengthLabels.Contains(text, StringComparer.OrdinalIgnoreCase))
                {
                    strength = extent;
                    strengthLabel = text;
                }
                else if (text.StartsWith("El", StringComparison.OrdinalIgnoreCase))
                {
                    elevation = extent;
                }
                else
                {
                    azimuth = extent;
                }
            }

            groups.Add(new ColumnGroup(prn, elevation, azimuth, strength, strengthLabel));
            i = j - 1;
        }

        return groups;
    }

    /// <summary>
    /// Compares the parsed row counts against the <c>Tracking:</c> and <c>Not Tracking:</c> figures
    /// the receiver prints above the table, and records a warning if they disagree.
    /// </summary>
    /// <remarks>
    /// The counts are the receiver's own view, so a mismatch means the column model has slipped on
    /// this firmware revision — exactly the failure §11.1's header-relative rule exists to prevent,
    /// and worth surfacing in Diagnostics rather than discovering from a wrong sky plot.
    /// </remarks>
    private static void CrossCheckCounts(string[] lines, int tracked, int notTracked, List<string> warnings)
    {
        int? declaredTracked = FindInteger(lines, TrackingCountPattern());
        int? declaredNotTracked = FindInteger(lines, NotTrackingCountPattern());

        if (declaredTracked is int t && t != tracked)
        {
            warnings.Add($"The screen reported {t} tracked satellites but {tracked} rows parsed.");
        }

        if (declaredNotTracked is int n && n != notTracked)
        {
            warnings.Add($"The screen reported {n} satellites not tracked but {notTracked} rows parsed.");
        }
    }

    private static SignalStrengthKind StrengthKindFor(string label) =>
        label.Equals("SS", StringComparison.OrdinalIgnoreCase)
            ? SignalStrengthKind.SignalStrength
            : SignalStrengthKind.CarrierToNoise;

    /// <summary>True for the banner lines that end a section, so table scanning stops at one.</summary>
    private static bool IsSectionBoundary(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("HEALTH MONITOR", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("SYNCHRONIZATION", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("ACQUISITION", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("ELEV MASK", StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------------------------
    // Time
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the clock row — a time scale, a time of day, and a date, as in
    /// <c>UTC      14:45:02     27 Dec 2006</c>.
    /// </summary>
    /// <remarks>
    /// Matched on the whole shape rather than on the leading word alone, because
    /// <c>GPS 1PPS Synchronized to UTC</c> sits two lines below and starts with a scale name too.
    /// </remarks>
    private static (DateTimeOffset? DeviceTime, TimeScale Scale, bool Provisional) ParseDeviceTime(
        string[] lines,
        List<string> warnings)
    {
        foreach (string line in lines)
        {
            Match match = DeviceTimePattern().Match(line);
            if (!match.Success)
            {
                continue;
            }

            string scaleText = CollapseSpaces(match.Groups["scale"].Value) ?? string.Empty;
            TimeScale scale = ParseTimeScale(scaleText);

            string stamp = $"{match.Groups["day"].Value} {match.Groups["month"].Value} {match.Groups["year"].Value} " +
                           $"{match.Groups["time"].Value}";

            if (!DateTime.TryParseExact(
                    stamp,
                    "d MMM yyyy H:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime parsed))
            {
                warnings.Add($"The clock row did not parse as a date and time: '{line.Trim()}'.");
                return (null, scale, false);
            }

            if (scale is TimeScale.Local or TimeScale.LocalGps)
            {
                // The receiver prints local time without saying what offset produced it, so the
                // instant cannot be reconstructed. The value is kept at face value and the caveat
                // recorded rather than a host-machine offset being invented for it.
                warnings.Add("The receiver is reporting local time, so the UTC offset is unknown and zero was assumed.");
            }

            // The marker is carried out with the value rather than warned about. §11.1 puts
            // ParseWarnings in Diagnostics, which nobody reads while looking at a clock - and this
            // is a property of the reading that the UI has to show next to it, not a parse problem.
            bool provisional = match.Groups["provisional"].Success;

            return (new DateTimeOffset(parsed, TimeSpan.Zero), scale, provisional);
        }

        // A clock row that is present but unreadable is a different report from an absent one, and
        // the difference is the whole value of the warning. §11.1 puts ParseWarnings in Diagnostics
        // so a field report about an odd firmware revision is actionable; "no clock row was found"
        // sends the reader looking for a line that is sitting right there in the capture.
        //
        // The case that prompted this was a power-up screen (#245): the receiver prints
        //     GPS      05:10:04 (?) 12 Jan 2007
        // and the (?) between the time and the date used to defeat the full pattern. That row now
        // parses, and the marker is carried on DeviceTimeIsProvisional - but this fallback is kept,
        // because the reason it was written has not gone away. A row this loop finds and the full
        // pattern does not is a shape nobody has seen yet, and saying so beats denying the line
        // exists.
        foreach (string line in lines)
        {
            if (ClockRowShapePattern().IsMatch(line))
            {
                warnings.Add(
                    $"A clock row was found but did not parse: '{line.Trim()}'. " +
                    "The time was not read.");
                return (null, TimeScale.Unknown, false);
            }
        }

        warnings.Add("No clock row was found on the status screen.");
        return (null, TimeScale.Unknown, false);
    }

    private static TimeScale ParseTimeScale(string text)
    {
        bool local = text.Contains("LOCAL", StringComparison.OrdinalIgnoreCase)
            || text.Contains("LCL", StringComparison.OrdinalIgnoreCase);

        if (local)
        {
            return text.Contains("GPS", StringComparison.OrdinalIgnoreCase) ? TimeScale.LocalGps : TimeScale.Local;
        }

        return text.Contains("UTC", StringComparison.OrdinalIgnoreCase) ? TimeScale.Utc
            : text.Contains("GPS", StringComparison.OrdinalIgnoreCase) ? TimeScale.Gps
            : TimeScale.Unknown;
    }

    /// <summary>
    /// Applies §7.4's week-rollover correction: if the device's date is close to a whole number of
    /// 1024-week epochs behind the host clock, report how many and what the corrected instant is.
    /// </summary>
    /// <remarks>
    /// The correction is reported, never substituted. §7.4 is explicit that the raw device date must
    /// stay visible, because a user who sees a date two decades out with no explanation reasonably
    /// concludes the hardware has failed. Time of day and the 1 PPS itself are unaffected.
    /// </remarks>
    private static (int Epochs, DateTimeOffset? Corrected) ApplyWeekRollover(DateTimeOffset? deviceTime, DateTimeOffset now)
    {
        if (deviceTime is not DateTimeOffset device)
        {
            return (0, null);
        }

        TimeSpan delta = now - device;
        double epochs = Math.Round(delta / GpsWeekRollover.Epoch);

        if (epochs <= 0)
        {
            return (0, device);
        }

        TimeSpan residual = delta - (GpsWeekRollover.Epoch * epochs);
        if (residual.Duration() > GpsWeekRollover.Tolerance)
        {
            // A large gap that is not a multiple of the epoch is a receiver with the wrong date set,
            // not a rollover, and inventing a correction for it would be worse than showing what the
            // device said.
            return (0, device);
        }

        int count = (int)epochs;
        return (count, device + (GpsWeekRollover.Epoch * count));
    }

    /// <summary>
    /// Reads the <c>1PPS CLK</c> advisory and decodes it to one of the §11.3 values.
    /// </summary>
    private static ClockAdvisory ParseClockAdvisory(string[] lines, List<string> warnings)
    {
        foreach (string line in lines)
        {
            // The acquisition banner reads "ACQUISITION ... [ GPS 1PPS Valid ]" and would otherwise
            // match first, yielding "Valid ]" as the advisory. Banners are excluded by their
            // brackets, which no panel line carries.
            if (BannerAnnotation(line) is not null)
            {
                continue;
            }

            // The mode row is excluded for the same reason and was found the same way. In holdover
            // it reads ">> Holdover: GPS 1PPS invalid", which this pattern matches and then runs to
            // the end of the line, taking the reference-outputs panel with it — the 28 Aug fixture
            // produced the advisory 'invalid HOLD THR 1.000 us', warned that it was unrecognised,
            // and never reached the real advisory two panels below.
            //
            // Only holdover puts that phrase on the mode row, which is why five earlier fixtures
            // and §11.3's own tests all passed.
            if (line.TrimStart().StartsWith(">>", StringComparison.Ordinal))
            {
                continue;
            }

            Match match = ClockAdvisoryPattern().Match(line);
            if (!match.Success)
            {
                continue;
            }

            string? text = CollapseSpaces(match.Groups["advisory"].Value);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            ClockAdvisory advisory = ClassifyAdvisory(text);
            if (advisory == ClockAdvisory.Other)
            {
                // §11.3 keeps no string form of the advisory on the model, so this is the only
                // place the device's own wording survives — and it is the only place it is worth
                // having, because an advisory the table does not cover is exactly what a field
                // report about an unfamiliar firmware revision needs to quote.
                warnings.Add($"Unrecognised 1PPS advisory: '{text}'.");
            }

            return advisory;
        }

        return ClockAdvisory.None;
    }

    private static ClockAdvisory ClassifyAdvisory(string text)
    {
        // "Assessing stability" animates with nought to three trailing dots on the device's own
        // screen. They carry no information and would otherwise make four distinct strings of one
        // state.
        string normalised = text.TrimEnd('.', ' ');

        return normalised switch
        {
            _ when Has(normalised, "Synchronized to UTC") => ClockAdvisory.SynchronizedToUtc,
            _ when Has(normalised, "Synchronized to GPS") => ClockAdvisory.SynchronizedToGpsTime,
            _ when Has(normalised, "Assessing stability") => ClockAdvisory.AssessingStability,
            _ when Has(normalised, "Questionable accuracy") => ClockAdvisory.QuestionableAccuracy,
            _ when Has(normalised, "not tracking") => ClockAdvisory.InaccurateNotTracking,
            _ when Has(normalised, "inacc position") => ClockAdvisory.InaccurateInaccuratePosition,
            _ when Has(normalised, "Absent or freq error") => ClockAdvisory.AbsentOrFrequencyError,
            _ when Has(normalised, "GPS rcvr err") => ClockAdvisory.InvalidGpsReceiverError,
            _ => ClockAdvisory.Other,
        };

        static bool Has(string haystack, string needle) =>
            haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads a pending leap-second announcement.
    /// </summary>
    /// <remarks>
    /// No captured screen carries one — they appear a few times a decade — so this matches the label
    /// and its sign rather than a known full line, and reports <see cref="LeapSecondPending.None"/>
    /// when there is nothing to read. That is the correct answer on every screen so far.
    /// </remarks>
    private static LeapSecondPending ParseLeapPending(string[] lines)
    {
        foreach (string line in lines)
        {
            Match match = LeapPendingPattern().Match(line);
            if (match.Success)
            {
                return match.Groups["sign"].Value == "-" ? LeapSecondPending.Minus : LeapSecondPending.Plus;
            }
        }

        return LeapSecondPending.None;
    }

    // -----------------------------------------------------------------------------------------
    // Position
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Reads latitude, longitude, and height, converting the receiver's degrees-minutes-seconds
    /// into the signed decimal degrees every consumer wants.
    /// </summary>
    private static (GeoPosition? Position, HeightDatum Datum) ParsePosition(string[] lines, List<string> warnings)
    {
        double? latitude = null;
        double? longitude = null;
        double? height = null;
        HeightDatum datum = HeightDatum.Unknown;
        bool sawAny = false;

        foreach (string line in lines)
        {
            Match angle = AnglePattern().Match(line);
            if (angle.Success)
            {
                sawAny = true;
                double? value = ToDecimalDegrees(angle, warnings, line);
                if (angle.Groups["label"].Value.Equals("LAT", StringComparison.OrdinalIgnoreCase))
                {
                    latitude = value;
                }
                else
                {
                    longitude = value;
                }

                continue;
            }

            Match hgt = HeightPattern().Match(line);
            if (hgt.Success)
            {
                sawAny = true;
                if (double.TryParse(hgt.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double metres))
                {
                    height = metres;
                }
                else
                {
                    warnings.Add($"The height did not parse: '{line.Trim()}'.");
                }

                string qualifier = hgt.Groups["datum"].Value;
                datum =
                    qualifier.Contains("MSL", StringComparison.OrdinalIgnoreCase) ? HeightDatum.Msl :
                    qualifier.Contains("GPS", StringComparison.OrdinalIgnoreCase) ? HeightDatum.GpsEllipsoid :
                    HeightDatum.Unknown;
            }
        }

        if (!sawAny)
        {
            return (null, HeightDatum.Unknown);
        }

        return (new GeoPosition
        {
            LatitudeDegrees = latitude,
            LongitudeDegrees = longitude,
            HeightMetres = height,
        }, datum);
    }

    /// <summary>Converts a matched <c>N  47:31:18.822</c> into signed decimal degrees.</summary>
    private static double? ToDecimalDegrees(Match match, List<string> warnings, string line)
    {
        if (!int.TryParse(match.Groups["deg"].Value, CultureInfo.InvariantCulture, out int degrees)
            || !int.TryParse(match.Groups["min"].Value, CultureInfo.InvariantCulture, out int minutes)
            || !double.TryParse(match.Groups["sec"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
        {
            warnings.Add($"The coordinate did not parse: '{line.Trim()}'.");
            return null;
        }

        double magnitude = degrees + (minutes / 60d) + (seconds / 3600d);
        string hemisphere = match.Groups["hemisphere"].Value.ToUpperInvariant();
        return hemisphere is "S" or "W" ? -magnitude : magnitude;
    }

    /// <summary>Reads the position mode row and any survey progress or suspension it carries.</summary>
    private static (PositionMode Mode, double? Percent, SurveySuspendedReason Suspended)
        ParsePositionMode(string[] lines, List<string> warnings)
    {
        PositionMode mode = PositionMode.Unknown;
        double? percent = null;

        foreach (string line in lines)
        {
            Match match = PositionModePattern().Match(line);
            if (!match.Success)
            {
                continue;
            }

            string body = match.Groups["body"].Value;
            PositionMode candidate =
                body.Contains("Survey", StringComparison.OrdinalIgnoreCase) ? PositionMode.Survey :
                body.Contains("Hold", StringComparison.OrdinalIgnoreCase) ? PositionMode.Hold :
                PositionMode.Unknown;

            if (candidate == PositionMode.Unknown)
            {
                // "SmartClock Mode ___..." heads the synchronization panel and matches the label
                // just as well as the position row does. Keep looking rather than stopping on it —
                // the row that names a mode is the one that means anything.
                continue;
            }

            mode = candidate;

            Match progress = PercentPattern().Match(body);
            if (progress.Success
                && double.TryParse(progress.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                percent = value;
            }

            break;
        }

        return (mode, percent, ParseSurveySuspension(lines, warnings));
    }

    private static SurveySuspendedReason ParseSurveySuspension(string[] lines, List<string> warnings)
    {
        foreach (string line in lines)
        {
            Match match = SuspendedPattern().Match(line);
            if (!match.Success)
            {
                continue;
            }

            string? text = CollapseSpaces(match.Groups["reason"].Value);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            SurveySuspendedReason reason =
                text.Contains("sats", StringComparison.OrdinalIgnoreCase)
                || text.Contains("track <", StringComparison.OrdinalIgnoreCase) ? SurveySuspendedReason.TooFewSatellites :
                text.Contains("geometry", StringComparison.OrdinalIgnoreCase) ? SurveySuspendedReason.PoorGeometry :
                text.Contains("no track data", StringComparison.OrdinalIgnoreCase) ? SurveySuspendedReason.NoTrackData :
                SurveySuspendedReason.Other;

            if (reason == SurveySuspendedReason.Other)
            {
                warnings.Add($"Unrecognised survey suspension: '{text}'.");
            }

            return reason;
        }

        return SurveySuspendedReason.None;
    }

    /// <summary>
    /// Reads the position qualifier if the screen states one, in either form the family uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A held position prints no qualifier on this receiver, so
    /// <see cref="PositionQualifier.Unknown"/> stays the ordinary result rather than a failure.
    /// </para>
    /// <para>
    /// <b>Two forms, because the parenthesised one is not what the Z3805A prints.</b> The
    /// documented form qualifies the value — <c>(Average)</c>, <c>(Init)</c>, <c>(Held)</c> — and is
    /// kept for the models that use it. The SmartClock screens captured on 27 Aug 2026 qualify the
    /// <i>label</i> instead, and only while a survey is running:
    /// </para>
    /// <code>
    /// holding:    LAT      N  47:31:18.822
    /// surveying:  AVG LAT  N  47:31:18.640
    /// </code>
    /// <para>
    /// <c>AVG</c> does not match <c>Aver\w*</c>, so both surveying fixtures in the corpus read as
    /// having no qualifier at all — the one distinction this method exists to draw, lost on the only
    /// screens that draw it. The remark above used to say every captured screen was a held one,
    /// which is how it went unnoticed until there were screens that were not.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Reads how long the receiver has been degraded, or null if the screen does not say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This counts holdover and recovery together.</b> The Z3801A guide says so twice — "the
    /// duration that the Receiver has been operating in holdover (and recovery)", and "the
    /// cumulative duration of holdover and recovery operations". So it keeps running after the
    /// antenna is reconnected, until lock is regained, and a caller must not present it as "time
    /// since the signal was lost" once the receiver has moved on to recovery.
    /// </para>
    /// <para>
    /// Null when absent rather than <see cref="TimeSpan.Zero"/>, which is the same distinction
    /// <c>HoldoverViewModel</c> already draws: a dash says the screen did not report it, and a zero
    /// would claim no time has passed.
    /// </para>
    /// </remarks>
    private static TimeSpan? FindHoldoverDuration(string[] lines)
    {
        foreach (string line in lines)
        {
            Match match = HoldoverDurationPattern().Match(line);
            if (!match.Success)
            {
                continue;
            }

            // Every group is digits the pattern matched, so none of these can fail; the unmatched
            // optional groups yield zero rather than throwing.
            static int Part(Group group) =>
                group.Success ? int.Parse(group.Value, CultureInfo.InvariantCulture) : 0;

            return new TimeSpan(
                Part(match.Groups["days"]),
                Part(match.Groups["hours"]),
                Part(match.Groups["minutes"]),
                Part(match.Groups["seconds"]));
        }

        return null;
    }

    private static PositionQualifier ParsePositionQualifier(string[] lines)
    {
        foreach (string line in lines)
        {
            Match match = PositionQualifierPattern().Match(line);
            if (match.Success)
            {
                string word = match.Groups["qualifier"].Value;
                return
                    word.StartsWith("Init", StringComparison.OrdinalIgnoreCase) ? PositionQualifier.Init :
                    word.StartsWith("Aver", StringComparison.OrdinalIgnoreCase) ? PositionQualifier.Average :
                    PositionQualifier.Held;
            }

            if (AveragedPositionLabelPattern().IsMatch(line))
            {
                return PositionQualifier.Average;
            }
        }

        return PositionQualifier.Unknown;
    }

    // -----------------------------------------------------------------------------------------
    // Health
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the health item list, which the receiver prints as label-and-verdict pairs on one or
    /// more lines below the health banner.
    /// </summary>
    /// <remarks>
    /// Pairs are separated by runs of two or more spaces while labels contain single spaces
    /// ("Self Test", "Int Pwr"), so the run length is what splits them. The labels are kept as the
    /// device spells them rather than mapped onto a fixed set of properties: the list differs across
    /// the family, and an item this build has never seen must still reach the Diagnostics page.
    /// </remarks>
    private static IReadOnlyDictionary<string, bool> ParseHealthItems(string[] lines, List<string> warnings)
    {
        Dictionary<string, bool> items = new(StringComparer.OrdinalIgnoreCase);

        int bannerIndex = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("HEALTH MONITOR", StringComparison.OrdinalIgnoreCase))
            {
                bannerIndex = i;
                break;
            }
        }

        if (bannerIndex < 0)
        {
            return items;
        }

        for (int i = bannerIndex + 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            foreach (string chunk in PairSeparatorPattern().Split(lines[i].Trim()))
            {
                int colon = chunk.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                string label = chunk[..colon].Trim();
                string verdict = chunk[(colon + 1)..].Trim();
                if (label.Length == 0 || verdict.Length == 0)
                {
                    continue;
                }

                bool ok = verdict.Equals("OK", StringComparison.OrdinalIgnoreCase);
                if (!ok && !IsKnownFailureVerdict(verdict))
                {
                    warnings.Add($"Unrecognised health verdict '{verdict}' for '{label}'; treated as a failure.");
                }

                items[label] = ok;
            }
        }

        return items;
    }

    private static bool IsKnownFailureVerdict(string verdict) =>
        verdict.Equals("BAD", StringComparison.OrdinalIgnoreCase)
        || verdict.Equals("FAIL", StringComparison.OrdinalIgnoreCase)
        || verdict.Equals("FAILED", StringComparison.OrdinalIgnoreCase)
        || verdict.Equals("ERR", StringComparison.OrdinalIgnoreCase);

    // -----------------------------------------------------------------------------------------
    // Scalar helpers
    // -----------------------------------------------------------------------------------------

    /// <summary>Finds the first line matching <paramref name="pattern"/> and returns its integer group.</summary>
    private static int? FindInteger(string[] lines, Regex pattern)
    {
        foreach (string line in lines)
        {
            Match match = pattern.Match(line);
            if (match.Success
                && int.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the first line matching <paramref name="pattern"/> and returns its value converted to
    /// <paramref name="target"/>, using the unit the receiver printed beside it.
    /// </summary>
    /// <remarks>
    /// The unit is read rather than assumed because the receiver switches it with the magnitude:
    /// the same holdover field reads <c>2.5 us</c> on one screen and <c>1.4 ms</c> on another, and a
    /// fixed scale factor would be wrong by a thousand exactly when the number matters most.
    /// </remarks>
    private static double? FindScaledValue(string[] lines, Regex pattern, UnitScale target)
    {
        foreach (string line in lines)
        {
            Match match = pattern.Match(line);
            if (!match.Success
                || !double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                continue;
            }

            double? seconds = ToSeconds(value, match.Groups["unit"].Value);
            if (seconds is null)
            {
                continue;
            }

            return target == UnitScale.Nanoseconds ? seconds.Value * 1e9 : seconds.Value;
        }

        return null;
    }

    private static double? ToSeconds(double value, string unit) => unit.Trim().ToLowerInvariant() switch
    {
        "ps" => value * 1e-12,
        "ns" => value * 1e-9,
        "us" or "µs" or "μs" => value * 1e-6,
        "ms" => value * 1e-3,
        "s" or "sec" => value,
        _ => null,
    };

    /// <summary>Which unit <see cref="FindScaledValue"/> should hand back, matching the model's member.</summary>
    private enum UnitScale
    {
        Seconds,
        Nanoseconds,
    }

    // -----------------------------------------------------------------------------------------
    // Text helpers
    // -----------------------------------------------------------------------------------------

    /// <summary>Splits a line into whitespace-delimited tokens, keeping each one's character extent.</summary>
    private static List<(string Text, int Start, int End)> Tokenize(string line)
    {
        List<(string, int, int)> tokens = [];
        int i = 0;

        while (i < line.Length)
        {
            if (char.IsWhiteSpace(line[i]))
            {
                i++;
                continue;
            }

            int start = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i]))
            {
                i++;
            }

            tokens.Add((line[start..i], start, i - 1));
        }

        return tokens;
    }

    /// <summary>
    /// Returns the text up to the first run of three or more spaces, which is how the screen
    /// separates two side-by-side panels sharing a physical line.
    /// </summary>
    private static string CutAtWideGap(string text)
    {
        Match gap = WideGapPattern().Match(text);
        return gap.Success ? text[..gap.Index] : text;
    }

    /// <summary>Trims a value and reduces internal whitespace runs to single spaces.</summary>
    private static string? CollapseSpaces(string? text) =>
        text is null ? null : WhitespaceRunPattern().Replace(text, " ").Trim();

    private static int? ParseIntOrNull(string? text) =>
        text is not null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;

    // -----------------------------------------------------------------------------------------
    // Column model
    // -----------------------------------------------------------------------------------------

    /// <summary>A character range within a line, with both ends inclusive.</summary>
    private readonly record struct FieldExtent(int Start, int End)
    {
        /// <summary>
        /// Returns this field's text from <paramref name="line"/>, or null if the line is too short
        /// or the field is blank.
        /// </summary>
        /// <remarks>
        /// Short lines are ordinary rather than exceptional: the receiver stops padding once the
        /// right-hand panel runs out of content, so the last rows of a long not-tracking table are
        /// routinely shorter than the header that defined the columns.
        /// </remarks>
        public string? Slice(string line)
        {
            if (Start >= line.Length)
            {
                return null;
            }

            int end = Math.Min(End, line.Length - 1);
            if (end < Start)
            {
                return null;
            }

            string text = line[Start..(end + 1)].Trim();
            return text.Length == 0 ? null : text;
        }
    }

    /// <summary>One <c>PRN / El / Az [/ C-N]</c> column group within the acquisition table.</summary>
    private sealed record ColumnGroup(
        FieldExtent Prn,
        FieldExtent? Elevation,
        FieldExtent? Azimuth,
        FieldExtent? Strength,
        string? StrengthLabel);

    // -----------------------------------------------------------------------------------------
    // Patterns
    // -----------------------------------------------------------------------------------------
    //
    // Source-generated rather than interpreted: these run on every poll for the life of the session,
    // and the generator also turns a malformed pattern into a compile error rather than a first-poll
    // exception, which matters more here than the speed does.

    [GeneratedRegex(@"\bTFOM\s+(?<value>[-+]?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TfomPattern();

    [GeneratedRegex(@"\bFFOM\s+(?<value>[-+]?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex FfomPattern();

    [GeneratedRegex(@"\bELEV\s+MASK\s+(?<value>[-+]?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ElevationMaskPattern();

    [GeneratedRegex(@"(?<!Not\s)\bTracking:\s*(?<value>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TrackingCountPattern();

    [GeneratedRegex(@"\bNot\s+Tracking:\s*(?<value>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex NotTrackingCountPattern();

    [GeneratedRegex(@"\b1PPS\s+TI\s+(?<value>[-+]?[\d.]+)\s*(?<unit>ps|ns|us|µs|μs|ms|s)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OnePpsTiPattern();

    [GeneratedRegex(@"\bHOLD\s+THR\s+(?<value>[-+]?[\d.]+)\s*(?<unit>ps|ns|us|µs|μs|ms|s)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HoldThresholdPattern();

    [GeneratedRegex(@"\bPredict\w*\s+(?<value>[-+]?[\d.]+)\s*(?<unit>ps|ns|us|µs|μs|ms|s)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HoldoverPredictPattern();

    [GeneratedRegex(@"\bPresent\s+(?<value>[-+]?[\d.]+)\s*(?<unit>ps|ns|us|µs|μs|ms|s)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HoldoverPresentPattern();

    /// <summary>
    /// The elapsed-holdover row, as <c>Holdover Duration:  0m 03s</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Minutes and seconds are required because both the captured screen and the Z3801A guide's
    /// Figure 3-4 print them, and neither pads the minutes. Hours and days are accepted ahead of
    /// them but <b>not</b> confirmed: no capture has run long enough to show what this prints past
    /// an hour, so the leading groups are a tolerance rather than a claim. If a long holdover ever
    /// turns out to print something else, this returns null and the field reads as a dash, which is
    /// the §11.1 behaviour and not a regression.
    /// </para>
    /// <para>
    /// The row shares its line with <c>Present  1.0 us</c>, whose unit ends in <c>s</c>. Matching
    /// left to right from the label consumes the duration and stops before reaching it.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"\bHoldover\s+Duration:\s*(?:(?<days>\d+)\s*d\s+)?(?:(?<hours>\d+)\s*h\s+)?" +
        @"(?<minutes>\d+)\s*m\s+(?<seconds>\d+)\s*s\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex HoldoverDurationPattern();

    [GeneratedRegex(@"\bANT\s+DLY\s+(?<value>[-+]?[\d.]+)\s*(?<unit>ps|ns|us|µs|μs|ms|s)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AntennaDelayPattern();

    /// <summary>
    /// A clock row: a time scale, a time of day, an optional power-up marker, and a date.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two shapes of the same row, both seen in the field (#245).
    /// </para>
    /// <para>
    /// <b>The provisional marker.</b> Between the time and the date the receiver may print
    /// <c>(?)</c> — printed <c>[?]</c> in the Z3801A user guide's Figure 3-1, which is the same
    /// field on a sibling model. It means the time is the <b>default power-up value, not yet
    /// corrected from GPS</b>, and the guide says it is corrected once the first satellite is
    /// tracked. It is captured rather than merely tolerated — see
    /// <see cref="ReceiverStatus.DeviceTimeIsProvisional"/> for why reading the value while
    /// discarding the marker would be worse than not reading it at all.
    /// </para>
    /// <para>
    /// <b>Two date orders.</b> Every screen captured from this unit prints <c>12 Jan 2007</c>, but
    /// the 58503A and Z3801A manuals both print <c>1994 DEC 01</c> — year first, day last. §11.1's
    /// header-relative parsing exists to survive exactly this kind of cross-model difference, and
    /// the alternation costs nothing on a unit that never emits the second form.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"(?<scale>\b(?:UTC|GPS|LOCAL(?:\s+GPS)?|LCL(?:\s+GPS)?)\b)\s+(?<time>\d{1,2}:\d{2}:\d{2})\s*" +
        @"(?<provisional>[(\[]\s*\?\s*[)\]])?\s*" +
        @"(?:(?<day>\d{1,2})\s+(?<month>[A-Za-z]{3})\s+(?<year>\d{4})" +
        @"|(?<year>\d{4})\s+(?<month>[A-Za-z]{3})\s+(?<day>\d{1,2}))",
        RegexOptions.IgnoreCase)]
    private static partial Regex DeviceTimePattern();

    /// <summary>
    /// The shape of a clock row, without requiring the date to follow the time.
    /// </summary>
    /// <remarks>
    /// Used only to tell "the row is missing" from "the row is there and I could not read it", so it
    /// is deliberately looser than <see cref="DeviceTimePattern"/> and is never used to extract a
    /// value. A time of day after a scale name is enough to identify the line — <c>GPS 1PPS
    /// Synchronized to UTC</c> starts with a scale name too and carries no clock.
    /// </remarks>
    [GeneratedRegex(
        @"\b(?:UTC|GPS|LOCAL(?:\s+GPS)?|LCL(?:\s+GPS)?)\b\s+\d{1,2}:\d{2}:\d{2}",
        RegexOptions.IgnoreCase)]
    private static partial Regex ClockRowShapePattern();

    [GeneratedRegex(@"(?:GPS\s+1PPS|1PPS\s+CLK)\s+(?<advisory>\S.*?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ClockAdvisoryPattern();

    [GeneratedRegex(@"\bLEAP\b[^-+]*(?<sign>[-+])", RegexOptions.IgnoreCase)]
    private static partial Regex LeapPendingPattern();

    [GeneratedRegex(
        @"\b(?<label>LAT|LON)\b\s+(?<hemisphere>[NSEW])\s*(?<deg>\d{1,3}):(?<min>\d{1,2}):(?<sec>[\d.]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex AnglePattern();

    [GeneratedRegex(@"\bHGT\b\s+(?<value>[-+]?[\d.]+)\s*m\b\s*(?<datum>\([^)]*\))?", RegexOptions.IgnoreCase)]
    private static partial Regex HeightPattern();

    [GeneratedRegex(@"\bMODE\b\s+(?<body>\S.*?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PositionModePattern();

    [GeneratedRegex(@"(?<value>[\d.]+)\s*%")]
    private static partial Regex PercentPattern();

    [GeneratedRegex(@"\bSuspended:\s*(?<reason>\S.*?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SuspendedPattern();

    [GeneratedRegex(@"\((?<qualifier>Init\w*|Aver\w*|Held)\)", RegexOptions.IgnoreCase)]
    private static partial Regex PositionQualifierPattern();

    /// <summary>
    /// The SmartClock family's own way of saying the position is a survey average.
    /// </summary>
    /// <remarks>
    /// Anchored on the label rather than the line, because the position block shares its lines with
    /// the satellite table — <c>AVG LAT</c> appears after eight columns of PRN, elevation and
    /// azimuth on a screen tracking eight satellites.
    /// </remarks>
    [GeneratedRegex(@"\bAVG\s+(?:LAT|LON|HGT)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AveragedPositionLabelPattern();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex PairSeparatorPattern();

    [GeneratedRegex(@" {3,}")]
    private static partial Regex WideGapPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRunPattern();
}
