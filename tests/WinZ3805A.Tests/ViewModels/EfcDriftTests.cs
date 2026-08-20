using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// #137's drift fit, against synthetic series whose slope, daily cycle and settling period are
/// known by construction.
/// </summary>
/// <remarks>
/// The classification is exercised against a series that is <b>not</b> ageing as well as one that
/// is. An advisory that says "consistent with an oscillator at end of life" about a healthy
/// receiver is a worse failure than one that says nothing, and only a negative case catches it.
/// </remarks>
public sealed class EfcDriftTests
{
    private static readonly long Origin = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;

    /// <summary>Builds a series at one sample a minute over a number of days.</summary>
    private static EfcSample[] Series(
        double days,
        Func<double, double> percentAtDay,
        bool locked = true,
        double powerUpForDays = 0)
    {
        int count = (int)(days * 24 * 60);
        EfcSample[] samples = new EfcSample[count];

        for (int i = 0; i < count; i++)
        {
            double t = i / (24.0 * 60.0);
            samples[i] = new EfcSample(
                Origin + (long)(t * TimeSpan.TicksPerDay),
                percentAtDay(t),
                IsPowerUp: t < powerUpForDays,
                IsLocked: locked);
        }

        return samples;
    }

    // ------------------------------------------------------------------------------- the slope

    [Fact]
    public void AFlatSeriesHasNoSlope()
    {
        EfcDriftResult result = EfcDrift.Analyse(Series(7, _ => -16.8));

        Assert.True(result.IsUsable);
        Assert.Equal(0, result.SlopePercentPerDay, 3);
        Assert.Null(result.DaysToRail);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(-0.25)]
    [InlineData(2.0)]
    public void ASteadyRampRecoversItsSlope(double slope)
    {
        EfcDriftResult result = EfcDrift.Analyse(Series(14, t => -10 + (slope * t)));

        Assert.Equal(slope, result.SlopePercentPerDay, 3);
    }

    /// <summary>
    /// The reason the model has a 24-hour term at all. A straight line through a day of ambient
    /// cycling reports the room, not the oscillator — and its answer depends on which phase of the
    /// cycle the window happened to start and end on.
    /// </summary>
    [Fact]
    public void ADailyCycleIsNotMistakenForDrift()
    {
        // Pure 24-hour swing of ±6 %, no secular trend whatever.
        EfcDriftResult result = EfcDrift.Analyse(
            Series(7, t => -20 + (6 * Math.Sin(2 * Math.PI * t))));

        Assert.Equal(0, result.SlopePercentPerDay, 2);
        Assert.Equal(6, result.DiurnalAmplitudePercent, 1);
        Assert.Null(result.DaysToRail);
    }

    /// <summary>And the two together are separated, which is the whole point.</summary>
    [Fact]
    public void ATrendUnderADailyCycleIsSeparatedFromIt()
    {
        EfcDriftResult result = EfcDrift.Analyse(
            Series(14, t => -30 + (0.8 * t) + (4 * Math.Sin(2 * Math.PI * t))));

        Assert.Equal(0.8, result.SlopePercentPerDay, 2);
        Assert.Equal(4, result.DiurnalAmplitudePercent, 1);
    }

    [Fact]
    public void AWellModelledSeriesHasASmallResidual()
    {
        EfcDriftResult result = EfcDrift.Analyse(
            Series(7, t => -20 + (0.5 * t) + (3 * Math.Sin(2 * Math.PI * t))));

        Assert.True(result.ResidualPercent < 0.01, $"residual was {result.ResidualPercent}");
    }

    // --------------------------------------------------------------------------- the cold start

    /// <summary>
    /// The loop settles after a power-up, and those samples bend the fit. §10.8 uses the same 24 h
    /// figure for a related reason.
    /// </summary>
    [Fact]
    public void SettlingSamplesAreExcludedAndCounted()
    {
        // Two days of wild settling, then five days dead flat.
        EfcSample[] samples = Series(
            7,
            t => t < 1 ? -80 + (60 * t) : -20,
            powerUpForDays: 1);

        EfcDriftResult result = EfcDrift.Analyse(samples);

        Assert.True(result.ExcludedForSettling > 0);
        Assert.Equal(0, result.SlopePercentPerDay, 2);
    }

    [Fact]
    public void WithoutExclusionTheSettlingWouldDominate()
    {
        // The same series, but nothing marked as power-up and no settling window: the ramp is
        // then real data and the fit is entitled to follow it. This is the control that shows the
        // exclusion above is doing the work.
        EfcSample[] samples = Series(7, t => t < 1 ? -80 + (60 * t) : -20);

        EfcDriftResult result = EfcDrift.Analyse(samples, settling: TimeSpan.Zero);

        Assert.NotEqual(0, Math.Round(result.SlopePercentPerDay, 2));
    }

    [Fact]
    public void PowerUpSamplesThemselvesAreNeverUsed()
    {
        EfcSample[] samples = Series(7, _ => -20, powerUpForDays: 0.5);

        EfcDriftResult result = EfcDrift.Analyse(samples, settling: TimeSpan.Zero);

        Assert.Equal(samples.Length - result.SampleCount, result.ExcludedForSettling);
        Assert.True(result.ExcludedForSettling > 0);
    }

    // -------------------------------------------------------------------------- not enough data

    [Fact]
    public void TooFewSamplesReportsInsufficientRatherThanGuessing()
    {
        EfcDriftResult result = EfcDrift.Analyse(Series(0.01, _ => -20));

        Assert.False(result.IsUsable);
        Assert.Equal(DriftPattern.Insufficient, result.Pattern);
        Assert.Equal(0, result.SlopePercentPerDay);
    }

    [Fact]
    public void AnEmptySeriesIsInsufficientRatherThanAnError()
    {
        EfcDriftResult result = EfcDrift.Analyse([]);

        Assert.False(result.IsUsable);
        Assert.Equal(0, result.SampleCount);
    }

    /// <summary>
    /// A window shorter than a day leaves the sine and cosine columns nearly degenerate. The fit
    /// must not answer NaN, which would reach the interface as a slope of "NaN %/day".
    /// </summary>
    [Fact]
    public void AShortWindowStillProducesFiniteNumbers()
    {
        EfcDriftResult result = EfcDrift.Analyse(Series(0.1, t => -20 + t));

        Assert.False(double.IsNaN(result.SlopePercentPerDay));
        Assert.False(double.IsNaN(result.DiurnalAmplitudePercent));
        Assert.False(double.IsNaN(result.ResidualPercent));
    }

    /// <summary>
    /// The defect this test exists for, found on the bench. A short window makes the 24-hour sine
    /// and cosine columns collinear with the constant and the line; the solve went singular,
    /// returned zeros, and the residual was then measured against a model of <b>zero</b>. A
    /// dead-flat −16.83 % series therefore reported "unexplained scatter 16.83 %" — the reading
    /// itself wearing the label of its own error, and enough to be classified as an erratic loop.
    /// </summary>
    [Fact]
    public void AShortFlatWindowReportsNoScatterRatherThanItsOwnMagnitude()
    {
        // Two minutes at one sample a second, dead flat at the value the bench receiver reads.
        EfcSample[] samples = new EfcSample[120];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = new EfcSample(Origin + (i * TimeSpan.TicksPerSecond), -16.83, false, true);
        }

        EfcDriftResult result = EfcDrift.Analyse(samples);

        Assert.True(result.IsUsable);
        Assert.False(result.DiurnalSeparable);
        Assert.True(result.ResidualPercent < 0.001, $"residual was {result.ResidualPercent}");
        Assert.Equal(0, result.SlopePercentPerDay, 6);
        Assert.Equal(DriftPattern.NothingRemarkable, result.Pattern);
    }

    /// <summary>
    /// Below a day the fit drops to a plain line, and says so. The daily cycle is not reported as
    /// zero amplitude — that would be a measurement — so the flag is what the interface reads.
    /// </summary>
    [Fact]
    public void AWindowShorterThanADayDoesNotClaimToSeparateADailyCycle()
    {
        EfcDriftResult result = EfcDrift.Analyse(Series(0.2, t => -20 + (0.4 * t)));

        Assert.True(result.IsUsable);
        Assert.False(result.DiurnalSeparable);
    }

    [Fact]
    public void AFullWeekDoesSeparateIt() =>
        Assert.True(EfcDrift.Analyse(Series(7, _ => -20)).DiurnalSeparable);

    /// <summary>
    /// The window is what the fit spans, not what the user selected. Picking seven days on a
    /// receiver that has logged for one must not report a seven-day window.
    /// </summary>
    [Fact]
    public void TheWindowReportedIsTheOneActuallyFitted()
    {
        EfcDriftResult result = EfcDrift.Analyse(Series(2, _ => -20));

        Assert.Equal(2, result.WindowSpan.TotalDays, 1);
    }

    [Fact]
    public void SettlingSamplesAreNotCountedInTheWindow()
    {
        EfcDriftResult result = EfcDrift.Analyse(Series(3, _ => -20, powerUpForDays: 1));

        Assert.True(result.ExcludedForSettling > 0);
        Assert.True(result.WindowSpan.TotalDays < 2.1, $"window was {result.WindowSpan}");
    }

    // ------------------------------------------------------------------------- the projection

    [Fact]
    public void AProjectionCountsDaysToTheRailItIsHeadingFor()
    {
        // At −50 % rising 1 %/day, +100 % is 150 days away.
        double? days = EfcDrift.DaysToRail(-50, 1);

        Assert.NotNull(days);
        Assert.Equal(150, days!.Value, 1);
    }

    [Fact]
    public void AFallingTrendProjectsToTheNegativeRail()
    {
        double? days = EfcDrift.DaysToRail(-50, -0.5);

        Assert.NotNull(days);
        Assert.Equal(100, days!.Value, 1);
    }

    /// <summary>
    /// "12 000 days" is a way of saying "never" that invites being read as a measurement, so a
    /// flat or absurdly distant trend has no projection at all.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(0.0001)]
    [InlineData(-0.0001)]
    public void AFlatTrendHasNoProjection(double slope) =>
        Assert.Null(EfcDrift.DaysToRail(-16.8, slope));

    [Fact]
    public void AnAbsurdlyDistantProjectionIsNotOffered() =>
        Assert.Null(EfcDrift.DaysToRail(0, 0.002));

    /// <summary>
    /// And no projection either. A slope fitted to two minutes of noise extrapolates to a large
    /// figure in per cent per day, and "60 days to the rail" is read as a measurement however it
    /// was arrived at.
    /// </summary>
    [Fact]
    public void AShortWindowOffersNoProjection()
    {
        // A rise of 0.05 % across two minutes — arithmetically 36 %/day, which is noise, not ageing.
        EfcSample[] samples = new EfcSample[120];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = new EfcSample(
                Origin + (i * TimeSpan.TicksPerSecond),
                -16.83 + (0.05 * i / 120.0),
                false,
                true);
        }

        EfcDriftResult result = EfcDrift.Analyse(samples);

        Assert.False(result.DiurnalSeparable);
        Assert.Null(result.DaysToRail);
    }

    // ----------------------------------------------------------------------- the classification

    /// <summary>The negative case, and the one that matters most.</summary>
    [Fact]
    public void AHealthyReceiverIsNotAccusedOfAnything()
    {
        EfcDriftResult result = EfcDrift.Analyse(
            Series(7, t => -16.8 + (0.01 * t) + (0.5 * Math.Sin(2 * Math.PI * t))));

        Assert.Equal(DriftPattern.NothingRemarkable, result.Pattern);
    }

    [Fact]
    public void EfcNearARailWithAGoodFixSuggestsTheOscillator() =>
        Assert.Equal(
            DriftPattern.OscillatorNearingRange,
            EfcDrift.Classify(latestPercent: 92, slopePerDay: 0.4, residualPercent: 0.2, lockedFraction: 1.0));

    [Fact]
    public void EfcMidRangeWithADegradedFixSuggestsTheAntennaPath() =>
        Assert.Equal(
            DriftPattern.GpsOrAntennaPath,
            EfcDrift.Classify(latestPercent: -16, slopePerDay: 0.0, residualPercent: 0.3, lockedFraction: 0.4));

    [Fact]
    public void EfcMidRangeButErraticSuggestsTheLoop() =>
        Assert.Equal(
            DriftPattern.LoopOrReference,
            EfcDrift.Classify(latestPercent: -16, slopePerDay: 0.0, residualPercent: 12, lockedFraction: 1.0));

    /// <summary>
    /// A bad fix explains a wandering EFC; an oscillator at end of range does not explain missing
    /// satellites. Reporting the more alarming of two explanations because it was checked first
    /// would be exactly the overstatement #137 forbids.
    /// </summary>
    [Fact]
    public void ABadFixOutranksANearRailReading() =>
        Assert.Equal(
            DriftPattern.GpsOrAntennaPath,
            EfcDrift.Classify(latestPercent: 95, slopePerDay: 1.0, residualPercent: 0.2, lockedFraction: 0.2));

    /// <summary>
    /// Every sentence hedges. #137 requires the advisory not to overstate its confidence, and the
    /// wording is where that requirement actually lives.
    /// </summary>
    [Theory]
    [InlineData(DriftPattern.OscillatorNearingRange)]
    [InlineData(DriftPattern.GpsOrAntennaPath)]
    [InlineData(DriftPattern.LoopOrReference)]
    public void EveryFaultDescriptionHedgesRatherThanDiagnosing(DriftPattern pattern)
    {
        string text = EfcDrift.Describe(pattern);

        Assert.Contains("consistent with", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("is failing", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("has failed", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryPatternHasSomethingToSay() =>
        Assert.All(
            Enum.GetValues<DriftPattern>(),
            pattern => Assert.False(string.IsNullOrWhiteSpace(EfcDrift.Describe(pattern))));

    /// <summary>
    /// The receiver as it actually is today: EFC −16.83 %, locked, no trend to speak of. The
    /// advisory must not turn that into a warning.
    /// </summary>
    [Fact]
    public void ThisBenchReceiverReadsAsNothingRemarkable()
    {
        EfcDriftResult result = EfcDrift.Analyse(Series(3, _ => -16.83));

        Assert.Equal(DriftPattern.NothingRemarkable, result.Pattern);
        Assert.Null(result.DaysToRail);
        Assert.Contains("Nothing in this window", EfcDrift.Describe(result.Pattern), StringComparison.Ordinal);
    }
}
