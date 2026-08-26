using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// §10.7.1's 24 h range against <c>EfcDrift.Fit</c>'s day-long separability rule (#184).
/// </summary>
/// <remarks>
/// The defect was structural rather than an off-by-one: a window of exactly <i>n</i> hours can only
/// ever hold a <b>span</b> of slightly under <i>n</i> hours, because its oldest sample sits just
/// inside the leading edge rather than on it. So the one range named for a day could never reach the
/// day-based analysis the card is built around, and no comparison could be adjusted to fix it.
/// </remarks>
public class EfcDriftWindowTests
{
    /// <summary>The trend cadence on the bench: 71,067 rows across 47.4 hours is about 2.4 s.</summary>
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(2.4);

    private const long Origin = 640_000_000_000_000_000;

    /// <summary>
    /// A continuously logged series filling the last <paramref name="window"/> before "now".
    /// </summary>
    /// <remarks>
    /// The first sample lands one cadence inside the leading edge, which is what a real query
    /// returns and is the whole of #184. Values carry a gentle daily term so a separable fit has
    /// something to find.
    /// </remarks>
    private static List<EfcSample> Series(TimeSpan window)
    {
        List<EfcSample> samples = [];
        long now = Origin + TimeSpan.FromDays(30).Ticks;
        long first = now - window.Ticks + Cadence.Ticks;

        for (long ticks = first; ticks <= now; ticks += Cadence.Ticks)
        {
            double days = (ticks - first) / (double)TimeSpan.TicksPerDay;
            double value = -16.83 + (0.00034 * Math.Sin(2 * Math.PI * days));
            samples.Add(new EfcSample(ticks, value, IsPowerUp: false, IsLocked: true));
        }

        return samples;
    }

    // -------------------------------------------------------------------------------------
    // The defect
    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// What #184 is. Selecting 24 h gave a window whose span is 23.999 hours — under the day the fit
    /// needs — so the card reported that a daily swing could not be separated, while its own
    /// evidence line said the window spanned 24.0 hours.
    /// </remarks>
    [Fact]
    public void AWindowOfExactlyTwentyFourHoursCannotSeparateADailySwing()
    {
        EfcDriftResult drift = EfcDrift.Analyse(Series(TimeSpan.FromHours(24)));

        Assert.True(drift.IsUsable);
        Assert.False(drift.DiurnalSeparable);
        Assert.True(drift.WindowSpan < TimeSpan.FromDays(1));
    }

    /// <summary>The same range, once the fit is allowed to reach back past the drawn edge.</summary>
    [Fact]
    public void TheSameRangePlusTheFitMarginCanSeparateIt()
    {
        EfcDriftResult drift = EfcDrift.Analyse(Series(TimeSpan.FromHours(24) + EfcDrift.FitMargin));

        Assert.True(drift.DiurnalSeparable);
        Assert.True(drift.WindowSpan >= TimeSpan.FromDays(1));
    }

    /// <remarks>
    /// The margin has to clear the gap to the oldest sample with room to spare. Five minutes against
    /// a cadence of seconds is not a close-run thing, and this says by how much rather than leaving
    /// it to be re-derived.
    /// </remarks>
    [Fact]
    public void TheMarginClearsTheCadenceByAWideMargin()
    {
        Assert.True(EfcDrift.FitMargin > Cadence * 100);

        EfcDriftResult drift = EfcDrift.Analyse(Series(TimeSpan.FromHours(24) + EfcDrift.FitMargin));

        Assert.True(drift.WindowSpan - TimeSpan.FromDays(1) > TimeSpan.FromMinutes(4));
    }

    // -------------------------------------------------------------------------------------
    // What the margin must not do
    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// The margin widens what the fit may use; it does not manufacture history. A receiver logging
    /// for eighteen hours still fits eighteen hours and still says a daily swing cannot be
    /// separated, which is the correct answer and the one §10.7.1 asks for.
    /// </remarks>
    [Fact]
    public void AReceiverWithLessThanADayOfHistoryStillCannotSeparateIt()
    {
        EfcDriftResult drift = EfcDrift.Analyse(Series(TimeSpan.FromHours(18) + EfcDrift.FitMargin));

        Assert.True(drift.IsUsable);
        Assert.False(drift.DiurnalSeparable);
    }

    /// <summary>The shorter ranges are unaffected — they could never separate a daily term anyway.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void TheShortRangesStillReportThatTheyCannotSeparateIt(int hours)
    {
        EfcDriftResult drift = EfcDrift.Analyse(Series(TimeSpan.FromHours(hours) + EfcDrift.FitMargin));

        Assert.True(drift.IsUsable);
        Assert.False(drift.DiurnalSeparable);
    }

    /// <summary>And the 7 d range, which always could.</summary>
    [Fact]
    public void TheSevenDayRangeSeparatesItAsItAlwaysDid()
    {
        EfcDriftResult drift = EfcDrift.Analyse(Series(TimeSpan.FromDays(7)));

        Assert.True(drift.DiurnalSeparable);
    }

    // -------------------------------------------------------------------------------------
    // The sentence, which is where the contradiction was visible
    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// The residual half of #184, and the reason spans are now rounded down. Even with the margin,
    /// a receiver whose history happens to stop just short of a day would print a span that rounds
    /// up across the very threshold the verdict is about. It may understate what it had; it may not
    /// overstate it.
    /// </remarks>
    [Fact]
    public void AnEvidenceLineNeverClaimsMoreSpanThanItFitted()
    {
        EfcDriftResult drift = EfcDrift.Analyse(Series(TimeSpan.FromHours(24)));

        string evidence = DriftAdvisory.Evidence(drift);

        Assert.Contains("spanning 23.9 hours", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("24.0 hours", evidence, StringComparison.Ordinal);
        Assert.Contains("cannot be separated from the trend", evidence, StringComparison.Ordinal);
    }

    /// <summary>Together: the sentence no longer contradicts its own verdict.</summary>
    [Fact]
    public void TheTwentyFourHourRangeNowReportsADailySwingRatherThanRefusingOne()
    {
        EfcDriftResult drift = EfcDrift.Analyse(Series(TimeSpan.FromHours(24) + EfcDrift.FitMargin));

        string evidence = DriftAdvisory.Evidence(drift);

        Assert.Contains("spanning 24.0 hours", evidence, StringComparison.Ordinal);
        Assert.Contains("Daily swing about", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot be separated", evidence, StringComparison.Ordinal);
    }
}
