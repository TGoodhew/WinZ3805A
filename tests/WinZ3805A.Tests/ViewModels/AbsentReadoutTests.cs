using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Drivers.Nmea;
using WinZ3805A.Device.Drivers.Uccm;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// Readouts a receiver's family can never fill are absent rather than blank (#456).
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect.</b> Connected to a VK-162, the primary window showed <c>1 PPS TI —</c>,
/// <c>TFOM —</c> and <c>FFOM —</c>. A talker has no disciplined oscillator, so none of the three
/// exists; §9.11's em dash means <i>unresolved field</i> — a reading in flight — and these are not
/// in flight. They never arrive, and §9.1 designs this window to be left on a second monitor for
/// weeks, so that is a dash somebody glances at for a fortnight.
/// </para>
/// <para>
/// <b>The distinction is declared, not inferred.</b> A null cannot tell "has not answered yet" from
/// "never will", and inferring from nulls would take the readouts away from a SmartClock that had
/// merely gone quiet — #475's lesson in a new place.
/// </para>
/// <para>
/// <b>This is a deliberate departure from §10.3</b>, whose wireframe is written around the window's
/// fixed shape. Recorded on the issue rather than left to look like an oversight.
/// </para>
/// </remarks>
public sealed class AbsentReadoutTests
{
    private static MainViewModel Model(IReceiverDriver driver)
    {
        FakeTimeProvider clock = new();
        return new MainViewModel(new ReceiverStateStore(clock), clock, driver);
    }

    private static SmartClockDriver SmartClock() => new(TimeProvider.System);

    [Fact]
    public void ATalkerHidesTheThreeReadingsItCanNeverMake()
    {
        MainViewModel model = Model(new NmeaDriver(TimeProvider.System));

        Assert.False(model.ShowsTimeInterval);
        Assert.False(model.ShowsTfom);
        Assert.False(model.ShowsFfom);
        Assert.False(model.ShowsAnyMerit);
    }

    [Fact]
    public void ASmartClockShowsAllOfThem()
    {
        MainViewModel model = Model(SmartClock());

        Assert.True(model.ShowsTimeInterval);
        Assert.True(model.ShowsTfom);
        Assert.True(model.ShowsFfom);
        Assert.True(model.ShowsAnyMerit);
    }

    /// <summary>
    /// A UCCM shows all three, and this is the test that stops the two questions being conflated.
    /// </summary>
    /// <remarks>
    /// Its <c>FastTierCarries</c> does <b>not</b> include TFOM or FFOM — they come from the status
    /// screen every ten seconds rather than the sweep every second (#475). A surface that hid a
    /// readout because the sweep does not carry it would blank a reading this receiver reports
    /// perfectly well, which is exactly the mistake #475 already had to undo once.
    /// </remarks>
    [Fact]
    public void AUccmShowsReadingsItsSweepDoesNotCarry()
    {
        UccmDriver uccm = new(TimeProvider.System);
        MainViewModel model = Model(uccm);

        Assert.False(uccm.Plan.FastTierCarries.HasFlag(FastFields.Tfom));

        Assert.True(model.ShowsTfom);
        Assert.True(model.ShowsFfom);
        Assert.True(model.ShowsAnyMerit);
    }

    /// <summary>
    /// A driver that declares nothing claims everything, and the window keeps §10.3's shape.
    /// </summary>
    /// <remarks>
    /// <c>Supplies</c> defaults to <see cref="FastFields.All"/>, so a family added later without
    /// thinking about this renders exactly as it would have before. <b>That is the safe direction:
    /// the failure mode is a dash that could have been hidden, not a reading that vanishes.</b> It
    /// also means the window does not shuffle as receivers come and go while a driver is chosen.
    /// </remarks>
    [Fact]
    public void ADriverThatDeclaresNothingClaimsEverything()
    {
        PollPlan silent = new(["*"], RefusableIndex: null, FullStatus: "*")
        {
            FastTierCarries = FastFields.None,
        };

        Assert.Equal(FastFields.All, silent.Supplies);

        MainViewModel model = Model(SmartClock());

        Assert.True(model.ShowsTimeInterval);
        Assert.True(model.ShowsAnyMerit);
    }

    /// <summary>
    /// Every driver states it, and the two questions are not accidentally the same one.
    /// </summary>
    /// <remarks>
    /// <c>Supplies</c> must be a superset of <c>FastTierCarries</c>: a tier cannot answer a field the
    /// family cannot report. A driver that got this backwards would hide a reading it is busy
    /// sending.
    /// </remarks>
    [Fact]
    public void NoDriverSweepsAFieldItsFamilyCannotReport()
    {
        IReceiverDriver[] drivers =
        [
            SmartClock(),
            new NmeaDriver(TimeProvider.System),
            new UccmDriver(TimeProvider.System),
        ];

        foreach (IReceiverDriver driver in drivers)
        {
            PollPlan plan = driver.Plan;

            Assert.True(
                (plan.FastTierCarries & ~plan.Supplies) == 0,
                $"{driver.Family} sweeps for fields it says it cannot supply: "
                + $"{plan.FastTierCarries & ~plan.Supplies}");
        }
    }
}
