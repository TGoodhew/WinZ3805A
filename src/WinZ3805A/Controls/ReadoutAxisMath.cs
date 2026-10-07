namespace WinZ3805A.Controls;

/// <summary>
/// The arithmetic that hangs a readout's label and caption from its decimal axis while keeping both
/// inside the tile, with no WinUI in it.
/// </summary>
/// <remarks>
/// <para>
/// Separated from <c>ReadoutTile</c> for the reason <c>FlowMath</c> is: the test assembly is headless
/// and cannot see a <c>Control</c>, and the part worth testing is the sums rather than the plumbing.
/// <c>ReadoutTile</c> is named in plain text rather than with a cref because this file is also
/// compiled into the test assembly, where that type does not exist.
/// </para>
/// <para>
/// <b>Why the tile grows rather than letting the text overhang (#753).</b> The label and caption are
/// centred on the axis, and the axis is not the middle of the tile. A text wider than the space on
/// either side of the axis therefore reached past the tile's edge, and nothing outside the tile knows
/// it is there: at 200 % text the Overview's "relative to GPS" ate the card's right padding, and the
/// main window's "satellites" moved towards the window's left edge. So the tile asks for the width the
/// centred text needs (<see cref="Inset"/>, <see cref="Width"/>), which its parent then honours like
/// any other width; and when the parent cannot give that much, the text is clamped to the tile instead
/// (<see cref="TextOffset"/>) and is centred on the axis only where it fits.
/// </para>
/// <para>
/// All widths are in effective pixels, measured from the tile's left edge. The value's own position
/// depends only on the reserve and the texts, never on the reading, so the decimal point still does
/// not move as digits come and go (§9.5.3 rule 5).
/// </para>
/// </remarks>
public static class ReadoutAxisMath
{
    /// <summary>
    /// Where the axis is, measured from the left edge of the value: the middle of the decimal point,
    /// or the middle of the ones digit when there is no point.
    /// </summary>
    /// <param name="reserveWidth">The width of the invisible reserve for the integer side.</param>
    /// <param name="pointWidth">The width of the decimal separator; zero when none is drawn.</param>
    /// <param name="reserveCharacters">How many characters the reserve holds.</param>
    /// <remarks>
    /// Without a point the ones digit is the column the eye returns to and the one right alignment
    /// holds still. Tabular figures make a character's width exactly the reserve's width over its
    /// length (§9.5.3 rule 1).
    /// </remarks>
    public static double Axis(double reserveWidth, double pointWidth, int reserveCharacters)
    {
        reserveWidth = Finite(reserveWidth);
        pointWidth = Finite(pointWidth);

        if (pointWidth > 0)
        {
            return reserveWidth + (pointWidth / 2);
        }

        double character = reserveCharacters > 0 ? reserveWidth / reserveCharacters : 0;

        return reserveWidth - (character / 2);
    }

    /// <summary>
    /// How far right the value must start for every text centred on the axis to clear the tile's
    /// left edge; zero when they all do already.
    /// </summary>
    /// <param name="axis">The axis, from the value's left edge (<see cref="Axis"/>).</param>
    /// <param name="textWidths">The label's and the caption's widths; zero for an absent one.</param>
    public static double Inset(double axis, params ReadOnlySpan<double> textWidths)
    {
        axis = Finite(axis);
        double inset = 0;

        foreach (double width in textWidths)
        {
            inset = Math.Max(inset, (Finite(width) / 2) - axis);
        }

        return inset;
    }

    /// <summary>
    /// The width the tile needs to hold the value, inset by <paramref name="inset"/>, and every text
    /// centred on the axis.
    /// </summary>
    /// <param name="valueWidth">The value's own width: reserve, point, fraction and unit.</param>
    /// <param name="axis">The axis, from the value's left edge (<see cref="Axis"/>).</param>
    /// <param name="inset">Where the value starts (<see cref="Inset"/>).</param>
    /// <param name="textWidths">The label's and the caption's widths; zero for an absent one.</param>
    public static double Width(double valueWidth, double axis, double inset, params ReadOnlySpan<double> textWidths)
    {
        axis = Finite(axis);
        inset = Finite(inset);
        double width = inset + Finite(valueWidth);

        foreach (double text in textWidths)
        {
            double half = Finite(text) / 2;
            width = Math.Max(width, Math.Max(Finite(text), inset + axis + half));
        }

        return width;
    }

    /// <summary>
    /// Where the value starts in a tile of <paramref name="tileWidth"/>: at <paramref name="inset"/>,
    /// unless that would push the value past the tile's right edge.
    /// </summary>
    public static double ValueOffset(double inset, double valueWidth, double tileWidth) =>
        Math.Clamp(Finite(inset), 0, Math.Max(0, Finite(tileWidth) - Finite(valueWidth)));

    /// <summary>
    /// The horizontal shift that moves a text, laid out centred in the tile, onto the axis - or as
    /// near it as the tile allows without the text crossing either edge.
    /// </summary>
    /// <param name="axis">The axis, from the tile's left edge (value offset plus <see cref="Axis"/>).</param>
    /// <param name="textWidth">The text's width.</param>
    /// <param name="tileWidth">The tile's width, inside which the text is centred before the shift.</param>
    /// <returns>
    /// Zero for a text at least as wide as the tile, which no shift can fit and centring already
    /// treats evenly.
    /// </returns>
    public static double TextOffset(double axis, double textWidth, double tileWidth)
    {
        axis = Finite(axis);
        textWidth = Finite(textWidth);
        tileWidth = Finite(tileWidth);

        double room = tileWidth - textWidth;
        if (room <= 0)
        {
            return 0;
        }

        double wanted = axis - (textWidth / 2);

        return Math.Clamp(wanted, 0, room) - (room / 2);
    }

    /// <summary>A width that is not a number, or negative, counts as nothing.</summary>
    private static double Finite(double value) =>
        double.IsFinite(value) && value > 0 ? value : 0;
}
