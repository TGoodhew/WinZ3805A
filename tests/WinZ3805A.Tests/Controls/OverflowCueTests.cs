using WinZ3805A.Controls;

namespace WinZ3805A.Tests.Controls;

/// <summary>
/// When the Manage satellites dialog says that it scrolls, and what it says (#767) — the decision,
/// not the rendering.
/// </summary>
/// <remarks>
/// The geometry is the photograph's: 200 % text in the Minimal window, six PRN buttons to a row, and a
/// view that ended exactly on the bottom of the second row, so twelve satellites were in view, twenty
/// were not, and nothing on screen said so.
/// </remarks>
public class OverflowCueTests
{
    private const int Columns = 6;
    private const double RowHeight = 48;
    private const double RowSpacing = 8;
    private const double GridTop = 100;

    /// <summary>The 32 PRN buttons laid out as the dialog lays them out, scrolled by <paramref name="offset"/>.</summary>
    private static List<(double Top, double Height)> Grid(double offset = 0) =>
        Enumerable.Range(0, 32)
            .Select(index => (GridTop + (index / Columns * (RowHeight + RowSpacing)) - offset, RowHeight))
            .ToList();

    /// <summary>A view whose bottom edge is exactly the bottom of row <paramref name="rows"/>.</summary>
    private static double EndingOnRow(int rows) => GridTop + (rows * RowHeight) + ((rows - 1) * RowSpacing);

    [Fact]
    public void AViewEndingExactlyOnARowBoundaryStillSaysThereIsMore()
    {
        double viewport = EndingOnRow(2);

        int inView = OverflowCue.CountInView(Grid(), viewport);
        string? cue = OverflowCue.Describe(OverflowCue.Overflows(extent: 700, viewport), inView, 32);

        Assert.Equal(12, inView);
        Assert.Equal("Showing 12 of 32 satellites. Scroll for more.", cue);
    }

    [Fact]
    public void AHalfVisibleRowIsNotCountedAsInView()
    {
        double viewport = EndingOnRow(2) + RowSpacing + (RowHeight / 2);

        Assert.Equal(12, OverflowCue.CountInView(Grid(), viewport));
    }

    [Fact]
    public void RowsScrolledAboveTheViewAreNotInViewEither()
    {
        double offset = GridTop + RowHeight + RowSpacing;
        double viewport = (2 * RowHeight) + RowSpacing;

        Assert.Equal(12, OverflowCue.CountInView(Grid(offset), viewport));
    }

    [Fact]
    public void ContentThatFitsSaysNothing()
    {
        Assert.False(OverflowCue.Overflows(extent: 600, viewport: 600));
        Assert.Null(OverflowCue.Describe(overflows: false, inView: 32, total: 32));
    }

    /// <summary>
    /// Every satellite in view, but the buttons below are not: the dialog still scrolls, and the
    /// sentence keeps its shape so that its height cannot change the view it is describing.
    /// </summary>
    [Fact]
    public void EverySatelliteInViewStillSaysTheDialogScrolls()
    {
        Assert.Equal(
            "Showing 32 of 32 satellites. Scroll for more.",
            OverflowCue.Describe(overflows: true, inView: 32, total: 32));
    }

    [Fact]
    public void BeforeTheReceiverIsReadItSaysOnlyThatItScrolls()
    {
        Assert.Equal("Scroll for more.", OverflowCue.Describe(overflows: true, inView: 0, total: 0));
    }

    /// <summary>Layout rounding at 150 % scaling leaves an edge a third of a pixel either side.</summary>
    [Theory]
    [InlineData(0.34)]
    [InlineData(-0.34)]
    public void ASubPixelRoundingIsAbsorbed(double error)
    {
        Assert.True(OverflowCue.IsInView(top: error, height: 48, viewport: 48));
        Assert.False(OverflowCue.Overflows(extent: 600 + Math.Abs(error), viewport: 600));
    }

    [Fact]
    public void AWholePixelIsNotAbsorbed()
    {
        Assert.False(OverflowCue.IsInView(top: 1, height: 48, viewport: 48));
        Assert.True(OverflowCue.Overflows(extent: 601, viewport: 600));
    }

    [Fact]
    public void AnItemNotYetLaidOutIsNotInView()
    {
        Assert.False(OverflowCue.IsInView(top: 0, height: 0, viewport: 600));
    }

    [Fact]
    public void TheCountCannotExceedTheTotal()
    {
        Assert.Equal(
            "Showing 32 of 32 satellites. Scroll for more.",
            OverflowCue.Describe(overflows: true, inView: 40, total: 32));
    }
}
