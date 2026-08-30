using WinZ3805A.Controls;

namespace WinZ3805A.Tests.Controls;

/// <summary>
/// §9.6.1's content grid: how many columns fit, and which one the next card goes in (#345).
/// </summary>
public sealed class CardColumnMathTests
{
    private const double Min = 420;
    private const double Gap = 24;

    /// <summary>
    /// n columns need n widths and n-1 gaps, so the boundary is not a plain division.
    /// </summary>
    /// <remarks>
    /// Two 420 px columns need 864 px, not 840: the gap between them is real. Getting this wrong by
    /// one gap gives a second column 24 px before there is room for it, and the cards inside it wrap
    /// — which looks like a layout bug rather than an arithmetic one.
    /// </remarks>
    [Theory]
    [InlineData(419, 1)]
    [InlineData(420, 1)]
    [InlineData(700, 1)]
    [InlineData(863, 1)]
    [InlineData(864, 2)]
    [InlineData(1320, 2)]
    [InlineData(4000, 2)]
    public void ColumnsFollowTheWidthAndTheGapsBetweenThem(double available, int expected) =>
        Assert.Equal(expected, CardColumnMath.ColumnsThatFit(available, Min, Gap, maxColumns: 2));

    /// <remarks>
    /// §9.6 caps the content region because label-value pairs separated by a hand's width are
    /// measurably worse to scan. A monitor 5120 px wide must not produce eleven columns.
    /// </remarks>
    [Fact]
    public void TheCapHoldsHoweverWideTheWindowGets()
    {
        Assert.Equal(2, CardColumnMath.ColumnsThatFit(5120, Min, Gap, maxColumns: 2));
        Assert.Equal(3, CardColumnMath.ColumnsThatFit(5120, Min, Gap, maxColumns: 3));
    }

    /// <summary>
    /// An unconstrained width yields one column, and that is not a rounding decision.
    /// </summary>
    /// <remarks>
    /// A panel measured with infinity is being asked how wide it would like to be. "As many columns
    /// as you will give me" is not an answer — it makes the page infinitely wide, and the
    /// <c>ScrollViewer</c> above it believes the answer.
    /// </remarks>
    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    [InlineData(0)]
    [InlineData(-100)]
    public void AnUnusableWidthYieldsOneColumn(double available) =>
        Assert.Equal(1, CardColumnMath.ColumnsThatFit(available, Min, Gap, maxColumns: 2));

    /// <remarks>
    /// Defensive, because a caller that passes zero or fewer is asking for a division by zero one
    /// line later rather than for no columns.
    /// </remarks>
    [Fact]
    public void ThereIsAlwaysAtLeastOneColumn()
    {
        Assert.Equal(1, CardColumnMath.ColumnsThatFit(2000, Min, Gap, maxColumns: 0));
        Assert.Equal(1, CardColumnMath.ColumnsThatFit(2000, minColumnWidth: 0, Gap, maxColumns: 2));
    }

    /// <remarks>
    /// The gaps come out of the width before it is shared, or the columns overflow the panel by
    /// exactly the space between them.
    /// </remarks>
    [Fact]
    public void TheGapsAreTakenBeforeTheWidthIsShared()
    {
        Assert.Equal(1000, CardColumnMath.ColumnWidth(1000, 1, Gap, Min));
        Assert.Equal(488, CardColumnMath.ColumnWidth(1000, 2, Gap, Min));
        Assert.Equal((1000 - (2 * Gap)) / 3, CardColumnMath.ColumnWidth(1000, 3, Gap, Min));
    }

    /// <summary>
    /// The heart of the arrangement: shortest column wins, so a tall card does not drag a whole
    /// column down with it.
    /// </summary>
    /// <remarks>
    /// This is what Tony asked for and what round-robin does not give. With a tall first card,
    /// round-robin puts the third card underneath it whatever its height; shortest-first puts the
    /// second and third beside it.
    /// </remarks>
    [Fact]
    public void CardsGoToTheShortestColumn()
    {
        double[] heights = [0, 0];

        // Card 1 is tall and lands left.
        int first = CardColumnMath.ShortestColumn(heights);
        heights[first] += 400;

        // Card 2 goes right, because left is now 400 tall.
        int second = CardColumnMath.ShortestColumn(heights);
        heights[second] += 120;

        // Card 3 goes right again — 120 is still shorter than 400.
        int third = CardColumnMath.ShortestColumn(heights);

        Assert.Equal(0, first);
        Assert.Equal(1, second);
        Assert.Equal(1, third);
    }

    /// <remarks>
    /// Ties go left, so the first card is always in the first column and the arrangement does not
    /// flip on floating-point noise between two equal heights.
    /// </remarks>
    [Fact]
    public void TiesGoToTheLeftmostColumn()
    {
        Assert.Equal(0, CardColumnMath.ShortestColumn([0, 0, 0]));
        Assert.Equal(0, CardColumnMath.ShortestColumn([250, 250]));
        Assert.Equal(2, CardColumnMath.ShortestColumn([250, 250, 100]));
    }

    /// <remarks>
    /// One column is the Compact case and has to keep working: every card stacks, in order.
    /// </remarks>
    [Fact]
    public void OneColumnTakesEveryCard()
    {
        double[] heights = [0];

        Assert.Equal(0, CardColumnMath.ShortestColumn(heights));
        heights[0] += 999;
        Assert.Equal(0, CardColumnMath.ShortestColumn(heights));
    }
}
