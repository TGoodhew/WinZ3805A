using System.Text;
using Microsoft.Extensions.Time.Testing;
using WinZ3805A.Device.Models;
using WinZ3805A.Device.Parsing;

namespace WinZ3805A.Tests.Parsing;

/// <summary>
/// Asserts what a captured status screen says (§11, P0-4).
/// </summary>
/// <remarks>
/// <para>
/// The assertions against <c>locked-stabilizing.txt</c> are the important ones: it is real output
/// from the unit named in <c>Fixtures/README.md</c>, and every value below was cross-checked against
/// the scalar queries taken in the same session.
/// </para>
/// <para>
/// The remaining tests build small screens in code to exercise a rule the one available capture
/// cannot reach — an <c>SS</c> column, a single column group, a screen that is not a screen at all.
/// They are deliberately <em>not</em> written into <c>Fixtures/</c>: that folder is captured device
/// output and nothing else, and a synthesised file sitting among real ones would be believed later.
/// </para>
/// </remarks>
public class StatusScreenParserTests
{
    /// <summary>
    /// The instant the fixture was taken, to the second, from its own clock row corrected by the
    /// §7.4 rollover. Pinning the clock here is what makes the rollover assertions mean anything.
    /// </summary>
    private static readonly DateTimeOffset s_captureInstant = new(2026, 8, 12, 14, 45, 2, TimeSpan.Zero);

    private static StatusScreenParser ParserAt(DateTimeOffset now) => new(new FakeTimeProvider(now));

    // ---------------------------------------------------------------------------------------
    // The captured screen
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheCapturedScreenReportsItsSynchronizationState()
    {
        ReceiverStatus status = ParseFixture("locked-stabilizing.txt");

        Assert.Equal(OutputValidity.ValidReduced, status.Outputs);
        Assert.Equal(SmartClockMode.Locked, status.Mode);
        Assert.Equal("stabilizing frequency", status.ModeDetail);
        Assert.Equal(3, status.Tfom);
        Assert.Equal(1, status.Ffom);
    }

    /// <summary>
    /// The mode row shares its line with the reference-outputs panel, so a detail of
    /// "stabilizing frequency TFOM 3 FFOM 1" is the failure this guards against.
    /// </summary>
    [Fact]
    public void TheModeDetailStopsAtTheEdgeOfItsPanel()
    {
        ReceiverStatus status = ParseFixture("locked-stabilizing.txt");

        Assert.DoesNotContain("TFOM", status.ModeDetail);
        Assert.DoesNotContain("FFOM", status.ModeDetail);
    }

    [Fact]
    public void TheCapturedScreenReportsItsTimingFigures()
    {
        ReceiverStatus status = ParseFixture("locked-stabilizing.txt");

        // -5.4 ns, cross-checked against :SYNC:TINT? answering -5.4E-009 in the same session.
        Assert.NotNull(status.OnePpsTiNanoseconds);
        Assert.Equal(-5.4, status.OnePpsTiNanoseconds.Value, 6);

        // "HOLD THR 1.000 us" and "Predict  2.5 us/initial 24 hrs" — both read in the unit printed.
        Assert.NotNull(status.HoldThresholdSeconds);
        Assert.Equal(1e-6, status.HoldThresholdSeconds.Value, 15);
        Assert.NotNull(status.HoldoverPredictedSeconds);
        Assert.Equal(2.5e-6, status.HoldoverPredictedSeconds.Value, 15);

        // The screen shows no present-uncertainty row and no holdover elapsed time.
        Assert.Null(status.HoldoverPresentSeconds);
        Assert.Null(status.HoldoverDuration);

        // 77 ns, against :GPS:REF:ADEL? answering +7.70000E-008.
        Assert.NotNull(status.AntennaDelayNanoseconds);
        Assert.Equal(77, status.AntennaDelayNanoseconds.Value, 6);
    }

    [Fact]
    public void TheCapturedScreenReportsOneTrackedSatelliteWithItsSignalStrength()
    {
        ReceiverStatus status = ParseFixture("locked-stabilizing.txt");

        Assert.True(status.GpsOnePpsValid);
        Assert.Equal(SignalStrengthKind.CarrierToNoise, status.SignalStrengthKind);
        Assert.Equal(10, status.ElevationMaskDegrees);

        TrackedSatellite satellite = Assert.Single(status.Tracked);
        Assert.Equal(18, satellite.Prn);
        Assert.Equal(79, satellite.ElevationDegrees);
        Assert.Equal(2, satellite.AzimuthDegrees);
        Assert.Equal(32, satellite.SignalStrength);
    }

    /// <summary>
    /// The nine not-tracked satellites, in screen order. Four of them have a three-digit azimuth
    /// under a two-character <c>Az</c> header, which is the case that breaks any parser slicing the
    /// header token's own extent — 219 would read as 19.
    /// </summary>
    [Fact]
    public void TheCapturedScreenReportsNineNotTrackedSatellitesIncludingWideAzimuths()
    {
        ReceiverStatus status = ParseFixture("locked-stabilizing.txt");

        (int Prn, int Elevation, int Azimuth)[] expected =
        [
            (5, 25, 50),
            (10, 21, 219),
            (15, 42, 108),
            (16, 31, 290),
            (20, 31, 68),
            (23, 53, 215),
            (26, 26, 256),
            (27, 15, 311),
            (29, 41, 143),
        ];

        Assert.Equal(expected.Length, status.NotTracked.Count);
        Assert.Equal(
            expected,
            status.NotTracked
                .Select(s => (s.Prn, s.ElevationDegrees ?? -1, s.AzimuthDegrees ?? -1))
                .ToArray());
    }

    [Fact]
    public void TheCapturedScreenReportsItsTimeAndAdvisory()
    {
        ReceiverStatus status = ParseFixture("locked-stabilizing.txt");

        Assert.Equal(TimeScale.Utc, status.TimeScale);
        Assert.Equal(new DateTimeOffset(2006, 12, 27, 14, 45, 2, TimeSpan.Zero), status.DeviceDateTime);
        Assert.Equal(ClockAdvisory.SynchronizedToUtc, status.OnePpsClockAdvisory);
        Assert.Equal(LeapSecondPending.None, status.LeapPending);
    }

    /// <summary>
    /// This unit reports 27 December 2006 — one 1024-week epoch behind — which is the exact case
    /// P0-10 names. With the clock pinned to the capture instant the delta is a whole epoch to the
    /// second, so the correction lands on the capture date itself.
    /// </summary>
    [Fact]
    public void TheCapturedScreenIsOneGpsEpochBehindAndIsCorrected()
    {
        ReceiverStatus status = ParseFixture("locked-stabilizing.txt");

        Assert.Equal(1, status.WeekRolloverEpochs);
        Assert.Equal(s_captureInstant, status.CorrectedDateTime);

        // §7.4 forbids substituting the correction for what the hardware said.
        Assert.Equal(new DateTimeOffset(2006, 12, 27, 14, 45, 2, TimeSpan.Zero), status.DeviceDateTime);
    }

    [Fact]
    public void TheCapturedScreenReportsAHeldPosition()
    {
        ReceiverStatus status = ParseFixture("locked-stabilizing.txt");

        Assert.Equal(PositionMode.Hold, status.PositionMode);
        Assert.Null(status.SurveyPercentComplete);
        Assert.Equal(SurveySuspendedReason.None, status.SurveySuspendedReason);

        Assert.NotNull(status.Position);
        Assert.NotNull(status.Position.LatitudeDegrees);
        Assert.NotNull(status.Position.LongitudeDegrees);
        Assert.NotNull(status.Position.HeightMetres);

        // N 47:31:18.822 and W 122:12:22.152 — west of Greenwich, so the longitude is negative.
        Assert.Equal(47.521895, status.Position.LatitudeDegrees.Value, 6);
        Assert.Equal(-122.206153, status.Position.LongitudeDegrees.Value, 6);
        Assert.Equal(38.0, status.Position.HeightMetres.Value, 6);
        Assert.Equal(HeightDatum.Msl, status.HeightDatum);
    }

    [Fact]
    public void TheCapturedScreenReportsEveryHealthItemPassing()
    {
        ReceiverStatus status = ParseFixture("locked-stabilizing.txt");

        Assert.True(status.HealthOk);
        Assert.Equal(6, status.HealthItems.Count);
        Assert.All(status.HealthItems, item => Assert.True(item.Value));

        foreach (string label in (string[])["Self Test", "Int Pwr", "Oven Pwr", "OCXO", "EFC", "GPS Rcv"])
        {
            Assert.True(status.HealthItems.ContainsKey(label), $"Expected a health item named '{label}'.");
        }
    }

    /// <summary>
    /// Nothing on a screen this ordinary should be reported as odd. A warning here means a field
    /// silently stopped parsing, which is the regression this whole file exists to catch.
    /// </summary>
    [Fact]
    public void TheCapturedScreenParsesWithNoWarnings()
    {
        ReceiverStatus status = ParseFixture("locked-stabilizing.txt");

        Assert.Empty(status.ParseWarnings);
    }

    [Fact]
    public void TheCaptureInstantComesFromTheInjectedClock()
    {
        ReceiverStatus status = ParseFixture("locked-stabilizing.txt");

        Assert.Equal(s_captureInstant, status.CapturedAt);
    }

    // ---------------------------------------------------------------------------------------
    // Rules the one capture cannot reach
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A 59551A-class unit labels the column <c>SS</c> on a 0–255 scale rather than <c>C/N</c> on
    /// 26–55. §11.1 is explicit that the two are not interchangeable, so which one was seen has to
    /// survive into the model.
    /// </summary>
    [Fact]
    public void AnSsColumnIsRecordedAsADifferentScaleFromCarrierToNoise()
    {
        ReceiverStatus status = ParserAt(s_captureInstant).Parse(string.Join("\r\n",
        [
            "ACQUISITION ................................................ [ GPS 1PPS Valid ]",
            "Tracking: 1 ____   Not Tracking: 0 ________",
            "PRN  El  Az  SS ",
            " 18  79 219 212",
        ]));

        Assert.Equal(SignalStrengthKind.SignalStrength, status.SignalStrengthKind);

        TrackedSatellite satellite = Assert.Single(status.Tracked);
        Assert.Equal(212, satellite.SignalStrength);
        Assert.Equal(219, satellite.AzimuthDegrees);
    }

    /// <summary>A screen with only a tracking group still parses; the not-tracked list is simply empty.</summary>
    [Fact]
    public void ASingleColumnGroupParsesWithNoPredictedSatellites()
    {
        ReceiverStatus status = ParserAt(s_captureInstant).Parse(string.Join("\r\n",
        [
            "ACQUISITION ................................................ [ GPS 1PPS Valid ]",
            "Tracking: 2 ____   Not Tracking: 0 ________",
            "PRN  El  Az  C/N",
            " 18  79   2   32",
            "  5  25  50   41",
        ]));

        Assert.Equal(2, status.Tracked.Count);
        Assert.Empty(status.NotTracked);
        Assert.Equal(SignalStrengthKind.CarrierToNoise, status.SignalStrengthKind);
    }

    /// <summary>
    /// The counts the receiver prints above the table are its own view of the world, so a
    /// disagreement with the rows means the column model has slipped on this firmware — worth a
    /// warning in Diagnostics rather than a quietly wrong sky plot.
    /// </summary>
    [Fact]
    public void ARowCountThatContradictsTheHeaderIsWarnedAbout()
    {
        ReceiverStatus status = ParserAt(s_captureInstant).Parse(string.Join("\r\n",
        [
            "ACQUISITION ................................................ [ GPS 1PPS Valid ]",
            "Tracking: 4 ____   Not Tracking: 0 ________",
            "PRN  El  Az  C/N",
            " 18  79   2   32",
        ]));

        Assert.Single(status.Tracked);
        Assert.Contains(status.ParseWarnings, w => w.Contains("4 tracked", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Synchronized to UTC", ClockAdvisory.SynchronizedToUtc)]
    [InlineData("Synchronized to GPS Time", ClockAdvisory.SynchronizedToGpsTime)]
    [InlineData("Assessing stability", ClockAdvisory.AssessingStability)]
    [InlineData("Assessing stability...", ClockAdvisory.AssessingStability)]
    [InlineData("Questionable accuracy", ClockAdvisory.QuestionableAccuracy)]
    [InlineData("Inaccurate: not tracking", ClockAdvisory.InaccurateNotTracking)]
    [InlineData("Inaccurate: inacc position", ClockAdvisory.InaccurateInaccuratePosition)]
    [InlineData("Absent or freq error", ClockAdvisory.AbsentOrFrequencyError)]
    [InlineData("Invalid: GPS rcvr err", ClockAdvisory.InvalidGpsReceiverError)]
    public void EveryAdvisoryInTheSpecificationDecodesToItsOwnValue(string advisory, ClockAdvisory expected)
    {
        ReceiverStatus status = ParserAt(s_captureInstant).Parse($"GPS 1PPS {advisory}");

        Assert.Equal(expected, status.OnePpsClockAdvisory);

        // A recognised advisory is not an anomaly. The other warnings this one-line screen raises
        // — no health banner, no table, no clock row — are all correct and beside the point.
        Assert.DoesNotContain(status.ParseWarnings, w => w.Contains("advisory", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §11.3 keeps no string form of the advisory on the model, so an unfamiliar one would vanish
    /// entirely if the parser did not quote it. That warning is what makes a field report about an
    /// odd firmware revision answerable.
    /// </summary>
    [Fact]
    public void AnUnrecognisedAdvisoryIsQuotedInTheWarnings()
    {
        ReceiverStatus status = ParserAt(s_captureInstant).Parse("GPS 1PPS Assessing drift");

        Assert.Equal(ClockAdvisory.Other, status.OnePpsClockAdvisory);
        Assert.Contains(status.ParseWarnings, w => w.Contains("Assessing drift", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Suspended: track <4 sats", SurveySuspendedReason.TooFewSatellites)]
    [InlineData("Suspended: poor geometry", SurveySuspendedReason.PoorGeometry)]
    [InlineData("Suspended: no track data", SurveySuspendedReason.NoTrackData)]
    public void ASuspendedSurveyReportsWhy(string line, SurveySuspendedReason expected)
    {
        ReceiverStatus status = ParserAt(s_captureInstant).Parse(string.Join("\r\n",
        [
            "MODE     Survey: 45% complete",
            line,
        ]));

        Assert.Equal(PositionMode.Survey, status.PositionMode);
        Assert.Equal(45d, status.SurveyPercentComplete);
        Assert.Equal(expected, status.SurveySuspendedReason);
    }

    /// <summary>
    /// A gap that is not close to a whole number of epochs is a receiver with its date set wrongly,
    /// not a rollover, and §7.4's ±7 day tolerance is what tells them apart.
    /// </summary>
    [Fact]
    public void ADateThatIsWrongButNotByAWholeEpochIsNotCorrected()
    {
        ReceiverStatus status = ParserAt(s_captureInstant).Parse("UTC      14:45:02     27 Dec 2010");

        Assert.Equal(0, status.WeekRolloverEpochs);
        Assert.Equal(status.DeviceDateTime, status.CorrectedDateTime);
    }

    [Fact]
    public void ACurrentDateIsLeftAlone()
    {
        ReceiverStatus status = ParserAt(s_captureInstant).Parse("UTC      14:45:02     12 Aug 2026");

        Assert.Equal(0, status.WeekRolloverEpochs);
        Assert.Equal(s_captureInstant, status.CorrectedDateTime);
    }

    // ---------------------------------------------------------------------------------------
    // §11.1: the parser never throws
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\r\n")]
    [InlineData("not a status screen at all")]
    [InlineData("PRN")]
    [InlineData("PRN  El  Az  C/N")]
    [InlineData("\0ÿ[2J")]
    public void NothingUnparseableThrows(string? screen)
    {
        ReceiverStatus status = ParserAt(s_captureInstant).Parse(screen);

        Assert.NotNull(status);
        Assert.Equal(s_captureInstant, status.CapturedAt);
        Assert.Empty(status.Tracked);
        Assert.Empty(status.NotTracked);
    }

    /// <summary>
    /// A screen truncated mid-table — which a timeout on a slow link produces — keeps the rows that
    /// did arrive rather than discarding the lot.
    /// </summary>
    [Fact]
    public void ATruncatedScreenKeepsWhatArrived()
    {
        string[] full = ReadFixtureLines("locked-stabilizing.txt");
        string truncated = string.Join("\r\n", full.Take(15));

        ReceiverStatus status = ParserAt(s_captureInstant).Parse(truncated);

        Assert.Equal(SmartClockMode.Locked, status.Mode);
        Assert.Single(status.Tracked);
        Assert.Equal(3, status.NotTracked.Count);

        // What is missing is reported rather than passed off as fine.
        Assert.NotEmpty(status.ParseWarnings);
    }

    /// <summary>A row whose columns hold dashes rather than numbers degrades to nulls, not an exception.</summary>
    [Fact]
    public void UnreadableColumnsBecomeNulls()
    {
        ReceiverStatus status = ParserAt(s_captureInstant).Parse(string.Join("\r\n",
        [
            "Tracking: 1 ____   Not Tracking: 0 ________",
            "PRN  El  Az  C/N",
            " 18  --  --   --",
        ]));

        TrackedSatellite satellite = Assert.Single(status.Tracked);
        Assert.Equal(18, satellite.Prn);
        Assert.Null(satellite.ElevationDegrees);
        Assert.Null(satellite.AzimuthDegrees);
        Assert.Null(satellite.SignalStrength);
    }

    // ---------------------------------------------------------------------------------------

    private static ReceiverStatus ParseFixture(string name) =>
        ParserAt(s_captureInstant).Parse(string.Join("\r\n", ReadFixtureLines(name)));

    // -------------------------------------------------------------------------------------
    // Satellites the receiver is attempting to track (#4)
    // -------------------------------------------------------------------------------------

    /// <summary>A starred PRN is a satellite, not a parse failure.</summary>
    /// <remarks>
    /// <para>
    /// While acquiring, the receiver marks satellites it is trying to lock onto with a leading
    /// asterisk, and explains it in the screen's own legend: <c>*attempting to track</c>. Read as a
    /// plain integer that row yields null and the whole satellite is dropped.
    /// </para>
    /// <para>
    /// <b>The screen contradicted itself and nothing noticed.</b> The captured power-up screen says
    /// <c>Not Tracking: 10</c> and the parser produced five — because five of the ten were starred.
    /// It took a receiver being power-cycled under a clear sky to produce the state at all, which is
    /// exactly what #4 exists for.
    /// </para>
    /// </remarks>
    [Fact]
    public void SatellitesTheReceiverIsAttemptingToTrackAreNotDropped()
    {
        ReceiverStatus status = ParseFixture("captured/power-up-gps-acquisition.txt");

        Assert.Equal(10, status.NotTracked.Count);
        Assert.Equal(5, status.NotTracked.Count(s => s.AttemptingToTrack));

        // The starred five, by PRN, so a regression that keeps the count but loses the marker fails.
        Assert.Equal(
            [15, 19, 20, 22, 24],
            status.NotTracked.Where(s => s.AttemptingToTrack).Select(s => s.Prn).Order());

        // And a starred row keeps its other columns rather than being half-parsed.
        PredictedSatellite fifteen = status.NotTracked.Single(s => s.Prn == 15);
        Assert.Equal(29, fifteen.ElevationDegrees);
        Assert.Equal(271, fifteen.AzimuthDegrees);
    }

    // -------------------------------------------------------------------------------------
    // The four states captured on the 27 Aug 2026 backyard sitting (#4, #185)
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// When the sitting captured its screens. Distinct from <see cref="s_captureInstant"/> because
    /// the §7.4 rollover correction is a function of "now", and asserting a corrected date against
    /// the wrong instant would assert the constant rather than the arithmetic.
    /// </summary>
    private static readonly DateTimeOffset s_sittingInstant = new(2026, 8, 28, 5, 15, 0, TimeSpan.Zero);

    private static ReceiverStatus ParseSittingFixture(string name) =>
        ParserAt(s_sittingInstant).Parse(string.Join("\r\n", ReadFixtureLines(name)));

    /// <summary>A survey in progress — the state #4 lists and the corpus had no assertions for.</summary>
    /// <remarks>
    /// Taken at 1.9 % of the two-hour survey that ran overnight on 27 Aug. It is the only screen in
    /// the corpus that is not a held position, which makes it the only one exercising the survey
    /// half of §11.2 at all.
    /// </remarks>
    [Fact]
    public void TheSurveyingScreenParses()
    {
        ReceiverStatus status = ParseSittingFixture("captured/surveying-locked-to-gps-stabilizing-frequency.txt");

        Assert.Equal(SmartClockMode.Locked, status.Mode);
        Assert.Equal("stabilizing frequency", status.ModeDetail);
        Assert.Equal(OutputValidity.ValidReduced, status.Outputs);
        Assert.Equal(4, status.Tfom);
        Assert.Equal(1, status.Ffom);

        Assert.Equal(-22.9, status.OnePpsTiNanoseconds!.Value, 10);
        Assert.Equal(1e-6, status.HoldThresholdSeconds!.Value, 12);
        Assert.Equal(432e-6, status.HoldoverPredictedSeconds!.Value, 12);

        Assert.True(status.GpsOnePpsValid);
        Assert.Equal(8, status.Tracked.Count);
        Assert.Equal(2, status.NotTracked.Count);
        Assert.Equal(10, status.ElevationMaskDegrees);
        Assert.Equal(SignalStrengthKind.CarrierToNoise, status.SignalStrengthKind);
        Assert.Equal(77, status.AntennaDelayNanoseconds);

        // The survey, which is the point of this fixture.
        Assert.Equal(PositionMode.Survey, status.PositionMode);
        Assert.Equal(1.9, status.SurveyPercentComplete);
        Assert.Equal(SurveySuspendedReason.None, status.SurveySuspendedReason);

        // AVG LAT / AVG LON / AVG HGT: a running average, not a held position.
        Assert.Equal(PositionQualifier.Average, status.PositionQualifier);
        Assert.Equal(HeightDatum.Msl, status.HeightDatum);
        Assert.Equal(30.47, status.Position!.HeightMetres);

        Assert.True(status.HealthOk);
        Assert.Equal(6, status.HealthItems.Count);
        Assert.All(status.HealthItems, item => Assert.True(item.Value));
        Assert.Empty(status.ParseWarnings);
    }

    /// <summary>The week rollover, checked against a screen whose real capture time is known.</summary>
    /// <remarks>
    /// The receiver printed <c>12 Jan 2007</c>; the screen was taken at about 22:12 on 27 Aug 2026
    /// Pacific, which is 05:12 UTC on the 28th. One 1024-week epoch is the whole correction, and the
    /// minutes and seconds have to survive it — the strongest rollover evidence in the corpus,
    /// because the truth is independently known from the application log.
    /// </remarks>
    [Fact]
    public void TheSurveyingScreensRolledOverDateIsCorrected()
    {
        ReceiverStatus status = ParseSittingFixture("captured/surveying-locked-to-gps-stabilizing-frequency.txt");

        Assert.Equal(TimeScale.Utc, status.TimeScale);
        Assert.Equal(new DateTimeOffset(2007, 1, 12, 5, 12, 20, TimeSpan.Zero), status.DeviceDateTime);
        Assert.Equal(1, status.WeekRolloverEpochs);
        Assert.Equal(new DateTimeOffset(2026, 8, 28, 5, 12, 20, TimeSpan.Zero), status.CorrectedDateTime);
        Assert.Equal(ClockAdvisory.SynchronizedToUtc, status.OnePpsClockAdvisory);
    }

    /// <summary>Power-up, which has no clock yet and must say so rather than inventing one.</summary>
    /// <remarks>
    /// <b>The fixture that exercises §11.1 hardest.</b> A receiver seconds from cold has no time row
    /// on the screen at all, so the date, the time scale and the 1 PPS reading are all genuinely
    /// absent. The requirement is that they come back null and the reason is recorded — not that the
    /// parse fails, and not that a plausible value is manufactured.
    /// </remarks>
    [Fact]
    public void ThePowerUpScreenParsesWithoutAClock()
    {
        ReceiverStatus status = ParseSittingFixture("captured/power-up-fine-freq-adj.txt");

        Assert.Equal(SmartClockMode.PowerUp, status.Mode);
        Assert.Equal("fine freq adj", status.ModeDetail);
        Assert.Equal(OutputValidity.Invalid, status.Outputs);
        Assert.Equal(9, status.Tfom);
        Assert.Equal(3, status.Ffom);

        // Absent, and null rather than zero - a 1 PPS offset of 0 ns would read as a perfect lock.
        Assert.Null(status.OnePpsTiNanoseconds);
        Assert.Null(status.DeviceDateTime);
        Assert.Null(status.CorrectedDateTime);
        Assert.Equal(TimeScale.Unknown, status.TimeScale);

        // And the reason is surfaced rather than swallowed - §11.1 puts ParseWarnings in Diagnostics
        // precisely so an odd firmware revision is actionable instead of merely quiet.
        //
        // Specifically: the row is THERE and unreadable, not missing (#245). This screen prints
        // "UTC 05:10:26 (?) 12 Jan 2007", where (?) marks a provisional power-up time; the marker
        // between time and date defeats the pattern. Asserting only that some warning mentions a
        // clock row would pass for either message, which is what it used to do.
        string warning = Assert.Single(status.ParseWarnings);
        Assert.Contains("did not parse", warning, StringComparison.Ordinal);
        Assert.Contains("05:10:26 (?) 12 Jan 2007", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("No clock row was found", warning, StringComparison.Ordinal);

        // The survey the power cycle started, three tenths of a per cent in (#229).
        Assert.Equal(PositionMode.Survey, status.PositionMode);
        Assert.Equal(0.3, status.SurveyPercentComplete);
        Assert.Equal(PositionQualifier.Average, status.PositionQualifier);

        Assert.Equal(8, status.Tracked.Count);
        Assert.Equal(2, status.NotTracked.Count);
        Assert.True(status.HealthOk);
    }

    /// <summary>Full lock with nine satellites, the best state the sitting reached.</summary>
    [Fact]
    public void TheFullyLockedScreenParses()
    {
        ReceiverStatus status = ParseSittingFixture("captured/locked-to-gps.txt");

        Assert.Equal(SmartClockMode.Locked, status.Mode);
        Assert.True(string.IsNullOrEmpty(status.ModeDetail));
        Assert.Equal(OutputValidity.Valid, status.Outputs);
        Assert.Equal(3, status.Tfom);
        Assert.Equal(0, status.Ffom);
        Assert.Equal(49.8, status.OnePpsTiNanoseconds!.Value, 10);

        Assert.Equal(9, status.Tracked.Count);
        Assert.Equal(2, status.NotTracked.Count);

        // Still the rack position: taken before the survey ran, so the receiver was holding a
        // position surveyed indoors while its antenna was already outside.
        Assert.Equal(PositionMode.Hold, status.PositionMode);
        Assert.Null(status.SurveyPercentComplete);
        Assert.Equal(PositionQualifier.Unknown, status.PositionQualifier);
        Assert.Equal(38.0, status.Position!.HeightMetres);
    }

    /// <summary>Locked but still stabilizing, the state the sitting spent longest in.</summary>
    [Fact]
    public void TheStabilizingScreenParses()
    {
        ReceiverStatus status = ParseSittingFixture("captured/locked-to-gps-stabilizing-frequency.txt");

        Assert.Equal(SmartClockMode.Locked, status.Mode);
        Assert.Equal("stabilizing frequency", status.ModeDetail);

        // The distinction the medallion turns on: locked, but not yet at full accuracy.
        Assert.Equal(OutputValidity.ValidReduced, status.Outputs);
        Assert.Equal(3, status.Tfom);
        Assert.Equal(1, status.Ffom);
        Assert.Equal(-20.9, status.OnePpsTiNanoseconds!.Value, 10);

        Assert.Equal(8, status.Tracked.Count);
        Assert.Equal(PositionMode.Hold, status.PositionMode);
        Assert.Equal(PositionQualifier.Unknown, status.PositionQualifier);
    }

    /// <summary>A held position and a surveyed average are told apart on every captured screen.</summary>
    /// <remarks>
    /// The regression this pins is specific. The qualifier was matched by a parenthesised word —
    /// <c>(Average)</c> — which the documented form uses and this receiver never prints; it prefixes
    /// the label instead, as <c>AVG LAT</c>. Both surveying fixtures therefore read as having no
    /// qualifier, losing the one distinction the field exists to draw, on the only two screens in
    /// the corpus that draw it.
    /// </remarks>
    [Theory]
    [InlineData("captured/surveying-locked-to-gps-stabilizing-frequency.txt", PositionQualifier.Average)]
    [InlineData("captured/power-up-fine-freq-adj.txt", PositionQualifier.Average)]
    [InlineData("captured/locked-to-gps.txt", PositionQualifier.Unknown)]
    [InlineData("captured/locked-to-gps-stabilizing-frequency.txt", PositionQualifier.Unknown)]
    [InlineData("locked-stabilizing.txt", PositionQualifier.Unknown)]
    public void AnAveragedPositionIsDistinguishedFromAHeldOne(string fixtureName, PositionQualifier expected) =>
        Assert.Equal(expected, ParseSittingFixture(fixtureName).PositionQualifier);

    /// <summary>A clock row that is present but unreadable is not reported as a missing one.</summary>
    /// <remarks>
    /// <para>
    /// The distinction is the whole value of the warning (#245). §11.1 puts <c>ParseWarnings</c> in
    /// Diagnostics so a field report about an odd firmware revision is actionable, and telling
    /// somebody no clock row was found sends them looking for a line that is sitting in the capture
    /// they are holding.
    /// </para>
    /// <para>
    /// Both power-up screens print the provisional marker the Z3801A guide documents — <c>(?)</c>,
    /// which it renders as <c>[?]</c> — between the time and the date, and it defeats the pattern.
    /// Whether that time should be read at all is a model question, still open on #245; the value
    /// stays null either way, so this asserts only what the parser says about it.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("captured/power-up-gps-acquisition.txt", "05:10:04 (?) 12 Jan 2007")]
    [InlineData("captured/power-up-fine-freq-adj.txt", "05:10:26 (?) 12 Jan 2007")]
    public void AnUnreadableClockRowIsNotReportedAsAMissingOne(string fixtureName, string expectedQuoted)
    {
        ReceiverStatus status = ParseSittingFixture(fixtureName);

        Assert.Null(status.DeviceDateTime);
        Assert.Equal(TimeScale.Unknown, status.TimeScale);

        string warning = Assert.Single(status.ParseWarnings);
        Assert.Contains(expectedQuoted, warning, StringComparison.Ordinal);
        Assert.DoesNotContain("No clock row was found", warning, StringComparison.Ordinal);
    }

    /// <summary>A screen with no clock row at all still says so.</summary>
    /// <remarks>
    /// The other half, and the one that keeps the change honest: loosening the detection until
    /// nothing is ever called missing would be its own defect. <c>GPS 1PPS Synchronized to UTC</c>
    /// begins with a scale name and must not be mistaken for a clock row, which is why the shape
    /// test requires a time of day after it.
    /// </remarks>
    [Fact]
    public void AScreenWithNoClockRowStillSaysSo()
    {
        string[] lines =
        [
            "------------------------------- Receiver Status -------------------------------",
            "SYNCHRONIZATION ........................................... [ Outputs Invalid ]",
            ">> Power-up: GPS acquisition                  TFOM     9             FFOM     3",
            "ACQUISITION .............................................. [ GPS 1PPS Invalid ]",
            "Tracking: 0 ____   Not Tracking: 0 ________",
            "                                              GPS 1PPS Synchronized to UTC",
            "HEALTH MONITOR ......................................................... [ OK ]",
        ];

        ReceiverStatus status = ParserAt(s_sittingInstant).Parse(string.Join("\r\n", lines));

        Assert.Null(status.DeviceDateTime);
        Assert.Contains(status.ParseWarnings, w => w.Contains("No clock row was found", StringComparison.Ordinal));
    }

    /// <summary>
    /// Reads a fixture as the device wrote it. Latin-1 because it never substitutes, and an explicit
    /// CRLF split because the file is committed with <c>-text</c> and must not depend on the
    /// platform's idea of a line.
    /// </summary>
    private static string[] ReadFixtureLines(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        string text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        return text.TrimEnd('\r', '\n').Split("\r\n");
    }
}
