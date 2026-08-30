namespace WinZ3805A.Controls;

/// <summary>
/// The arithmetic behind §9.6.1's content grid, with no WinUI in it.
/// </summary>
/// <remarks>
/// Separated from <c>CardColumns</c> for the reason <c>MedallionRingMath</c> and
/// <c>SkyPlotGeometry</c> are: the test assembly is headless and cannot see a <c>Panel</c>, and the
/// part worth testing is the sums rather than the plumbing. The panel does nothing but call these
/// and hand the answers to Measure and Arrange.
/// </remarks>
public static class CardColumnMath
{
    /// <summary>
    /// How many columns fit in <paramref name="available"/> effective pixels.
    /// </summary>
    /// <remarks>
    /// <b>An unconstrained width yields one column.</b> A panel measured with infinity is being
    /// asked how wide it would like to be, and "as many columns as you will give me" is not an
    /// answer — it would make the page infinitely wide and the ScrollViewer above it would believe
    /// it.
    /// </remarks>
    public static int ColumnsThatFit(double available, double minColumnWidth, double columnSpacing, int maxColumns)
    {
        if (maxColumns < 1)
        {
            return 1;
        }

        if (double.IsNaN(available) || double.IsInfinity(available) || available <= 0 || minColumnWidth <= 0)
        {
            return 1;
        }

        // n columns need n widths and n-1 gaps, so the gap is added to both sides of the division
        // rather than multiplied out: (w + g) / (min + g) counts the "column plus its gap" units
        // and the trailing gap that is not there cancels.
        int fits = (int)Math.Floor((available + columnSpacing) / (minColumnWidth + columnSpacing));

        return Math.Clamp(fits, 1, maxColumns);
    }

    /// <summary>How wide each column is once <paramref name="columns"/> of them share the width.</summary>
    public static double ColumnWidth(double available, int columns, double columnSpacing, double minColumnWidth) =>
        double.IsNaN(available) || double.IsInfinity(available) || available <= 0 || columns < 1
            ? minColumnWidth
            : Math.Max(0, (available - (columnSpacing * (columns - 1))) / columns);

    /// <summary>
    /// Which column the next card goes in: the shortest, ties to the leftmost.
    /// </summary>
    /// <remarks>
    /// <b>Shortest and not round-robin.</b> Round-robin puts the third card under the first however
    /// tall the first is, which leaves one column running off the page beside a stub. Shortest-first
    /// is what makes a tall card sit alone while two short ones stack beside it.
    /// <para>
    /// Ties go left so the first card is always in the first column and the arrangement does not
    /// depend on floating-point noise between two equal heights.
    /// </para>
    /// </remarks>
    public static int ShortestColumn(IReadOnlyList<double> heights)
    {
        ArgumentNullException.ThrowIfNull(heights);

        int best = 0;

        for (int i = 1; i < heights.Count; i++)
        {
            if (heights[i] < heights[best])
            {
                best = i;
            }
        }

        return best;
    }
}
