using WinZ3805A.Controls;

namespace WinZ3805A.Tests.Controls;

/// <summary>
/// The wrapping behind the Satellites card's header and the sky-plot legend (#664).
/// </summary>
public sealed class FlowMathTests
{
    private const double Gap = 12;
    private const double RowGap = 4;

    [Fact]
    public void EverythingThatFitsSharesOneRowWithTheGapBetweenNeighbours()
    {
        FlowLayout layout = FlowMath.Arrange([(50, 20), (60, 20), (70, 20)], 400, Gap, RowGap);

        Assert.Equal([0.0, 62.0, 134.0], layout.Placements.Select(p => p.Left));
        Assert.All(layout.Placements, p => Assert.Equal(0, p.Row));
        Assert.Equal(204, layout.Width);
        Assert.Equal(20, layout.Height);
    }

    /// <remarks>
    /// The boundary is the item's right edge against the width, not its left edge, and an item that
    /// lands exactly on it fits. The gap after the last item in a row is never counted.
    /// </remarks>
    [Theory]
    [InlineData(132, 0)]
    [InlineData(131, 1)]
    public void AChildWrapsOnlyWhenItsRightEdgeWouldCrossTheWidth(double available, int expectedRow) =>
        Assert.Equal(expectedRow, FlowMath.Arrange([(60, 20), (60, 20)], available, Gap, RowGap).Placements[1].Row);

    [Fact]
    public void AWrappedRowStartsAtTheLeftBelowTheTallestChildOfTheRowAbove()
    {
        FlowLayout layout = FlowMath.Arrange([(80, 20), (80, 32), (80, 20)], 200, Gap, RowGap);

        Assert.Equal(new FlowPlacement(0, 36, 1), layout.Placements[2]);
        Assert.Equal(56, layout.Height);
    }

    /// <remarks>
    /// The original header grid centred the heading against the taller radio buttons; a row does the
    /// same, so the heading's baseline does not jump to the top when the header is replaced.
    /// </remarks>
    [Fact]
    public void AShorterChildIsCentredInItsRow() =>
        Assert.Equal(6, FlowMath.Arrange([(80, 20), (80, 32)], 400, Gap, RowGap).Placements[0].Top);

    /// <remarks>
    /// Wrapping a child that cannot fit anywhere would only leave an empty row above it.
    /// </remarks>
    [Fact]
    public void AChildWiderThanThePanelStartsItsOwnRowAndIsNotWrappedAgain()
    {
        FlowLayout layout = FlowMath.Arrange([(500, 20), (40, 20)], 300, Gap, RowGap);

        Assert.Equal(new FlowPlacement(0, 0, 0), layout.Placements[0]);
        Assert.Equal(new FlowPlacement(0, 24, 1), layout.Placements[1]);
    }

    /// <remarks>
    /// The card header's arrangement while there is room: heading at the left, actions at the right.
    /// </remarks>
    [Fact]
    public void ThePushedChildSitsAtTheRightEdgeWhileItSharesARow()
    {
        FlowLayout layout = FlowMath.Arrange([(90, 20), (300, 32)], 600, Gap, RowGap, pushLastToEnd: true);

        Assert.Equal(300, layout.Placements[1].Left);
        Assert.Equal(600, layout.Width);
    }

    /// <remarks>
    /// #664: when the actions no longer fit beside the heading, they go under it rather than over it.
    /// </remarks>
    [Fact]
    public void ThePushedChildGoesUnderTheHeadingAtTheLeftOnceItWraps()
    {
        FlowLayout layout = FlowMath.Arrange([(90, 20), (300, 32)], 350, Gap, RowGap, pushLastToEnd: true);

        Assert.Equal(new FlowPlacement(0, 24, 1), layout.Placements[1]);
        Assert.Equal(56, layout.Height);
    }

    /// <remarks>
    /// A panel measured with infinity is asked how wide it would like to be; there is no right edge
    /// to push to, and no reason to wrap.
    /// </remarks>
    [Fact]
    public void AnUnboundedWidthIsOneRowAndPushesNothing()
    {
        FlowLayout layout = FlowMath.Arrange([(90, 20), (300, 32)], double.PositiveInfinity, Gap, RowGap, pushLastToEnd: true);

        Assert.Equal(102, layout.Placements[1].Left);
        Assert.Equal(402, layout.Width);
        Assert.Equal(32, layout.Height);
    }

    [Fact]
    public void NoChildrenTakeNoSpace()
    {
        FlowLayout layout = FlowMath.Arrange([], 400, Gap, RowGap, pushLastToEnd: true);

        Assert.Empty(layout.Placements);
        Assert.Equal((0.0, 0.0), (layout.Width, layout.Height));
    }
}
