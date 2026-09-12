using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Controls;
using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Drivers.Nmea;
using WinZ3805A.Device.Drivers.Uccm;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.Uccm;

/// <summary>
/// The disciplining loop's readings reach the store, the trend and the page (#512).
/// </summary>
/// <remarks>
/// <para>
/// <b>What #418 could not test.</b> Its acceptance asked for a test that a value outside the
/// oscillator sanity band never reaches the trend store, and that could not be written: nothing
/// carried a frequency difference there by any route. The band was real and guarded nothing.
/// </para>
/// </remarks>
public sealed class UccmLoopReadingsSurfacedTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);

    private static UccmDriver Driver() => new(new FakeTimeProvider(Start));

    /// <summary>A sweep whose loop answer is the 12 Sep bench reply, with the offset substituted.</summary>
    private static FastReadings Sweep(UccmDriver driver, string frequencyDifference)
    {
        string loop = $"""
            LINK0: [-1.00E+00]
            DAC_LINK    DAC_AVG    DAC_GPS   FREQ_CORR  LINK_OFF   FREQ_DIFF  FREQ_FRAC
            +5.91E-08  +5.90E-08  +5.91E-08  +0.00E+00  -6.47E-12  {frequencyDifference}  -2.57E-12
            Command complete
            """;

        string?[] answers = new string?[driver.Plan.FastTier.Count];
        answers[0] = "1";
        answers[driver.Plan.FastTier.ToList().IndexOf(UccmCommands.Loop)] = loop;

        return driver.InterpretSweep(answers).Readings;
    }

    [Fact]
    public void TheSweepCarriesTheOffsetOutOfTheLoopReply()
    {
        FastReadings readings = Sweep(Driver(), "-6.47E-12");

        // -6.47E-12 as a fraction is -0.00647 parts per billion.
        Assert.Equal(-0.00647, readings.OscillatorOffsetPpb!.Value, 6);
    }

    [Fact]
    public void ThePlanDeclaresWhatTheLoopAnswers()
    {
        PollPlan plan = Driver().Plan;

        Assert.True(plan.FastTierCarries.HasFlag(FastFields.OscillatorOffset));
        Assert.True(plan.FastTierCarries.HasFlag(FastFields.OscillatorTemperature));
        Assert.True(plan.FastTierCarries.HasFlag(FastFields.DiscipliningState));

        // And that the family can produce them at all (#456), which is the other question.
        Assert.True(plan.Supplies.HasFlag(FastFields.OscillatorOffset));
    }

    [Fact]
    public void AFamilyWithNoDiscipliningLoopSaysSo()
    {
        // An NMEA talker has no disciplined oscillator. Not late, not missing - absent.
        PollPlan plan = new NmeaDriver(TimeProvider.System).Plan;

        Assert.False(plan.Supplies.HasFlag(FastFields.OscillatorOffset));
        Assert.False(plan.Supplies.HasFlag(FastFields.DiscipliningState));
    }

    // ---- the sanity band now guards something ----------------------------------------------------

    [Fact]
    public void AValueOutsideTheSanityBandNeverReachesTheTrendStore()
    {
        // #418'S ACCEPTANCE CRITERION, WRITABLE AT LAST. Heather discards a frequency difference
        // outside ±2.00E-7 because a bogus -2.79E-7 "shows up occasionally on Trimble". Without the
        // band that value lands in the durable series and §9.10.2's chart draws a full-height
        // excursion for an event that never happened.
        FastReadings readings = Sweep(Driver(), "-2.79E-7");
        Assert.Null(readings.OscillatorOffsetPpb);

        using TrendStore store = new(":memory:");
        store.Append(new TrendRecord(Start.UtcTicks, 1.0, 2.0, "LOCK", 6)
        {
            OscillatorOffsetPpb = readings.OscillatorOffsetPpb,
        });

        TrendRecord stored = Assert.Single(store.Read(Start.UtcTicks, Start.UtcTicks));
        Assert.Null(stored.OscillatorOffsetPpb);
    }

    [Fact]
    public void AValueInsideTheBandDoesReachIt()
    {
        FastReadings readings = Sweep(Driver(), "-6.47E-12");

        using TrendStore store = new(":memory:");
        store.Append(new TrendRecord(Start.UtcTicks, 1.0, 2.0, "LOCK", 6)
        {
            OscillatorOffsetPpb = readings.OscillatorOffsetPpb,
        });

        TrendRecord stored = Assert.Single(store.Read(Start.UtcTicks, Start.UtcTicks));
        Assert.Equal(-0.00647, stored.OscillatorOffsetPpb!.Value, 6);
    }

    [Fact]
    public void ARowWrittenBeforeTheColumnExistedReadsBackNull()
    {
        // The migration's whole point: an existing trend.db keeps its shape, so the column is added
        // rather than assumed, and history written without it says null rather than zero.
        using TrendStore store = new(":memory:");
        store.Append(new TrendRecord(Start.UtcTicks, 1.0, 2.0, "LOCK", 6));

        TrendRecord stored = Assert.Single(store.Read(Start.UtcTicks, Start.UtcTicks));
        Assert.Null(stored.OscillatorOffsetPpb);
    }

    // ---- what the page shows ----------------------------------------------------------------------

    private static (TimingViewModel Model, ReceiverStateStore Store) Page()
    {
        ReceiverStateStore store = new(new FakeTimeProvider(Start));
        TimingViewModel model = new(store, Driver()) { Connection = ConnectionStatus.Connected };
        return (model, store);
    }

    private static void Push(ReceiverStateStore store, UccmDriver driver, FastReadings readings) =>
        store.UpdateFast(
            readings.SyncState, readings.Tfom, readings.Ffom,
            readings.TimeIntervalNanoseconds, readings.EfcPercent, readings.SatellitesTracked,
            driver.Plan.FastTierCarries,
            readings.OscillatorOffsetPpb, readings.OscillatorTemperature, readings.Disciplining);

    [Fact]
    public void TheOffsetRendersWithEnoughDecimalsToBeVisible()
    {
        (TimingViewModel model, ReceiverStateStore store) = Page();
        UccmDriver driver = Driver();

        Push(store, driver, Sweep(driver, "-6.47E-12"));

        // Three decimals: the bench values are hundredths of a ppb, and one or two would render
        // every reading this receiver has ever given as a flat zero. U+2212 rather than a hyphen,
        // which is §9.5.3's rule for every readout.
        Assert.Contains("−0.006", model.OscillatorOffsetText, StringComparison.Ordinal);
        Assert.Contains("ppb", model.OscillatorOffsetText, StringComparison.Ordinal);
    }

    [Fact]
    public void TemperatureIsAnEmDashOnATrimbleRatherThanAZero()
    {
        // §11.1, and §418's acceptance. A zero would read as a real "no correction applied" on a
        // module that has no temperature reading to give at all.
        (TimingViewModel model, ReceiverStateStore store) = Page();
        UccmDriver driver = Driver();

        Push(store, driver, Sweep(driver, "-6.47E-12"));

        Assert.Equal(ReadoutFormatter.NoValue, model.OscillatorTemperatureText);
        Assert.DoesNotContain("0", model.OscillatorTemperatureText, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscipliningReadsAsNotKnownOnTheMeasuredTrimbleRatherThanNo()
    {
        // THE ONE THAT MATTERS. FREQ_CORR is +0.00E+00 on the bench module in every sitting taken,
        // while locked and disciplining, so zero was measured to mean "cannot tell" (#418). A "No"
        // here would be a confident wrong answer on the only hardware this has met.
        (TimingViewModel model, ReceiverStateStore store) = Page();
        UccmDriver driver = Driver();

        Push(store, driver, Sweep(driver, "-6.47E-12"));

        Assert.Equal("Not known", model.DiscipliningText);
    }

    [Fact]
    public void DiscipliningIsLabelledAnInferenceWhenItIsKnown()
    {
        (TimingViewModel model, ReceiverStateStore store) = Page();
        UccmDriver driver = Driver();

        string loop = """
            DAC_LINK   DAC_AVG   DAC_GPS  FREQ_CORR  LINK_OFF  FREQ_DIFF  FREQ_FRAC
            +1E-8      +1E-8     +1E-8    +1.5E-11   +0.0      -6.47E-12  +0.0
            """;
        string?[] answers = new string?[driver.Plan.FastTier.Count];
        answers[0] = "1";
        answers[driver.Plan.FastTier.ToList().IndexOf(UccmCommands.Loop)] = loop;

        Push(store, driver, driver.InterpretSweep(answers).Readings);

        Assert.Equal("Yes (inferred)", model.DiscipliningText);
    }

    [Fact]
    public void ATalkerHidesTheWholeCardRatherThanShowingThreeDashes()
    {
        ReceiverStateStore store = new(new FakeTimeProvider(Start));
        TimingViewModel talker = new(store, new NmeaDriver(TimeProvider.System))
        {
            Connection = ConnectionStatus.Connected,
        };

        Assert.False(talker.ReportsLoop);
        Assert.True(new TimingViewModel(store, Driver()).ReportsLoop);
    }
}
