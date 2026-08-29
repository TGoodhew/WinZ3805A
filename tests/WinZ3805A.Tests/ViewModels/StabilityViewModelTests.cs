using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Controls;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// P2-3's stability table over the persisted series (#63).
/// </summary>
/// <remarks>
/// The estimator itself is covered by <c>AllanDeviationTests</c>. What is checked here is the part
/// that reads the store and formats the answer — where a unit slip, a stale window or a silently
/// dropped gap turns a correct computation into a wrong reading.
/// </remarks>
public class StabilityViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 3, 0, 0, TimeSpan.Zero);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"stability-{Guid.NewGuid():N}.db");
    private readonly TrendStore _store;

    public StabilityViewModelTests() => _store = new TrendStore(_path, TimeSpan.FromDays(30));

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // A temp file the OS still holds is not worth failing a test over.
        }

        GC.SuppressFinalize(this);
    }

    private StabilityViewModel Model() => new(_store, new FakeTimeProvider(Now));

    /// <summary>Appends a uniformly spaced series of time-interval readings, oldest first.</summary>
    private void Append(IEnumerable<double> nanoseconds, double stepSeconds = 1, double startMinutesAgo = 60)
    {
        long ticks = Now.AddMinutes(-startMinutesAgo).UtcTicks;
        long step = (long)(stepSeconds * TimeSpan.TicksPerSecond);

        foreach (double ns in nanoseconds)
        {
            _store.Append(new TrendRecord(ticks, null, ns, "LOCK", 8));
            ticks += step;
        }
    }

    [Fact]
    public void An_empty_store_says_what_will_appear_rather_than_failing()
    {
        StabilityViewModel model = Model();

        model.Refresh();

        Assert.False(model.HasCurve);
        Assert.Contains("No time-interval samples", model.Summary, StringComparison.Ordinal);
        Assert.Contains("fills as the receiver is polled", model.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sampling_interval_is_measured_from_the_data()
    {
        // §7.3's cadence is a target, not a guarantee, so it is read rather than assumed.
        Append(Enumerable.Range(0, 64).Select(i => (double)i), stepSeconds: 2);

        StabilityViewModel model = Model();
        model.Refresh();

        Assert.NotNull(model.NominalIntervalSeconds);
        Assert.Equal(2, model.NominalIntervalSeconds!.Value, 3);
    }

    [Fact]
    public void White_phase_noise_falls_as_one_over_tau()
    {
        // The known-answer control. For white phase modulation sigma goes as tau^-1, so each octave
        // of tau should roughly halve it. Without a control the table would be a column of
        // plausible numbers nobody could falsify.
        Random random = new(20260829);
        Append(Enumerable.Range(0, 512).Select(_ => random.NextDouble() * 100 - 50));

        StabilityViewModel model = Model();
        model.Refresh();

        Assert.True(model.Rows.Count >= 4, $"expected several taus, got {model.Rows.Count}");

        StabilityRow first = model.Rows[0];
        StabilityRow later = model.Rows[3];

        double ratio = first.Point!.Value.Deviation / later.Point!.Value.Deviation;
        double taus = later.Tau / first.Tau;

        // tau^-1 would give exactly `taus`; allow a wide band because this is one finite draw.
        Assert.InRange(ratio, taus * 0.5, taus * 2.0);
    }

    [Fact]
    public void The_reading_is_dimensionless_not_scaled_by_a_billion()
    {
        // The store holds nanoseconds and the estimator works in seconds. Mixing them scales the
        // answer by 1e9 and still produces a number that looks like a stability figure, which is
        // the whole reason this test exists.
        //
        // 1 ns of white phase noise at tau = 1 s cannot give a sigma anywhere near unity.
        Random random = new(4242);
        Append(Enumerable.Range(0, 256).Select(_ => random.NextDouble() * 2 - 1));

        StabilityViewModel model = Model();
        model.Refresh();

        Assert.All(model.Rows, r => Assert.InRange(r.Point!.Value.Deviation, 0, 1e-6));
    }

    [Fact]
    public void A_gap_does_not_silently_join_two_runs()
    {
        // trend.db follows the poll schedule, not a clock, and tonight alone put two multi-minute
        // holes in it. An estimator treating either side of a gap as adjacent invents a second
        // difference across it and reports a worse sigma with no indication anything happened.
        Append(Enumerable.Repeat(0.0, 64));

        // A second run, an hour later, with a deliberate offset. Joined naively, the step across
        // the gap dwarfs everything either side of it.
        long ticks = Now.AddMinutes(-5).UtcTicks;
        foreach (int i in Enumerable.Range(0, 64))
        {
            _store.Append(new TrendRecord(ticks, null, 1_000_000 + i, "LOCK", 8));
            ticks += TimeSpan.TicksPerSecond;
        }

        StabilityViewModel model = Model();
        model.Refresh();

        // Both runs are flat, so every honest estimate is tiny. A joined series would show the
        // millisecond jump instead.
        Assert.All(model.Rows, r => Assert.InRange(r.Point!.Value.Deviation, 0, 1e-6));
    }

    [Fact]
    public void Each_row_carries_how_many_differences_it_averaged()
    {
        Append(Enumerable.Range(0, 128).Select(i => (double)(i % 7)));

        StabilityViewModel model = Model();
        model.Refresh();

        Assert.All(model.Rows, r => Assert.True(r.Point!.Value.Pairs > 0));

        // Longer averaging times rest on fewer differences. That is the fact the count exists to
        // convey, so it had better be true of the numbers being shown.
        Assert.True(model.Rows[^1].Point!.Value.Pairs < model.Rows[0].Point!.Value.Pairs);
    }

    [Fact]
    public void A_thinly_supported_row_is_marked()
    {
        // Sixteen samples, not forty. AveragingFactors caps tau at N/4, so the longest row keeps
        // about N/2 differences and a series of forty can never be thin -- the cap is already doing
        // most of this work. The mark is for the short series the cap cannot save.
        Append(Enumerable.Range(0, 16).Select(i => (double)(i % 5)));

        StabilityViewModel model = Model();
        model.Refresh();

        Assert.Contains(model.Rows, r => r.IsSparse);
        Assert.DoesNotContain(model.Rows.Take(1), r => r.IsSparse);
    }

    [Fact]
    public void Samples_outside_the_window_are_not_read()
    {
        // Stale data quietly widening the series would change the answer without changing the
        // stated window.
        Append(Enumerable.Range(0, 64).Select(i => (double)i), startMinutesAgo: 600);

        StabilityViewModel model = Model();
        model.Window = TimeSpan.FromMinutes(5);
        model.Refresh();

        Assert.False(model.HasCurve);
        Assert.Contains("No time-interval samples", model.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_series_too_short_to_average_says_so_instead_of_showing_nothing()
    {
        Append(Enumerable.Range(0, 3).Select(i => (double)i));

        StabilityViewModel model = Model();
        model.Refresh();

        Assert.False(model.HasCurve);
        Assert.Contains("too few", model.Summary, StringComparison.Ordinal);
    }

    // ---- Formatting -------------------------------------------------------------------------

    [Fact]
    public void The_deviation_uses_a_true_minus_in_its_exponent()
    {
        // §9.5.3 rule 4. A hyphen is optically too short beside lining figures, and this table is
        // nothing but lining figures.
        StabilityRow row = new(1, new AllanPoint(1, 1.234e-11, 500));

        Assert.Equal("1.23e−11", row.DeviationText);
        Assert.DoesNotContain('-', row.DeviationText);
    }

    [Fact]
    public void The_mantissa_keeps_a_fixed_width_so_rows_compare()
    {
        // Rule 6 fixes decimals per quantity; sigma is dimensionless, so the exponent carries the
        // magnitude and the mantissa stays put.
        StabilityRow small = new(1, new AllanPoint(1, 9.9e-13, 500));
        StabilityRow large = new(1, new AllanPoint(1, 1.0e-9, 500));

        Assert.Equal(small.DeviationText.Length, large.DeviationText.Length);
    }

    [Fact]
    public void A_row_without_an_estimate_reads_as_absent()
    {
        StabilityRow row = new(4, null);

        Assert.Equal(ReadoutFormatter.NoValue, row.DeviationText);
        Assert.Equal(ReadoutFormatter.NoValue, row.PairsText);
        Assert.False(row.IsSparse);
    }
}
