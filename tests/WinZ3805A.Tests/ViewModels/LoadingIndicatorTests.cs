using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// §9.11's loading ladder: nothing, then a ring, then a skeleton (#320).
/// </summary>
public sealed class LoadingIndicatorTests
{
    /// <summary>
    /// The first threshold is the one that is easy to skip and the one that matters most.
    /// </summary>
    /// <remarks>
    /// §9.11 opens with <i>nothing under 500 ms</i>. A ring bound straight to "is reading" breaks it
    /// on every read that finishes quickly — which is most of them — and what the user sees is a
    /// flash: a spinner appearing and vanishing inside a fifth of a second, which reads as a glitch
    /// rather than as progress. That was the Diagnostics page's behaviour until this was built.
    /// </remarks>
    [Theory]
    [InlineData(0, LoadingIndicator.None)]
    [InlineData(200, LoadingIndicator.None)]
    [InlineData(499, LoadingIndicator.None)]
    [InlineData(500, LoadingIndicator.Ring)]
    [InlineData(1999, LoadingIndicator.Ring)]
    [InlineData(2000, LoadingIndicator.Skeleton)]
    [InlineData(30000, LoadingIndicator.Skeleton)]
    public void TheLadderClimbsAtSection911sThresholds(int milliseconds, LoadingIndicator expected) =>
        Assert.Equal(expected, LoadingIndicators.For(true, TimeSpan.FromMilliseconds(milliseconds)));

    /// <remarks>
    /// A finished read shows nothing whatever its elapsed time. The two facts are separate arguments
    /// precisely so that a stale duration cannot keep a ring spinning over data that has arrived.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(600)]
    [InlineData(5000)]
    public void NothingIsShownWhenNothingIsReading(int milliseconds) =>
        Assert.Equal(
            LoadingIndicator.None,
            LoadingIndicators.For(false, TimeSpan.FromMilliseconds(milliseconds)));

    /// <remarks>
    /// A clock that steps backwards mid-read must not promote the indicator. The elapsed time is a
    /// subtraction of two reads of the same provider, and §7.4's receiver is not the only clock in
    /// this application that can move.
    /// </remarks>
    [Fact]
    public void ANegativeElapsedTimeShowsNothing() =>
        Assert.Equal(LoadingIndicator.None, LoadingIndicators.For(true, TimeSpan.FromSeconds(-3)));

    /// <summary>
    /// The skeleton does not replace the ring.
    /// </summary>
    /// <remarks>
    /// §9.11 lists them as successive states rather than as alternatives: the ring is status —
    /// something is happening — and the skeleton is shape — this much is coming. The page keeps the
    /// ring visible for both, and this asserts the ordering the page relies on to do that.
    /// </remarks>
    [Fact]
    public void TheSkeletonRanksAboveTheRing() =>
        Assert.True(LoadingIndicator.Skeleton > LoadingIndicator.Ring);

    /// <remarks>
    /// Held as values rather than as literals in the pages, for the reason <c>Staleness</c> gives:
    /// "how long is too long" is a judgement the application makes once, and two pages implementing
    /// it from the prose would drift.
    /// </remarks>
    [Fact]
    public void TheThresholdsAreTheOnesSection911States()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), LoadingIndicators.RingThreshold);
        Assert.Equal(TimeSpan.FromSeconds(2), LoadingIndicators.SkeletonThreshold);
    }
}
