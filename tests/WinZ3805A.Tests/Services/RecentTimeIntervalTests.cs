using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// The ring snapshot's identity, which a guard elsewhere depends on (#403).
/// </summary>
/// <remarks>
/// <b>This is not a test about allocation for its own sake.</b> <c>StatusMedallion.Samples</c> is
/// assigned on every render and guarded by a reference comparison, so an unchanged ring is supposed
/// to cost nothing. Handing a managed object to WinRT mints a COM callable wrapper that the runtime
/// records in storage it never shrinks, so "costs nothing" is the difference between flat memory and
/// half a megabyte an hour.
///
/// The store used to rebuild this array on every read. That made the reference comparison never
/// match, so the guard was inert from the day it was written — and nothing failed, because a guard
/// that does not fire looks exactly like a guard that had nothing to do. It took a soak, a heap
/// histogram and an allocation trace to find. This test is what makes it visible instead.
/// </remarks>
public sealed class RecentTimeIntervalTests
{
    private static ReceiverStateStore Store() => new(new FakeTimeProvider());

    [Fact]
    public void TheSameInstanceComesBackUntilASampleArrives()
    {
        ReceiverStateStore store = Store();
        store.UpdateFast("LOCK", 3, 0, 1.5, 0.5, 6);

        object first = store.RecentTimeInterval;

        Assert.Same(first, store.RecentTimeInterval);
        Assert.Same(first, store.RecentTimeInterval);
    }

    [Fact]
    public void ANewSampleReplacesIt()
    {
        ReceiverStateStore store = Store();
        store.UpdateFast("LOCK", 3, 0, 1.5, 0.5, 6);
        object before = store.RecentTimeInterval;

        store.UpdateFast("LOCK", 3, 0, 2.5, 0.5, 6);

        Assert.NotSame(before, store.RecentTimeInterval);
    }

    [Fact]
    public void TheSnapshotIsReplacedRatherThanMutated()
    {
        // A caller holding the previous snapshot must still see a coherent ring, which is what
        // makes replacing it safe to do from the poll thread while the UI thread reads.
        ReceiverStateStore store = Store();
        store.UpdateFast("LOCK", 3, 0, 1.0, 0.5, 6);

        IReadOnlyList<double?> held = store.RecentTimeInterval;
        double?[] copy = [.. held];

        store.UpdateFast("LOCK", 3, 0, 2.0, 0.5, 6);

        Assert.Equal(copy, held);
    }

    [Fact]
    public void ItStillReportsTheSamplesOldestFirst()
    {
        // The snapshot moved from the getter to the writer; the ordering it produces must not have.
        ReceiverStateStore store = Store();
        foreach (double value in new double[] { 1, 2, 3 })
        {
            store.UpdateFast("LOCK", 3, 0, value, 0.5, 6);
        }

        Assert.Equal([1.0, 2.0, 3.0], store.RecentTimeInterval);
    }

    [Fact]
    public void ItWrapsAtTheWindowAndKeepsTheNewest()
    {
        ReceiverStateStore store = Store();
        for (int i = 1; i <= ReceiverStateStore.TimeIntervalWindow + 3; i++)
        {
            store.UpdateFast("LOCK", 3, 0, i, 0.5, 6);
        }

        IReadOnlyList<double?> ring = store.RecentTimeInterval;

        Assert.Equal(ReceiverStateStore.TimeIntervalWindow, ring.Count);
        Assert.Equal(4.0, ring[0]);
        Assert.Equal((double)(ReceiverStateStore.TimeIntervalWindow + 3), ring[^1]);
    }
}
