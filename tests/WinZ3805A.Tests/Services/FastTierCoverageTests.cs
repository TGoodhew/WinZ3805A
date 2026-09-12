using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Drivers.Nmea;
using WinZ3805A.Device.Drivers.Uccm;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// Which tier a reading comes from, and what a null from the other one is allowed to do (#475).
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect these exist to make impossible.</b> A connected, locked Trimble UCCM-P showed
/// <c>TFOM —</c>, <c>FFOM —</c> and no satellite count permanently, while its status screen carried
/// all three every ten seconds and the parser read them correctly. The sweep wrote every fast-tier
/// field unconditionally — right for a field it asks about, because a reading the receiver has
/// stopped giving must not stand — and the UCCM's sweep does not ask about those three. So the
/// screen stored them and the sweep nulled them a second later, ten times over, and the value was
/// present in the state and unreachable by the display.
/// </para>
/// <para>
/// <b>It is not a UCCM quirk, which is why the fix is in the contract.</b> Nothing said which tier
/// carried which reading, and a null could not distinguish "asked, and not answered" from "this
/// family never asks here". Any driver whose plan put a reading on the full tier lost it.
/// </para>
/// </remarks>
public sealed class FastTierCoverageTests
{
    private const FastFields UccmShape =
        FastFields.SyncState | FastFields.TimeInterval | FastFields.OscillatorControl;

    private static ReceiverStateStore Store() => new(new FakeTimeProvider());

    private static ReceiverStatus Screen(int? tfom = 2, int? ffom = 0, int tracked = 7) => new()
    {
        Tfom = tfom,
        Ffom = ffom,
        Tracked = [.. Enumerable.Range(1, tracked).Select(prn => new TrackedSatellite { Prn = prn })],
    };

    [Fact]
    public void TheScreenSuppliesTheReadingsTheSweepDoesNotAskFor()
    {
        ReceiverStateStore store = Store();

        store.UpdateFull(Screen(), UccmShape);

        Assert.Equal(2, store.Tfom);
        Assert.Equal(0, store.Ffom);
        Assert.Equal(7, store.TrackedCount);
    }

    [Fact]
    public void ASweepThatDoesNotCarryAReadingLeavesItAlone()
    {
        ReceiverStateStore store = Store();
        store.UpdateFull(Screen(), UccmShape);

        // Ten sweeps, which is what fits between two screens at the UCCM's cadence. Every one of
        // them answers null for TFOM, FFOM and the tracked count, because it never asked.
        for (int i = 0; i < 10; i++)
        {
            store.UpdateFast("LOCK", null, null, -5.4, -16.8, null, UccmShape);
        }

        Assert.Equal(2, store.Tfom);
        Assert.Equal(0, store.Ffom);
        Assert.Equal(7, store.TrackedCount);
    }

    [Fact]
    public void ASweepThatDoesCarryAReadingStillBlanksIt()
    {
        // The other half of the rule, and the one that must not regress: where the sweep asks, a
        // null is the receiver having stopped answering, and the display must go to an em dash
        // rather than keep showing a number nobody stands behind.
        ReceiverStateStore store = Store();
        store.UpdateFast("LOCK", 3, 1, -5.4, -16.8, 6, FastFields.All);

        store.UpdateFast("LOCK", null, null, -5.4, -16.8, null, FastFields.All);

        Assert.Null(store.Tfom);
        Assert.Null(store.Ffom);
        Assert.Null(store.TrackedCount);
    }

    [Fact]
    public void TheScreenDoesNotOverwriteAReadingTheSweepCarries()
    {
        // A ten-second-old screen must not win over a one-second-old answer — and must not quietly
        // restore a value the sweep blanked on purpose, which would undo the test above from the
        // other direction.
        ReceiverStateStore store = Store();
        store.UpdateFast("LOCK", null, null, -5.4, -16.8, null, FastFields.All);

        store.UpdateFull(Screen(), FastFields.All);

        Assert.Null(store.Tfom);
        Assert.Null(store.Ffom);
        Assert.Null(store.TrackedCount);
    }

    [Fact]
    public void ASweepWithNoTimeIntervalDoesNotAdvanceTheTrendRing()
    {
        // The ring is the time interval's own history. A driver that never measures one must not
        // push a null into it every second: that fills the sparkline with gaps saying "no reading"
        // where the truth is "never asked".
        ReceiverStateStore store = Store();

        for (int i = 0; i < 5; i++)
        {
            store.UpdateFast("LOCK", null, null, null, -16.8, null, FastFields.SyncState);
        }

        Assert.Empty(store.RecentTimeInterval);
    }

    [Fact]
    public void ASweepThatCarriesTheTimeIntervalStillAdvancesIt()
    {
        ReceiverStateStore store = Store();

        store.UpdateFast("LOCK", null, null, 1.5, -16.8, null, UccmShape);

        Assert.Equal([1.5], store.RecentTimeInterval);
    }

    [Fact]
    public void TheUccmSweepDoesNotClaimTheReadingsOnlyItsScreenCarries()
    {
        // TFOM, FFOM and the tracked count are on `SYST:STAT?` and nowhere else in this family:
        // there is no scalar query for any of them, so the plan must not say it answers them.
        PollPlan plan = new UccmDriver(new FakeTimeProvider()).Plan;

        Assert.False(plan.FastTierCarries.HasFlag(FastFields.Tfom));
        Assert.False(plan.FastTierCarries.HasFlag(FastFields.Ffom));
        Assert.False(plan.FastTierCarries.HasFlag(FastFields.SatellitesTracked));

        // What it DOES carry, stated rather than asserted as an exact set. This was
        // `Assert.Equal(UccmShape, ...)` until #512, which meant that teaching the sweep to answer
        // something new failed a test whose own name is about what it must not claim.
        Assert.True(plan.FastTierCarries.HasFlag(FastFields.SyncState));
        Assert.True(plan.FastTierCarries.HasFlag(FastFields.TimeInterval));
        Assert.True(plan.FastTierCarries.HasFlag(FastFields.OscillatorControl));

        // The disciplining loop's three, added by #512 and read from the same DIAG:LOOP? reply.
        Assert.True(plan.FastTierCarries.HasFlag(FastFields.OscillatorOffset));
        Assert.True(plan.FastTierCarries.HasFlag(FastFields.OscillatorTemperature));
        Assert.True(plan.FastTierCarries.HasFlag(FastFields.DiscipliningState));
    }

    [Fact]
    public void TheSmartClockSweepCarriesAllSix()
    {
        // §7.3's sweep asks every one of them, which is why the common currency has these fields
        // and no others.
        PollPlan plan = new SmartClockDriver(new FakeTimeProvider()).Plan;

        Assert.Equal(FastFields.All, plan.FastTierCarries);
    }

    [Fact]
    public void TheTalkerSweepCarriesOnlyWhatNmeaSends()
    {
        // A talker has no figure of merit, no interval against a reference and no oscillator to
        // control (#435, #456).
        PollPlan plan = new NmeaDriver(new FakeTimeProvider()).Plan;

        Assert.Equal(FastFields.SyncState | FastFields.SatellitesTracked, plan.FastTierCarries);
    }
}
