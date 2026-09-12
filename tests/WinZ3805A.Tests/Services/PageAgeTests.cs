using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// Which poll a page of mixed readings is aged against (#479).
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect these exist to make impossible.</b> A Trimble UCCM-P sweeps every second and prints
/// its screen every ten, and TFOM, FFOM and the satellite count are on the screen. The primary window
/// showed one page age taken from the fast sweep, so <b>a TFOM that was nine seconds old was reported
/// as one second old</b> — and §9.11's staleness treatment, which dims a reading and shows its age
/// past a threshold, ran off the wrong number for exactly the readings it matters most for.
/// </para>
/// <para>
/// It understated, which is the unsafe direction: a stale reading looked fresh rather than a fresh
/// one looking stale.
/// </para>
/// <para>
/// <b>The fix is deliberately pessimistic</b> for the fast readings sharing the page. A
/// one-second-old sync state shown as ten seconds old looks worse than it is; a nine-second-old TFOM
/// shown as one second old looks better than it is. Only one of those two errors is safe.
/// </para>
/// </remarks>
public sealed class PageAgeTests
{
    /// <summary>The UCCM's shape: the screen carries TFOM, FFOM and the satellite count.</summary>
    private const FastFields SplitPlan =
        FastFields.SyncState | FastFields.TimeInterval | FastFields.OscillatorControl;

    private static ReceiverStatus Screen() => new() { Tfom = 2, Ffom = 0 };

    [Fact]
    public void ASplitPlanIsAgedAgainstTheScreenThatCarriesHalfThePage()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        ReceiverStateStore store = new(clock);

        // A screen, then nine seconds of sweeps - the UCCM's real cadence.
        store.UpdateFull(Screen(), SplitPlan);
        for (int second = 0; second < 9; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            store.UpdateFast("LOCK", null, null, 2.8, 47.5, null, SplitPlan);
        }

        // THE DEFECT: aged against the fast sweep this is one second. The TFOM on that page came
        // from the screen and is nine.
        Assert.Equal(TimeSpan.FromSeconds(9), store.AgeOf(store.LastDisplayedPoll));
        Assert.Equal(TimeSpan.Zero, store.AgeOf(store.LastFastPoll));
    }

    [Fact]
    public void ADriverWhoseSweepCarriesEverythingIsUnaffected()
    {
        // The SmartClock, and every family until the UCCM. The old behaviour must be reproduced
        // exactly, or this change would make every existing receiver's window pessimistic for
        // nothing.
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        ReceiverStateStore store = new(clock);

        store.UpdateFull(Screen(), FastFields.All);
        clock.Advance(TimeSpan.FromSeconds(9));
        store.UpdateFast("LOCK", 3, 0, 2.8, 47.5, 8, FastFields.All);

        Assert.Equal(store.LastFastPoll, store.LastDisplayedPoll);
        Assert.Equal(TimeSpan.Zero, store.AgeOf(store.LastDisplayedPoll));
    }

    [Fact]
    public void BeforeTheFirstScreenThereIsNothingScreenBorneToAge()
    {
        // A split plan whose screen has not arrived yet shows blanks where the screen's readings
        // will go, so there is nothing on the page older than the sweep. Aging it to null - or to
        // some notional infinity - would report a staleness about readings that are not shown.
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        ReceiverStateStore store = new(clock);

        store.UpdateFast("LOCK", null, null, 2.8, 47.5, null, SplitPlan);

        Assert.Equal(store.LastFastPoll, store.LastDisplayedPoll);
        Assert.NotNull(store.LastDisplayedPoll);
    }

    [Fact]
    public void BeforeAnythingAtAllThereIsNoAge()
    {
        ReceiverStateStore store = new(new FakeTimeProvider());

        Assert.Null(store.LastDisplayedPoll);
        Assert.Null(store.AgeOf(store.LastDisplayedPoll));
    }

    /// <summary>
    /// A screen that arrives before any sweep still dates the page.
    /// </summary>
    /// <remarks>
    /// Reachable on a fresh connection, and the reason <c>UpdateFull</c> records the plan's shape as
    /// well as <c>UpdateFast</c>: judging the page by a default shape would be judging it by the
    /// previous driver's.
    /// </remarks>
    [Fact]
    public void AScreenBeforeAnySweepStillDatesThePage()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        ReceiverStateStore store = new(clock);

        store.UpdateFull(Screen(), SplitPlan);
        clock.Advance(TimeSpan.FromSeconds(4));

        Assert.Equal(store.LastFullPoll, store.LastDisplayedPoll);
        Assert.Equal(TimeSpan.FromSeconds(4), store.AgeOf(store.LastDisplayedPoll));
    }

    /// <summary>The surface is told, or it would go on painting the age it last read.</summary>
    [Fact]
    public void TheDerivedAgeRaisesPropertyChanged()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        ReceiverStateStore store = new(clock);

        List<string?> changed = [];
        store.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        store.UpdateFast("LOCK", null, null, 2.8, 47.5, null, SplitPlan);
        Assert.Contains(nameof(ReceiverStateStore.LastDisplayedPoll), changed);

        changed.Clear();
        store.UpdateFull(Screen(), SplitPlan);
        Assert.Contains(nameof(ReceiverStateStore.LastDisplayedPoll), changed);
    }

    /// <summary>
    /// A driver whose sweep carries <i>nothing</i> is aged by its screen, which is the whole page.
    /// </summary>
    /// <remarks>
    /// <c>FastFields.None</c> is a documented shape — "a driver whose readings all come from the
    /// screen" — and the arithmetic must not treat "carries nothing" as "carries everything".
    /// </remarks>
    [Fact]
    public void ADriverWhoseSweepCarriesNothingIsAgedByItsScreen()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
        ReceiverStateStore store = new(clock);

        store.UpdateFull(Screen(), FastFields.None);
        clock.Advance(TimeSpan.FromSeconds(7));
        store.UpdateFast(null, null, null, null, null, null, FastFields.None);

        Assert.Equal(store.LastFullPoll, store.LastDisplayedPoll);
        Assert.Equal(TimeSpan.FromSeconds(7), store.AgeOf(store.LastDisplayedPoll));
    }
}
