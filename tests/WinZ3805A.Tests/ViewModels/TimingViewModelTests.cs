using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Controls;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// The §10.7 Timing page's calculator and its comparison against the receiver.
/// </summary>
public sealed class TimingViewModelTests
{
    private static readonly DateTimeOffset Captured = new(2026, 8, 13, 19, 0, 0, TimeSpan.Zero);

    private static (TimingViewModel Model, ReceiverStateStore Store) Connected(double? antennaDelay = 77)
    {
        FakeTimeProvider clock = new(Captured);
        ReceiverStateStore store = new(clock);

        store.UpdateFull(new ReceiverStatus
        {
            AntennaDelayNanoseconds = antennaDelay,
            CapturedAt = Captured,
        });

        return (new TimingViewModel(store) { Connection = ConnectionStatus.Connected }, store);
    }

    /// <remarks>P0-11's own numbers, through the page rather than through the model.</remarks>
    [Fact]
    public void TheDefaultCalculationIsTheP011Example()
    {
        (TimingViewModel model, _) = Connected();

        Assert.Same(AntennaCable.Lmr400, model.EffectiveCable);
        Assert.Equal(20, model.LengthMetres);
        Assert.NotNull(model.ComputedDelayNanoseconds);
        Assert.InRange(model.ComputedDelayNanoseconds.Value, 78.2, 79.2);
    }

    /// <remarks>
    /// The reason a user opens this page. The receiver subtracts whatever it was told, so a gap
    /// here is a systematic offset of exactly this size on the 1 PPS output that nothing else will
    /// flag.
    /// </remarks>
    [Fact]
    public void TheDifferenceAgainstTheReceiverIsShown()
    {
        (TimingViewModel model, _) = Connected(antennaDelay: 77);

        Assert.NotNull(model.DifferenceNanoseconds);
        Assert.Equal(1.6, model.DifferenceNanoseconds.Value, 1);
        Assert.True(model.IsDifferenceSignificant);
        Assert.Equal(Severity.Caution, model.DifferenceSeverity);
    }

    /// <remarks>
    /// One nanosecond is 30 cm of cable, which is inside anyone's measurement of a run. Below that
    /// the two agree for every practical purpose and a caution would be noise.
    /// </remarks>
    [Fact]
    public void ASubNanosecondDifferenceIsNotWorthACaution()
    {
        (TimingViewModel model, _) = Connected(antennaDelay: 78.6);

        Assert.False(model.IsDifferenceSignificant);
        Assert.Equal(Severity.Success, model.DifferenceSeverity);
    }

    [Fact]
    public void WithNoReportedDelayThereIsNothingToCompare()
    {
        (TimingViewModel model, _) = Connected(antennaDelay: null);

        Assert.Null(model.DifferenceNanoseconds);
        Assert.Equal(string.Empty, model.DifferenceText);
        Assert.Equal(ReadoutFormatter.NoValue, model.CurrentDelayText);
    }

    [Fact]
    public void ChoosingAnotherCableRecalculates()
    {
        (TimingViewModel model, _) = Connected();

        model.Cable = AntennaCable.Rg213;

        Assert.NotNull(model.ComputedDelayNanoseconds);
        Assert.Equal(101.0, model.ComputedDelayNanoseconds.Value, 1);
    }

    [Fact]
    public void TheCustomVelocityFactorTakesOver()
    {
        (TimingViewModel model, _) = Connected();

        model.UseVelocityFactor = true;
        model.VelocityFactor = 0.66;

        Assert.NotNull(model.EffectiveCable);
        Assert.Contains("velocity factor", model.EffectiveCable.Name);
        Assert.Equal(101.1, model.ComputedDelayNanoseconds!.Value, 1);
    }

    /// <remarks>
    /// A half-typed velocity factor is not an error worth an exception, but it is also not a
    /// calculation — the page says what to do rather than showing a number from nothing.
    /// </remarks>
    [Fact]
    public void AnUnusableVelocityFactorProducesNoCalculation()
    {
        (TimingViewModel model, _) = Connected();

        model.UseVelocityFactor = true;
        model.VelocityFactor = 0;

        Assert.Null(model.EffectiveCable);
        Assert.Null(model.ComputedDelayNanoseconds);
        Assert.Equal(ReadoutFormatter.NoValue, model.ComputedDelayText);
        Assert.Contains("between 0 and 1", model.CableSourceText);
    }

    /// <remarks>
    /// §10.7 gives the field 0 – 999 999 ns. A run long enough to exceed it is refused here rather
    /// than by the device, whose error the user could do nothing with.
    /// </remarks>
    [Fact]
    public void ARunTooLongForTheReceiverIsRefusedClientSide()
    {
        (TimingViewModel model, _) = Connected();

        model.LengthMetres = 300_000;

        Assert.NotNull(model.ComputedDelayNanoseconds);
        Assert.False(model.IsComputedDelayAcceptable);
    }

    // ---- Deviation ---------------------------------------------------------------------------

    /// <remarks>
    /// Two points define a line. A standard deviation from fewer than three samples is arithmetic
    /// without meaning, and showing one would invite a reader to trust it.
    /// </remarks>
    [Fact]
    public void FewerThanThreeSamplesHaveNoDeviation()
    {
        FakeTimeProvider clock = new(Captured);
        ReceiverStateStore store = new(clock);
        TimingViewModel model = new(store) { Connection = ConnectionStatus.Connected };

        store.UpdateFast("LOCK", 3, 0, -10.0, 1.0, 6);
        store.UpdateFast("LOCK", 3, 0, -12.0, 1.0, 6);

        Assert.Null(model.TimeIntervalDeviation);
    }

    [Fact]
    public void TheDeviationIsTheSampleStandardDeviation()
    {
        FakeTimeProvider clock = new(Captured);
        ReceiverStateStore store = new(clock);
        TimingViewModel model = new(store) { Connection = ConnectionStatus.Connected };

        foreach (double sample in new[] { 2.0, 4.0, 4.0, 4.0, 5.0, 5.0, 7.0, 9.0 })
        {
            store.UpdateFast("LOCK", 3, 0, sample, 1.0, 6);
        }

        // Sample standard deviation of that set is 2.138…; the population figure would be 2.0.
        Assert.NotNull(model.TimeIntervalDeviation);
        Assert.Equal(2.138, model.TimeIntervalDeviation.Value, 2);
    }

    /// <remarks>
    /// §10.7's wireframe says "σ (1 h)". Nothing keeps an hour yet, so the window is named for what
    /// it is rather than letting a reader assume an hour of data behind it.
    /// </remarks>
    [Fact]
    public void TheDeviationWindowIsNamedHonestly()
    {
        FakeTimeProvider clock = new(Captured);
        ReceiverStateStore store = new(clock);
        TimingViewModel model = new(store) { Connection = ConnectionStatus.Connected };

        Assert.Equal("no samples yet", model.DeviationWindow);

        for (int i = 0; i < 5; i++)
        {
            store.UpdateFast("LOCK", 3, 0, i, 1.0, 6);
        }

        Assert.Equal(5, model.DeviationSampleCount);
        Assert.Equal("last 5 s", model.DeviationWindow);
    }

    [Fact]
    public void DisconnectedEmptiesTheReceiverSideReadings()
    {
        (TimingViewModel model, _) = Connected();
        model.Connection = ConnectionStatus.Disconnected;

        Assert.Null(model.CurrentDelayNanoseconds);
        Assert.Null(model.TimeIntervalNanoseconds);

        // The calculator still works. It is arithmetic about a cable, not about a receiver, and a
        // user planning an installation has no reason to be denied it.
        Assert.NotNull(model.ComputedDelayNanoseconds);
    }

    // -------------------------------------------------------------------------------------
    // §10.7's two routes to a delay, and what Apply would send
    // -------------------------------------------------------------------------------------

    /// <summary>By default the card is calculating, and Apply would send what it computed.</summary>
    [Fact]
    public void TheCalculatorFeedsApplyByDefault()
    {
        (TimingViewModel model, _) = Connected();

        Assert.False(model.UseDirectEntry);
        Assert.Equal(model.ComputedDelayNanoseconds, model.DelayToApplyNanoseconds);
        Assert.True(model.CanApplyDelay);
    }

    /// <summary>Switching to direct entry switches what Apply sends, and nothing else.</summary>
    [Fact]
    public void DirectEntryFeedsApplyInstead()
    {
        (TimingViewModel model, _) = Connected();

        model.UseDirectEntry = true;
        model.DirectDelayNanoseconds = 250;

        Assert.Equal(250, model.DelayToApplyNanoseconds);
        Assert.True(model.CanApplyDelay);

        // The calculator has not been reset by being deselected — a user toggling back should find
        // their cable and length where they left them.
        Assert.NotNull(model.ComputedDelayNanoseconds);
    }

    /// <summary>
    /// <b>The case the field validator cannot catch.</b> A 300 m run and an ordinary cable are each
    /// perfectly valid, and the delay they produce is not — so §9.11's "Apply stays disabled while
    /// any field is invalid" has to cover the derived value, or the one number actually being sent
    /// is the one nothing checked.
    /// </summary>
    [Fact]
    public void AComputedDelayPastTheReceiversCeilingBlocksApply()
    {
        (TimingViewModel model, _) = Connected();

        model.Cable = AntennaCable.Presets.First(cable => cable.Name.Contains("RG-213", StringComparison.Ordinal));
        model.LengthMetres = 300000;

        Assert.NotNull(model.DelayToApplyNanoseconds);
        Assert.False(model.IsComputedDelayAcceptable);
        Assert.False(model.CanApplyDelay);
    }

    /// <summary>Nothing is sent to a receiver that is not there.</summary>
    [Fact]
    public void ApplyIsNotOfferedWhileDisconnected()
    {
        (TimingViewModel model, _) = Connected();
        model.Connection = ConnectionStatus.Disconnected;

        Assert.False(model.CanApplyDelay);
    }
}
