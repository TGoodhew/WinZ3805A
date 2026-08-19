using WinZ3805A.Controls;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// P1-2 (#50). Its stated verification is "an integration test writing and reloading a multi-day
/// series", so these use a real file rather than an in-memory database — reopening is the criterion.
/// </summary>
public sealed class TrendStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "wz-trend-" + Guid.NewGuid().ToString("n")[..8]);

    private string Path0 => Path.Combine(_folder, "trend.db");

    private static readonly long Origin = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;

    private static long At(TimeSpan offset) => Origin + offset.Ticks;

    private static TrendRecord Sample(TimeSpan offset, double efc = -16.8, double tint = -2.0) =>
        new(At(offset), efc, tint, "LOCK", 4);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp folder that outlives the run is not a failing test.
        }
    }

    [Fact]
    public void ASampleComesBackAsItWentIn()
    {
        using TrendStore store = new(Path0);

        Assert.True(store.Append(new TrendRecord(At(TimeSpan.Zero), -16.83, -2.0, "LOCK", 4)));

        TrendRecord read = Assert.Single(store.Read(long.MinValue, long.MaxValue));
        Assert.Equal(At(TimeSpan.Zero), read.Ticks);
        Assert.Equal(-16.83, read.Efc);
        Assert.Equal(-2.0, read.TimeIntervalNanoseconds);
        Assert.Equal("LOCK", read.SyncState);
        Assert.Equal(4, read.TrackedCount);
    }

    /// <summary>
    /// The headline criterion: without this the 7-day range is only reachable by leaving the
    /// application running for seven days.
    /// </summary>
    [Fact]
    public void AMultiDaySeriesSurvivesAReopen()
    {
        const int days = 5;

        using (TrendStore store = new(Path0))
        {
            for (int hour = 0; hour < days * 24; hour++)
            {
                store.Append(Sample(TimeSpan.FromHours(hour), efc: -16 - (hour * 0.001)));
            }
        }

        using (TrendStore reopened = new(Path0))
        {
            IReadOnlyList<TrendRecord> all = reopened.Read(long.MinValue, long.MaxValue);

            Assert.Equal(days * 24, all.Count);
            Assert.Equal(At(TimeSpan.Zero), all[0].Ticks);
            Assert.Equal(At(TimeSpan.FromHours((days * 24) - 1)), all[^1].Ticks);

            // And the values, not just the count - a store that reloads the right number of empty
            // rows would pass a weaker test.
            Assert.Equal(-16.0, all[0].Efc!.Value, 3);
        }
    }

    [Fact]
    public void ReadingSelectsOnlyTheWindowAsked()
    {
        using TrendStore store = new(Path0);

        for (int hour = 0; hour < 48; hour++)
        {
            store.Append(Sample(TimeSpan.FromHours(hour)));
        }

        IReadOnlyList<TrendRecord> window =
            store.Read(At(TimeSpan.FromHours(10)), At(TimeSpan.FromHours(20)));

        Assert.Equal(11, window.Count);
        Assert.Equal(At(TimeSpan.FromHours(10)), window[0].Ticks);
        Assert.Equal(At(TimeSpan.FromHours(20)), window[^1].Ticks);
    }

    [Fact]
    public void SamplesComeBackInTimeOrderHoweverTheyWentIn()
    {
        using TrendStore store = new(Path0);

        store.Append(Sample(TimeSpan.FromHours(5)));
        store.Append(Sample(TimeSpan.FromHours(1)));
        store.Append(Sample(TimeSpan.FromHours(3)));

        IReadOnlyList<TrendRecord> all = store.Read(long.MinValue, long.MaxValue);

        Assert.Equal([At(TimeSpan.FromHours(1)), At(TimeSpan.FromHours(3)), At(TimeSpan.FromHours(5))],
            all.Select(record => record.Ticks));
    }

    /// <summary>Ticks is the primary key, so a repeated append is a correction, not a duplicate.</summary>
    [Fact]
    public void AppendingTwiceAtTheSameInstantReplacesRatherThanDuplicates()
    {
        using TrendStore store = new(Path0);

        store.Append(new TrendRecord(At(TimeSpan.Zero), -10, 1, "LOCK", 4));
        store.Append(new TrendRecord(At(TimeSpan.Zero), -20, 2, "HOLD", 0));

        TrendRecord only = Assert.Single(store.Read(long.MinValue, long.MaxValue));
        Assert.Equal(-20, only.Efc);
        Assert.Equal("HOLD", only.SyncState);
    }

    /// <summary>§11.1: unread is not zero, and it must not become zero by being stored.</summary>
    [Fact]
    public void AnUnreadFieldStaysNullThroughARoundTrip()
    {
        using (TrendStore store = new(Path0))
        {
            store.Append(new TrendRecord(At(TimeSpan.Zero), null, null, null, null));
        }

        using TrendStore reopened = new(Path0);
        TrendRecord read = Assert.Single(reopened.Read(long.MinValue, long.MaxValue));

        Assert.Null(read.Efc);
        Assert.Null(read.TimeIntervalNanoseconds);
        Assert.Null(read.SyncState);
        Assert.Null(read.TrackedCount);
    }

    // ------------------------------------------------------------------------------- compaction

    /// <summary>§12: full resolution for 24 hours, 10 s beyond it.</summary>
    [Fact]
    public void RecentSamplesKeepTheirFullResolution()
    {
        using TrendStore store = new(Path0);

        // Twelve hours old, one a second for a minute.
        for (int i = 0; i < 60; i++)
        {
            store.Append(Sample(TimeSpan.FromHours(12) + TimeSpan.FromSeconds(i)));
        }

        long now = At(TimeSpan.FromHours(24));
        store.Compact(now);

        Assert.Equal(60, store.Count());
    }

    [Fact]
    public void OlderSamplesAreThinnedToTenSeconds()
    {
        using TrendStore store = new(Path0);

        // One a second for two minutes, five days ago - well past the 24 h window.
        for (int i = 0; i < 120; i++)
        {
            store.Append(Sample(TimeSpan.FromSeconds(i)));
        }

        store.Compact(At(TimeSpan.FromDays(5)));

        // Two minutes at 10 s is twelve buckets.
        Assert.Equal(12, store.Count());
    }

    /// <summary>
    /// A thinned sample must be one the receiver actually reported. Averaging would invent a
    /// reading, which is the same objection §9.10.2 makes to decimating by anything but min/max.
    /// </summary>
    [Fact]
    public void ThinningKeepsRealSamplesRatherThanAverages()
    {
        using TrendStore store = new(Path0);

        for (int i = 0; i < 30; i++)
        {
            store.Append(new TrendRecord(At(TimeSpan.FromSeconds(i)), i, i, "LOCK", 4));
        }

        store.Compact(At(TimeSpan.FromDays(5)));

        // Every surviving EFC is one of the integers that went in, not a mean of a bucket.
        Assert.All(
            store.Read(long.MinValue, long.MaxValue),
            record => Assert.Equal(record.Efc!.Value, Math.Round(record.Efc!.Value)));
    }

    [Fact]
    public void AnythingPastTheRetentionIsDropped()
    {
        using TrendStore store = new(Path0, retention: TimeSpan.FromDays(7));

        store.Append(Sample(TimeSpan.Zero));
        store.Append(Sample(TimeSpan.FromDays(20)));

        store.Compact(At(TimeSpan.FromDays(21)));

        TrendRecord only = Assert.Single(store.Read(long.MinValue, long.MaxValue));
        Assert.Equal(At(TimeSpan.FromDays(20)), only.Ticks);
    }

    /// <summary>
    /// The reason compaction exists. A week at 1 s is 604 800 rows; compacted it is far fewer, and
    /// that is what makes multi-week retention affordable (§12).
    /// </summary>
    [Fact]
    public void CompactionMakesAWeekAffordable()
    {
        using TrendStore store = new(Path0);

        // Six days old, sampled every second for an hour: 3 600 rows.
        for (int i = 0; i < 3600; i++)
        {
            store.Append(Sample(TimeSpan.FromSeconds(i)));
        }

        Assert.Equal(3600, store.Count());

        store.Compact(At(TimeSpan.FromDays(6)));

        // An hour at 10 s is 360.
        Assert.Equal(360, store.Count());
    }

    [Fact]
    public void CompactingAnEmptyStoreIsHarmless()
    {
        using TrendStore store = new(Path0);

        Assert.Equal(0, store.Compact(At(TimeSpan.FromDays(1))));
        Assert.Equal(0, store.Count());
    }

    // ---------------------------------------------------------------------------------- series

    [Fact]
    public void ReadSeriesProjectsOneFieldForTheChart()
    {
        using TrendStore store = new(Path0);

        store.Append(new TrendRecord(At(TimeSpan.Zero), -10, 5, "LOCK", 4));
        store.Append(new TrendRecord(At(TimeSpan.FromSeconds(1)), -11, 6, "LOCK", 4));

        IReadOnlyList<TrendSample> efc =
            store.ReadSeries(long.MinValue, long.MaxValue, record => record.Efc);

        Assert.Equal([-10, -11], efc.Select(sample => sample.Value));
    }

    /// <summary>
    /// A period the receiver did not answer for must stay a gap. Decimation then omits the column
    /// rather than drawing a reading nobody took.
    /// </summary>
    [Fact]
    public void ReadSeriesDropsUnreadSamplesRatherThanZeroingThem()
    {
        using TrendStore store = new(Path0);

        store.Append(new TrendRecord(At(TimeSpan.Zero), -10, null, "LOCK", 4));
        store.Append(new TrendRecord(At(TimeSpan.FromSeconds(1)), null, 6, "HOLD", 0));

        IReadOnlyList<TrendSample> tint =
            store.ReadSeries(long.MinValue, long.MaxValue, record => record.TimeIntervalNanoseconds);

        TrendSample only = Assert.Single(tint);
        Assert.Equal(6, only.Value);
        Assert.DoesNotContain(tint, sample => sample.Value == 0);
    }

    /// <summary>
    /// The store feeds the decimator directly, so the two have to agree about what a series is.
    /// </summary>
    [Fact]
    public void AStoredSeriesDecimatesForTheChart()
    {
        using TrendStore store = new(Path0);

        for (int i = 0; i < 5000; i++)
        {
            store.Append(new TrendRecord(At(TimeSpan.FromSeconds(i)), i == 2500 ? 99 : -16, -2, "LOCK", 4));
        }

        IReadOnlyList<TrendSample> series =
            store.ReadSeries(long.MinValue, long.MaxValue, record => record.Efc);

        IReadOnlyList<TrendColumn> columns = TrendDecimation.ToColumns(
            series, At(TimeSpan.Zero), At(TimeSpan.FromSeconds(5000)), 400);

        Assert.True(columns.Count <= 400);
        Assert.Contains(columns, column => column.Maximum == 99);
    }

    // ----------------------------------------------------------------------------- robustness

    /// <summary>
    /// Appending happens on the poll loop. A store that throws there stops the receiver being
    /// polled, which is a worse outcome than a gap in a trend.
    /// </summary>
    [Fact]
    public void UsingADisposedStoreIsIgnoredRatherThanThrowing()
    {
        TrendStore store = new(Path0);
        store.Append(Sample(TimeSpan.Zero));
        store.Dispose();

        Assert.Null(Record.Exception(() =>
        {
            Assert.False(store.Append(Sample(TimeSpan.FromSeconds(1))));
            Assert.Empty(store.Read(long.MinValue, long.MaxValue));
            Assert.Equal(0, store.Count());
            Assert.Equal(0, store.Compact(At(TimeSpan.FromDays(1))));
        }));
    }

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        TrendStore store = new(Path0);
        store.Dispose();

        Assert.Null(Record.Exception(store.Dispose));
    }

    [Fact]
    public void AnInvertedWindowReadsNothingRatherThanEverything()
    {
        using TrendStore store = new(Path0);
        store.Append(Sample(TimeSpan.Zero));

        Assert.Empty(store.Read(At(TimeSpan.FromDays(2)), At(TimeSpan.FromDays(1))));
    }
}
