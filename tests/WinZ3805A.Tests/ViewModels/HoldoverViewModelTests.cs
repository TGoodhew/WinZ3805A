using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Controls;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// The §10.8 Holdover page's judgements.
/// </summary>
public sealed class HoldoverViewModelTests
{
    private static readonly DateTimeOffset Captured = new(2026, 8, 13, 19, 0, 0, TimeSpan.Zero);

    private static HoldoverViewModel Connected(
        string syncState = "LOCK",
        double? predicted = 2.0e-6,
        double? threshold = 1.0e-6,
        double? present = null,
        string? modeDetail = null)
    {
        FakeTimeProvider clock = new(Captured);
        ReceiverStateStore store = new(clock);

        store.UpdateFull(new ReceiverStatus
        {
            HoldoverPredictedSeconds = predicted,
            HoldThresholdSeconds = threshold,
            HoldoverPresentSeconds = present,
            ModeDetail = modeDetail,
            CapturedAt = Captured,
        });

        store.UpdateFast(syncState, 3, 0, -10.0, 1.0, 6);

        return new HoldoverViewModel(store) { Connection = ConnectionStatus.Connected };
    }

    /// <remarks>
    /// Holding, Waiting to Recover and Recovering are three separate register bits (#34) and three
    /// separate <c>:SYNC:STAT?</c> answers, but all three mean the 10 MHz is running on the
    /// oscillator's own memory. A page counting only HOLD would report "not in holdover" while the
    /// receiver was recovering from one.
    /// </remarks>
    [Theory]
    [InlineData("HOLD", true)]
    [InlineData("WAIT", true)]
    [InlineData("REC", true)]
    [InlineData("LOCK", false)]
    [InlineData("POW", false)]
    public void AllThreeHoldoverStatesCountAsHoldover(string syncState, bool expected) =>
        Assert.Equal(expected, Connected(syncState).IsInHoldover);

    /// <remarks>
    /// Holding is critical because the error grows for as long as it lasts and nothing downstream
    /// says so. Waiting and recovering are cautions: the outputs are usable and the receiver is on
    /// its way back.
    /// </remarks>
    [Theory]
    [InlineData("LOCK", Severity.Success)]
    [InlineData("HOLD", Severity.Critical)]
    [InlineData("WAIT", Severity.Caution)]
    [InlineData("REC", Severity.Caution)]
    public void TheStateSeverityDistinguishesHoldingFromRecovering(string syncState, Severity expected) =>
        Assert.Equal(expected, Connected(syncState).StateSeverity);

    /// <remarks>
    /// The 58503A guide is explicit that <c>:SYNC:HOLD:TUNC:PRESent?</c> answers error −230 when
    /// the receiver is not in holdover (#34), so showing a present error while locked would be
    /// showing a figure the device declines to give.
    /// </remarks>
    [Fact]
    public void ThePresentErrorIsOnlyShownDuringHoldover()
    {
        Assert.Equal(ReadoutFormatter.NoValue, Connected("LOCK", present: 4.2e-6).PresentError.Value);
        Assert.Equal("4.2", Connected("HOLD", present: 4.2e-6).PresentError.Value);
    }

    /// <remarks>
    /// Both figures come off the wire in seconds and are compared as such. The display units may
    /// differ, which is exactly why the comparison is made here rather than left to a reader
    /// looking at "2.0 µs" beside "1.000 µs".
    /// </remarks>
    [Fact]
    public void TheThresholdComparisonIsMadeInSecondsNotInDisplayUnits()
    {
        HoldoverViewModel exceeded = Connected(predicted: 2.0e-6, threshold: 1.0e-6);

        Assert.True(exceeded.IsThresholdExceeded);
        Assert.Equal(Severity.Caution, exceeded.ThresholdSeverity);
        Assert.Equal(("2.0", "µs"), exceeded.Predicted);
        Assert.Equal(("1.000", "µs"), exceeded.Threshold);
    }

    [Fact]
    public void APredictionInsideTheThresholdIsNotFlagged()
    {
        HoldoverViewModel within = Connected(predicted: 0.4e-6, threshold: 1.0e-6);

        Assert.False(within.IsThresholdExceeded);
        Assert.Equal(Severity.Success, within.ThresholdSeverity);
        Assert.Equal("No", within.ThresholdExceededText);
    }

    [Fact]
    public void WithNothingToCompareThereIsNoVerdict()
    {
        HoldoverViewModel unknown = Connected(predicted: null, threshold: null);

        Assert.Null(unknown.IsThresholdExceeded);
        Assert.Equal(Severity.Neutral, unknown.ThresholdSeverity);
        Assert.Equal(ReadoutFormatter.NoValue, unknown.ThresholdExceededText);
    }

    /// <remarks>
    /// §10.3 takes the waiting reason from <c>:SYNC:HOLD:WAIT?</c>, which nothing queries yet. The
    /// status screen's mode detail carries the same sentence, and only while in holdover — a
    /// "Stabilizing frequency" detail from a locked receiver is not a waiting reason.
    /// </remarks>
    [Fact]
    public void TheWaitingReasonOnlyAppearsDuringHoldover()
    {
        Assert.Equal("Not tracking GPS", Connected("WAIT", modeDetail: "Not tracking GPS").WaitingReasonText);
        Assert.Equal(
            ReadoutFormatter.NoValue,
            Connected("LOCK", modeDetail: "Stabilizing frequency").WaitingReasonText);
    }

    /// <remarks>
    /// #4: <c>HoldoverDuration</c> has no known screen label and is unparsed until the fixture that
    /// would settle it is captured. A dash says "not read"; a zero would say "no time has passed",
    /// which is a different and wrong claim.
    /// </remarks>
    [Fact]
    public void AnUnparsedDurationIsADashAndNotAZero() =>
        Assert.Equal(ReadoutFormatter.NoValue, Connected("HOLD").DurationText);

    [Fact]
    public void DisconnectedEmptiesEverything()
    {
        HoldoverViewModel model = Connected();
        model.Connection = ConnectionStatus.Disconnected;

        Assert.Equal(ReceiverMode.Disconnected, model.Mode);
        Assert.False(model.IsInHoldover);
        Assert.Equal("Not connected", model.StateText);
        Assert.Equal(ReadoutFormatter.NoValue, model.Predicted.Value);
        Assert.Null(model.IsThresholdExceeded);
    }
}
