using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// The sentences §10.7.1's drift card prints (#182).
/// </summary>
/// <remarks>
/// The figures are the 22–24 Aug 2026 capture as measured out of <c>trend.db</c> and written into
/// #182: secular drift −0.00086 %/day, diurnal amplitude 0.00034 %, residual rms 0.00324 % over
/// 71,008 settled readings spanning 47.4 hours. The fit that produced them was already right — it
/// agreed with an independent one to five decimal places. What was wrong was the sentence.
/// </remarks>
public class DriftAdvisoryTests
{
    private static readonly DateTimeOffset Today = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The bench receiver's 47-hour fit.</summary>
    private static EfcDriftResult BenchFit => new()
    {
        SampleCount = 71_008,
        ExcludedForSettling = 0,
        SlopePercentPerDay = -0.00086,
        DiurnalAmplitudePercent = 0.00034,
        ResidualPercent = 0.00324,
        LatestPercent = -16.8041,
        DaysToRail = null,
        Pattern = DriftPattern.NothingRemarkable,
        WindowSpan = TimeSpan.FromHours(47.4),
        DiurnalSeparable = true,
    };

    // -------------------------------------------------------------------------------------
    // The defect
    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// What #182 is. At §9.11 item 6's 1 dp for a percentage, every figure the 47-hour fit produced
    /// rounds to nothing — and §10.7.1 forbids exactly that, because "reporting a daily amplitude
    /// of zero would be a measurement". Asserted here so the reason for the unit is visible beside
    /// the unit.
    /// </remarks>
    [Fact]
    public void EveryFigureRoundsAwayWhenCountedInPerCent()
    {
        Assert.Equal("0.0", BenchFit.DiurnalAmplitudePercent.ToString("0.0"));
        Assert.Equal("0.0", BenchFit.ResidualPercent.ToString("0.0"));
        Assert.Equal("0.0", Math.Abs(BenchFit.SlopePercentPerDay).ToString("0.0"));
    }

    /// <summary>The same three figures, counted in ppm of range, are readable at the same 1 dp.</summary>
    [Fact]
    public void TheSameFiguresAreReadableInPpm()
    {
        Assert.Equal(3.4, DriftAdvisory.Ppm(BenchFit.DiurnalAmplitudePercent), 6);
        Assert.Equal(32.4, DriftAdvisory.Ppm(BenchFit.ResidualPercent), 6);
        Assert.Equal(-8.6, DriftAdvisory.Ppm(BenchFit.SlopePercentPerDay), 6);
    }

    /// <summary>One per cent of control range is ten thousand parts per million of it.</summary>
    [Theory]
    [InlineData(1.0, 10_000)]
    [InlineData(0.0001, 1)]
    [InlineData(-16.83, -168_300)]
    [InlineData(0, 0)]
    public void PpmIsPerCentTimesTenThousand(double percent, double expected) =>
        Assert.Equal(expected, DriftAdvisory.Ppm(percent), 6);

    // -------------------------------------------------------------------------------------
    // The sentences
    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// The card used to read "Drift −0.001 %/day". Three decimal places on a percentage, which
    /// §9.11 item 6 does not allow either, and still not enough to separate this receiver from a
    /// dead one.
    /// </remarks>
    [Fact]
    public void TheHeadlineStatesTheSlopeInPpmPerDay()
    {
        string numbers = DriftAdvisory.Numbers(BenchFit, Today);

        Assert.Contains("−8.6 ppm/day", numbers, StringComparison.Ordinal);
        Assert.DoesNotContain("%/day", numbers, StringComparison.Ordinal);
    }

    /// <summary>A negative slope takes U+2212, never a hyphen (§9.5.3, P0-20).</summary>
    [Fact]
    public void TheSlopeUsesATrueMinusSign()
    {
        string numbers = DriftAdvisory.Numbers(BenchFit, Today);

        Assert.Contains('−', numbers);
        Assert.DoesNotContain("-8.6", numbers, StringComparison.Ordinal);
    }

    /// <summary>A positive slope is signed too, so the two read alike in a glance.</summary>
    [Fact]
    public void APositiveSlopeCarriesItsPlus()
    {
        string numbers = DriftAdvisory.Numbers(BenchFit with { SlopePercentPerDay = 0.00086 }, Today);

        Assert.Contains("+8.6 ppm/day", numbers, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The whole evidence line for the bench window. Every number in it was 0.00 before #182, and
    /// the receiver it describes is the good case: a double-oven oscillator holding 0.05 % of range
    /// across two days. The card can now say so.
    /// </remarks>
    [Fact]
    public void TheEvidenceLineReportsScatterAndSwingInPpm()
    {
        string evidence = DriftAdvisory.Evidence(BenchFit);

        Assert.Contains("71,008 settled reading(s)", evidence, StringComparison.Ordinal);
        Assert.Contains("spanning 47.4 hours", evidence, StringComparison.Ordinal);
        Assert.Contains("Unexplained scatter 32.4 ppm of range", evidence, StringComparison.Ordinal);
        Assert.Contains("Daily swing about ±3.4 ppm", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("0.00", evidence, StringComparison.Ordinal);
    }

    /// <summary>The projection is still a percentage, because the rails are at ±100 %.</summary>
    [Fact]
    public void TheRailProjectionStaysInPerCent()
    {
        string numbers = DriftAdvisory.Numbers(BenchFit with { DaysToRail = 412 }, Today);

        Assert.Contains("reaches ±100 % in about 412 day(s)", numbers, StringComparison.Ordinal);
        Assert.Contains("11 Oct 2027", numbers, StringComparison.Ordinal);
    }

    /// <summary>Under a day of data, the card says why there is no daily figure rather than printing one.</summary>
    [Fact]
    public void AnInseparableDiurnalTermIsSaidRatherThanReportedAsZero()
    {
        string evidence = DriftAdvisory.Evidence(
            BenchFit with { DiurnalSeparable = false, WindowSpan = TimeSpan.FromHours(6) });

        Assert.Contains("cannot be separated from the trend", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("Daily swing", evidence, StringComparison.Ordinal);
    }

    /// <summary>Excluded settling samples are named when there are any, and not mentioned when there are none.</summary>
    [Fact]
    public void SettlingExclusionsAreNamedOnlyWhenTheyExist()
    {
        Assert.DoesNotContain("excluded", DriftAdvisory.Evidence(BenchFit), StringComparison.Ordinal);
        Assert.Contains(
            "4,919 excluded as post-power-up settling",
            DriftAdvisory.Evidence(BenchFit with { ExcludedForSettling = 4_919 }),
            StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------
    // Spans
    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// Days only past two of them, so the bench receiver's 47.4-hour window is still said in hours
    /// — which is what the card printed on #182 and #184, and worth pinning rather than assuming.
    /// <para>
    /// <b>Rounded down, never to nearest</b> (#184). The last two rows are why: 23.98 hours printed
    /// as "24.0 hours" beside a verdict of "under a day of data", and a figure that rounds up across
    /// the threshold its own sentence is about reads as a contradiction rather than as a rounding.
    /// An evidence line may understate what it had; it may not overstate it.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(50, "2.0 days")]
    [InlineData(48, "2.0 days")]
    [InlineData(47.4, "47.4 hours")]
    [InlineData(6, "6.0 hours")]
    [InlineData(1.5, "90 minutes")]
    [InlineData(24, "24.0 hours")]
    [InlineData(23.98, "23.9 hours")]
    public void ASpanIsSaidInTheLargestUnitThatStaysReadable(double hours, string expected) =>
        Assert.Equal(expected, DriftAdvisory.DescribeSpan(TimeSpan.FromHours(hours)));

    // -------------------------------------------------------------------------------------
    // The unusable case
    // -------------------------------------------------------------------------------------

    [Fact]
    public void TooFewSamplesSaysHowManyAreNeeded()
    {
        string text = DriftAdvisory.NotEnough(BenchFit with { SampleCount = 12 });

        Assert.Contains("12 usable reading(s)", text, StringComparison.Ordinal);
        Assert.Contains(EfcDrift.MinimumSamples.ToString(), text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFitIsRequired()
    {
        Assert.Throws<ArgumentNullException>(() => DriftAdvisory.Numbers(null!, Today));
        Assert.Throws<ArgumentNullException>(() => DriftAdvisory.Evidence(null!));
        Assert.Throws<ArgumentNullException>(() => DriftAdvisory.NotEnough(null!));
    }
}
