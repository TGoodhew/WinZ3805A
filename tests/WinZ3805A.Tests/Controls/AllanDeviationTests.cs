using WinZ3805A.Controls;

namespace WinZ3805A.Tests.Controls;

/// <summary>
/// §13's Allan deviation (P2-3, #63), checked against cases whose answer is known in closed form.
/// </summary>
/// <remarks>
/// Allan deviation is easy to implement plausibly and hard to implement correctly — an off-by-one
/// in the second difference, or dividing by the wrong count, produces a curve that looks entirely
/// reasonable and is wrong. So none of these tests asserts a number this code produced. Each one
/// asserts a number derived from the definition first.
/// </remarks>
public class AllanDeviationTests
{
    /// <summary>A constant frequency offset is not instability, and reads as zero.</summary>
    /// <remarks>
    /// <b>The sharpest test available.</b> A receiver whose pulse drifts at a perfectly steady rate
    /// has a phase series that is a straight line, and the second difference of a straight line is
    /// exactly zero at every lag — so a correct implementation returns 0 at every tau, and almost
    /// any indexing error does not.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(16)]
    public void AConstantFrequencyOffsetHasNoAllanDeviation(int m)
    {
        // x(t) = 3 ns + 7 ns per sample: a straight line in phase.
        double[] phase = [.. Enumerable.Range(0, 400).Select(i => 3e-9 + (7e-9 * i))];

        double? adev = AllanDeviation.Overlapping(phase, tau0: 1.0, averagingFactor: m);

        Assert.NotNull(adev);
        Assert.Equal(0.0, adev!.Value, 15);
    }

    /// <summary>A steady frequency drift gives √2·a·τ, exactly.</summary>
    /// <remarks>
    /// For x(t) = a·t², every second difference equals 2a·m²·τ₀² regardless of where it is taken, so
    /// the sum collapses and σ<sub>y</sub>(τ) = √2·a·τ falls out of the definition. That makes it a
    /// closed-form check on the scaling — the factor of two, the τ² in the denominator and the
    /// τ = m·τ₀ substitution all have to be right together for it to hold across values of m.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(32)]
    public void ASteadyDriftGivesTheClosedFormAnswer(int m)
    {
        const double A = 1.5e-11;   // phase curvature, s per s²
        const double Tau0 = 0.5;

        double[] phase = [.. Enumerable.Range(0, 600).Select(i => A * Math.Pow(i * Tau0, 2))];

        double? adev = AllanDeviation.Overlapping(phase, Tau0, m);
        double expected = Math.Sqrt(2.0) * A * (m * Tau0);

        Assert.NotNull(adev);
        Assert.Equal(expected, adev!.Value, 15);
    }

    /// <summary>Doubling tau halves nothing by accident — the drift case scales linearly.</summary>
    /// <remarks>
    /// A second reading of the same property, stated as a ratio so it holds even if both values are
    /// wrong by a common factor. On a drift-dominated series ADEV rises as τ; an implementation that
    /// forgot the τ² would show it falling.
    /// </remarks>
    [Fact]
    public void OnADriftingSeriesAllanDeviationRisesWithTau()
    {
        double[] phase = [.. Enumerable.Range(0, 600).Select(i => 2e-11 * Math.Pow(i, 2))];

        double one = AllanDeviation.Overlapping(phase, 1.0, 1)!.Value;
        double eight = AllanDeviation.Overlapping(phase, 1.0, 8)!.Value;

        Assert.Equal(8.0, eight / one, 6);
    }

    /// <summary>A series too short for the question answers null, not zero.</summary>
    /// <remarks>
    /// §11.1's habit, applied to a statistic: a missing answer is null and renders as an em dash.
    /// Zero would be a claim of perfect stability, which is the most misleading possible reading of
    /// "not enough data".
    /// </remarks>
    [Theory]
    [InlineData(2, 1)]      // needs at least three samples for one second difference
    [InlineData(10, 5)]     // 2m == N, so nothing fits
    [InlineData(10, 99)]
    public void ASeriesTooShortForTheAveragingTimeAnswersNull(int samples, int m) =>
        Assert.Null(AllanDeviation.Overlapping(
            [.. Enumerable.Range(0, samples).Select(i => (double)i)], 1.0, m));

    /// <summary>An unusable sampling interval or averaging factor answers null.</summary>
    [Theory]
    [InlineData(0.0, 1)]
    [InlineData(-1.0, 1)]
    [InlineData(double.NaN, 1)]
    [InlineData(1.0, 0)]
    [InlineData(1.0, -3)]
    public void AnUnusableParameterAnswersNull(double tau0, int m) =>
        Assert.Null(AllanDeviation.Overlapping(
            [.. Enumerable.Range(0, 100).Select(i => (double)i)], tau0, m));

    /// <summary>Averaging factors are octave-spaced and stop at a quarter of the series.</summary>
    /// <remarks>
    /// The cap is the point. A second difference fits whenever 2m &lt; N, but at m = N/2 exactly one
    /// contributes and the "estimate" is wherever the series happened to start. Stopping at N/4
    /// keeps at least half the series overlapping at the longest tau reported.
    /// </remarks>
    [Theory]
    [InlineData(3, new int[0])]
    [InlineData(4, new[] { 1 })]
    [InlineData(100, new[] { 1, 2, 4, 8, 16 })]
    [InlineData(4096, new[] { 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024 })]
    public void AveragingFactorsAreOctavesCappedAtAQuarterOfTheSeries(int samples, int[] expected) =>
        Assert.Equal(expected, AllanDeviation.AveragingFactors(samples));

    /// <summary>A gap in the series is skipped rather than poisoning the whole estimate.</summary>
    /// <remarks>
    /// §11.1 lets an unparseable reading be null, and a trend store that has been running for weeks
    /// will contain some. One NaN touches three second differences; letting it through would make
    /// the entire Allan deviation NaN, which discards several thousand good samples to represent one
    /// bad one.
    /// </remarks>
    [Fact]
    public void ANonFiniteSampleDoesNotPoisonTheEstimate()
    {
        double[] clean = [.. Enumerable.Range(0, 400).Select(i => 2e-11 * Math.Pow(i, 2))];
        double[] withGap = [.. clean];
        withGap[200] = double.NaN;

        double? adev = AllanDeviation.Overlapping(withGap, 1.0, 4);

        Assert.NotNull(adev);
        Assert.True(double.IsFinite(adev!.Value));

        // The drift case is constant term-by-term, so dropping three of them changes nothing.
        Assert.Equal(AllanDeviation.Overlapping(clean, 1.0, 4)!.Value, adev.Value, 15);
    }

    /// <summary>On a genuinely uniform series the gap-aware overload agrees with the simple one.</summary>
    /// <remarks>
    /// The two must not be different estimators. Everything below is about what the gap-aware form
    /// does with data the simple one cannot handle; this pins that it changes nothing otherwise.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(17)]
    public void OnUniformDataBothOverloadsAgree(int m)
    {
        double[] phase = [.. Enumerable.Range(0, 500).Select(i => 4e-11 * Math.Pow(i, 2))];
        double[] seconds = [.. Enumerable.Range(0, 500).Select(i => i * 10.0)];

        Assert.Equal(
            AllanDeviation.Overlapping(phase, 10.0, m)!.Value,
            AllanDeviation.Overlapping(phase, seconds, 10.0, m)!.Value,
            15);
    }

    /// <summary>The receiver being unplugged overnight is not oscillator instability.</summary>
    /// <remarks>
    /// <b>The test this overload exists for.</b> Two stretches of perfectly steady frequency, six
    /// hours apart, with the phase having walked 200 ns in between — which is what an antenna lead
    /// pulled out and put back looks like in the trend store. Every second difference within each
    /// stretch is zero, so the honest answer is zero.
    /// <para>
    /// The uniform overload cannot know that and reports the step as though it happened in ten
    /// seconds. Asserting that it disagrees, and by a wide margin, is what keeps this from being a
    /// test that would pass either way.
    /// </para>
    /// </remarks>
    [Fact]
    public void AGapIsNotInstability()
    {
        const double Tau0 = 10.0;
        List<double> phase = [];
        List<double> seconds = [];

        for (int i = 0; i < 200; i++)          // steady drift rate, then a six-hour hole
        {
            phase.Add(1e-9 + (3e-12 * i));
            seconds.Add(i * Tau0);
        }

        for (int i = 0; i < 200; i++)          // and back, 200 ns further along
        {
            phase.Add(201e-9 + (3e-12 * i));
            seconds.Add(21600 + (i * Tau0));
        }

        double? aware = AllanDeviation.Overlapping(phase, seconds, Tau0, 4);
        double? naive = AllanDeviation.Overlapping(phase, Tau0, 4);

        Assert.NotNull(aware);
        Assert.Equal(0.0, aware!.Value, 15);

        // Not merely different - wrong by a margin that would dominate the plotted curve.
        Assert.NotNull(naive);
        Assert.True(naive!.Value > 1e-12, $"the uniform overload should be fooled, got {naive.Value}");
    }

    /// <summary>Samples logged faster than the nominal rate are left out rather than misread.</summary>
    /// <remarks>
    /// Not hypothetical: the trend store writes a row per poll, so a burst of fast polling puts
    /// 0.2 s steps in the middle of a 10 s series. Treating those as 10 s apart would report the
    /// oscillator as far more stable than it is, because a second difference over 0.6 s of real time
    /// is tiny and would be divided by a tau of 40 s.
    /// </remarks>
    [Fact]
    public void ABurstOfFastPollingDoesNotEnterTheEstimate()
    {
        const double Tau0 = 10.0;
        List<double> phase = [];
        List<double> seconds = [];
        double t = 0;

        for (int i = 0; i < 300; i++)
        {
            phase.Add(5e-11 * Math.Pow(t, 2));
            seconds.Add(t);
            t += i is >= 140 and < 160 ? 0.2 : Tau0;    // a burst in the middle
        }

        double? adev = AllanDeviation.Overlapping(phase, seconds, Tau0, 2);
        double expected = Math.Sqrt(2.0) * 5e-11 * (2 * Tau0);

        Assert.NotNull(adev);
        Assert.Equal(expected, adev!.Value, 15);
    }

    /// <summary>A series with no two samples the right distance apart answers null.</summary>
    [Fact]
    public void ASeriesWithNoUsableRunAnswersNull()
    {
        double[] phase = [.. Enumerable.Range(0, 50).Select(i => (double)i)];
        double[] seconds = [.. Enumerable.Range(0, 50).Select(i => i * 900.0)];

        Assert.Null(AllanDeviation.Overlapping(phase, seconds, 10.0, 1));
    }

    /// <summary>Mismatched or unusable inputs answer null rather than guessing.</summary>
    [Fact]
    public void MismatchedSeriesLengthsAnswerNull()
    {
        double[] phase = [1, 2, 3, 4, 5];
        double[] seconds = [0, 10, 20];

        Assert.Null(AllanDeviation.Overlapping(phase, seconds, 10.0, 1));
    }

    /// <summary>The nominal interval is the median step, so one overnight gap does not move it.</summary>
    /// <remarks>
    /// A mean would. On the six-day capture that prompted this overload, two gaps of over half a day
    /// each would have pulled a mean interval to several minutes and made every run fail the
    /// tolerance test - reporting no data at all for a series that holds days of good samples.
    /// </remarks>
    [Fact]
    public void TheNominalIntervalIgnoresGaps()
    {
        List<double> seconds = [];
        double t = 0;

        for (int i = 0; i < 100; i++)
        {
            seconds.Add(t);
            t += i == 50 ? 64800 : 10.0;      // eighteen hours off, once
        }

        Assert.Equal(10.0, AllanDeviation.NominalInterval(seconds)!.Value, 12);
    }

    /// <summary>Too short to have an interval, or no forward step at all, answers null.</summary>
    [Fact]
    public void TheNominalIntervalOfAnUnusableSeriesIsNull()
    {
        Assert.Null(AllanDeviation.NominalInterval([]));
        Assert.Null(AllanDeviation.NominalInterval([5.0]));
        Assert.Null(AllanDeviation.NominalInterval([7.0, 7.0, 7.0]));
    }
}
