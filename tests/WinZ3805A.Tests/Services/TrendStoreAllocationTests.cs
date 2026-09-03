using WinZ3805A.Controls;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// #390: a read allocates its list once rather than growing it from empty.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured rather than asserted structurally, because the defect was a quantity.</b> A
/// <c>List&lt;T&gt;</c> that grows from empty doubles as it goes; every doubling past a few thousand
/// records allocates an array over 85 KB, which lands on the large object heap and stays there,
/// because the LOH is not compacted. The consequence in #385 was 1.1 GB of LOH and 36 MB/s of
/// allocation on an idle instrument.
/// </para>
/// <para>
/// <b>Why <see cref="GC.GetAllocatedBytesForCurrentThread"/> and not a mock.</b> The thing being
/// fixed is how many bytes the method asks for, which no seam can observe. This counter is exact
/// for the calling thread, and xunit runs a test on one thread, so the number below is the method's
/// own allocation plus the reader's — which is why the bound is generous rather than tight.
/// </para>
/// </remarks>
public sealed class TrendStoreAllocationTests : IDisposable
{
    private readonly TrendStore _store = new(":memory:");
    private static readonly long Start = new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero).UtcTicks;

    /// <inheritdoc />
    public void Dispose() => _store.Dispose();

    private int Fill(int samples)
    {
        for (int i = 0; i < samples; i++)
        {
            _store.Append(new TrendRecord(
                Start + (TimeSpan.TicksPerSecond * i),
                -16.5 + (i % 100 * 0.001),
                (i % 7) - 3.0,
                "LOCK",
                8));
        }

        return samples;
    }

    /// <summary>
    /// Reading 40,000 records allocates about one list's worth, not a doubling series' worth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 40,000 <c>TrendRecord</c>s is a little over eleven hours of one-second samples, and well
    /// inside the 24 h range §10.7 offers. The record is 56 bytes, so the list itself is about
    /// 2.2 MB.
    /// </para>
    /// <para>
    /// <b>The bound is 6 MB, and the arithmetic behind it is the point.</b> Grown from empty, the
    /// list doubles sixteen times and allocates roughly 4.4 MB in total — twice the final size —
    /// leaving fifteen dead arrays behind, most of them on the LOH. Sized once, it allocates 2.2 MB
    /// and leaves nothing. The reader's own strings and boxes sit on top of both, which is why this
    /// is not asserted to the byte: it is asserted below the doubling series and above the honest
    /// single allocation.
    /// </para>
    /// </remarks>
    [Fact]
    public void AReadAllocatesOneListRatherThanADoublingSeries()
    {
        int count = Fill(40_000);

        // Warm the query plan and the reader's own one-time allocations out of the measurement.
        _ = _store.Read(Start, Start + TimeSpan.TicksPerSecond);

        long before = GC.GetAllocatedBytesForCurrentThread();
        IReadOnlyList<TrendRecord> window = _store.Read(Start, Start + (TimeSpan.TicksPerSecond * count));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(count, window.Count);
        Assert.True(
            allocated < 6 * 1024 * 1024,
            $"a {count:N0}-record read allocated {allocated / 1024.0 / 1024.0:N1} MB; grown from empty it " +
            "would allocate about twice the list's size and leave the intermediate arrays on the LOH (#390)");
    }

    /// <summary>The same for the series read, which builds a second list from the first.</summary>
    [Fact]
    public void ASeriesReadIsSizedFromItsWindow()
    {
        int count = Fill(40_000);

        _ = _store.ReadSeries(Start, Start + TimeSpan.TicksPerSecond, r => r.Efc);

        long before = GC.GetAllocatedBytesForCurrentThread();
        IReadOnlyList<TrendSample> series = _store.ReadSeries(
            Start, Start + (TimeSpan.TicksPerSecond * count), record => record.Efc);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(count, series.Count);
        Assert.True(
            allocated < 9 * 1024 * 1024,
            $"a {count:N0}-sample series read allocated {allocated / 1024.0 / 1024.0:N1} MB (#390)");
    }

    /// <summary>
    /// An empty window costs nothing, which is the case the count query has to not make worse.
    /// </summary>
    /// <remarks>
    /// The capacity hint costs one extra query per read. On a window with nothing in it that query
    /// is the whole cost, and it must stay small — this is the guard on having added it at all.
    /// </remarks>
    [Fact]
    public void AnEmptyWindowStaysCheap()
    {
        Fill(1_000);

        long empty = Start + TimeSpan.TicksPerDay;
        _ = _store.Read(empty, empty + TimeSpan.TicksPerHour);

        long before = GC.GetAllocatedBytesForCurrentThread();
        IReadOnlyList<TrendRecord> window = _store.Read(empty, empty + TimeSpan.TicksPerHour);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Empty(window);
        Assert.True(allocated < 64 * 1024, $"an empty read allocated {allocated:N0} bytes (#390)");
    }

    /// <summary>The count is of the window, not of the table.</summary>
    /// <remarks>
    /// A capacity taken from <see cref="TrendStore.Count"/> would size every read to the whole
    /// eight weeks of history, which would be a worse defect than the one being fixed: a 1 h chart
    /// would allocate a 56-day list. This is the test that would catch that.
    /// </remarks>
    [Fact]
    public void ANarrowReadDoesNotPayForTheWholeTable()
    {
        Fill(40_000);

        long from = Start + (TimeSpan.TicksPerSecond * 39_000);
        _ = _store.Read(from, from + TimeSpan.TicksPerSecond);

        long before = GC.GetAllocatedBytesForCurrentThread();
        IReadOnlyList<TrendRecord> window = _store.Read(from, from + (TimeSpan.TicksPerSecond * 100));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(101, window.Count);
        Assert.True(
            allocated < 128 * 1024,
            $"a 101-record read out of 40,000 allocated {allocated:N0} bytes, which is the shape of a " +
            "capacity taken from the whole table rather than from the window (#390)");
    }
}
