using WinZ3805A.Controls;

namespace WinZ3805A.Tests.Controls;

/// <summary>
/// The arithmetic that centres a readout's label and caption on its decimal axis and keeps them
/// inside the tile (#716, #753).
/// </summary>
/// <remarks>
/// The widths are round numbers rather than measured glyphs: what is being checked is where a text
/// lands relative to the tile's edges and the axis, which does not depend on the font.
/// </remarks>
public sealed class ReadoutAxisMathTests
{
    [Fact]
    public void WithADecimalPointTheAxisIsTheMiddleOfThePoint() =>
        Assert.Equal(108, ReadoutAxisMath.Axis(reserveWidth: 100, pointWidth: 16, reserveCharacters: 5));

    /// <remarks>
    /// "satellites": a two-digit reserve whose usual reading is one digit, right-aligned into its
    /// right half. Centring on the reserve's middle put the label half a digit left of the number.
    /// </remarks>
    [Fact]
    public void WithoutAPointTheAxisIsTheMiddleOfTheOnesDigit() =>
        Assert.Equal(51, ReadoutAxisMath.Axis(reserveWidth: 68, pointWidth: 0, reserveCharacters: 2));

    [Fact]
    public void TextsThatFitBesideTheAxisNeedNoInsetAndNoExtraWidth()
    {
        // 1 PPS TI at 100 %: a narrow label well inside a wide value.
        double axis = ReadoutAxisMath.Axis(114, 8, 6);

        double inset = ReadoutAxisMath.Inset(axis, 45, 0);

        Assert.Equal(0, inset);
        Assert.Equal(155, ReadoutAxisMath.Width(155, axis, inset, 45, 0));
    }

    /// <remarks>
    /// The Overview's "relative to GPS" at 200 % text (#753): centred on an axis right of the value's
    /// middle, the caption reached past the tile's right edge and into the card's padding. The tile
    /// now asks for that width, so its parent keeps the padding.
    /// </remarks>
    [Fact]
    public void ACaptionReachingPastTheRightEdgeWidensTheTileToTheRight()
    {
        double axis = 118;
        double caption = 170;

        double inset = ReadoutAxisMath.Inset(axis, 45, caption);
        double width = ReadoutAxisMath.Width(155, axis, inset, 45, caption);

        Assert.Equal(0, inset);
        Assert.Equal(axis + (caption / 2), width);
        Assert.Equal(axis - (caption / 2), CaptionLeft(axis, caption, width));
        Assert.Equal(width, CaptionLeft(axis, caption, width) + caption);
    }

    /// <remarks>
    /// The main window's "satellites" at 200 % text (#753): a label wider than twice the room left of
    /// the axis moved towards the window's edge. The value starts further right instead, by exactly
    /// the shortfall, so the label's left edge is the tile's.
    /// </remarks>
    [Fact]
    public void ALabelReachingPastTheLeftEdgeInsetsTheValueByTheShortfall()
    {
        double axis = ReadoutAxisMath.Axis(68, 0, 2);
        double label = 110;

        double inset = ReadoutAxisMath.Inset(axis, label, 0);
        double width = ReadoutAxisMath.Width(68, axis, inset, label, 0);

        Assert.Equal((label / 2) - axis, inset);
        Assert.Equal(label, width);

        double offset = ReadoutAxisMath.TextOffset(inset + axis, label, width);
        Assert.Equal(0, offset);
    }

    [Fact]
    public void TheWidestTextDecidesTheInset() =>
        Assert.Equal(20, ReadoutAxisMath.Inset(50, 60, 140, 90));

    /// <remarks>#716's centring is unchanged wherever the text fits, overhang or not.</remarks>
    [Theory]
    [InlineData(118, 45, 160)]
    [InlineData(51, 40, 120)]
    public void ATextThatFitsIsCentredExactlyOnTheAxis(double axis, double text, double tile)
    {
        double offset = ReadoutAxisMath.TextOffset(axis, text, tile);

        double centre = ((tile - text) / 2) + offset + (text / 2);

        Assert.Equal(axis, centre, 9);
    }

    /// <remarks>
    /// When the parent cannot give the tile the width it asked for, the text is clamped inside it
    /// rather than spilling - centred on the axis only as far as it fits.
    /// </remarks>
    [Theory]
    [InlineData(150, 80, 160, 80)] // would end at 190; clamped to end at the right edge
    [InlineData(10, 80, 160, 0)]   // would start at -30; clamped to start at the left edge
    public void ATextThatWouldCrossAnEdgeIsClampedToIt(double axis, double text, double tile, double expectedLeft)
    {
        double left = ((tile - text) / 2) + ReadoutAxisMath.TextOffset(axis, text, tile);

        Assert.Equal(expectedLeft, left, 9);
        Assert.InRange(left, 0, tile - text);
    }

    [Theory]
    [InlineData(160)]
    [InlineData(200)]
    public void ATextAsWideAsTheTileIsLeftWhereCentringPutsIt(double text) =>
        Assert.Equal(0, ReadoutAxisMath.TextOffset(10, text, 160));

    [Theory]
    [InlineData(20, 100, 200, 20)]
    [InlineData(20, 100, 110, 10)] // the value is never pushed past the right edge
    [InlineData(20, 100, 90, 0)]
    public void TheValueStartsAtTheInsetUnlessThatWouldPushItOut(double inset, double value, double tile, double expected) =>
        Assert.Equal(expected, ReadoutAxisMath.ValueOffset(inset, value, tile));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-5)]
    public void AWidthThatIsNotAWidthMovesNothing(double bad)
    {
        Assert.Equal(0, ReadoutAxisMath.TextOffset(bad, bad, bad));
        Assert.Equal(0, ReadoutAxisMath.Inset(bad, bad));
        Assert.True(double.IsFinite(ReadoutAxisMath.Width(bad, bad, bad, bad)));
    }

    private static double CaptionLeft(double axis, double caption, double tile) =>
        ((tile - caption) / 2) + ReadoutAxisMath.TextOffset(axis, caption, tile);
}
