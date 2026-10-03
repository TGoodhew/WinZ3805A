namespace WinZ3805A.Controls;

/// <summary>Where one child of a <c>FlowPanel</c> goes, in effective pixels from the panel's corner.</summary>
public readonly record struct FlowPlacement(double Left, double Top, int Row);

/// <summary>A whole <c>FlowPanel</c> arrangement: every child's place and the size they take together.</summary>
public sealed record FlowLayout(IReadOnlyList<FlowPlacement> Placements, double Width, double Height);

/// <summary>
/// The arithmetic behind <c>FlowPanel</c>, with no WinUI in it.
/// </summary>
/// <remarks>
/// Separated from the panel for the reason <c>CardColumnMath</c> is: the test assembly is headless
/// and cannot see a <c>Panel</c>, and the part worth testing is the sums rather than the plumbing.
/// </remarks>
public static class FlowMath
{
    /// <summary>
    /// Layout rounding can leave a row a hair short of an item that does fit; that is not a reason to
    /// wrap it.
    /// </summary>
    private const double Tolerance = 0.01;

    /// <summary>
    /// Lays the children out left to right, starting a new row whenever the next one would cross
    /// <paramref name="available"/>, and centres each child vertically in its row.
    /// </summary>
    /// <param name="sizes">Each visible child's desired width and height, in order.</param>
    /// <param name="available">The width to wrap at. Infinity means one row.</param>
    /// <param name="columnSpacing">The gap between neighbours in a row.</param>
    /// <param name="rowSpacing">The gap between rows.</param>
    /// <param name="pushLastToEnd">
    /// Put the last child flush with the right edge when it shares a row with another child, the way
    /// a two-column <c>*</c>/<c>Auto</c> grid puts a card's actions opposite its heading. A last child
    /// that has wrapped onto a row of its own stays at the left, under what it was beside (#664).
    /// </param>
    /// <remarks>
    /// <b>A child wider than the panel still starts a row of its own at the left and is not wrapped
    /// again.</b> Moving it down a row would leave an empty row above it and fit no better.
    /// </remarks>
    public static FlowLayout Arrange(
        IReadOnlyList<(double Width, double Height)> sizes,
        double available,
        double columnSpacing,
        double rowSpacing,
        bool pushLastToEnd = false)
    {
        ArgumentNullException.ThrowIfNull(sizes);

        if (sizes.Count == 0)
        {
            return new FlowLayout([], 0, 0);
        }

        bool bounded = !double.IsNaN(available) && !double.IsInfinity(available);

        var lefts = new double[sizes.Count];
        var rows = new int[sizes.Count];
        var rowHeights = new List<double>();
        var rowCounts = new List<int>();

        double x = 0;
        double widest = 0;

        for (int i = 0; i < sizes.Count; i++)
        {
            (double width, double height) = sizes[i];

            if (rowHeights.Count == 0 || (x > 0 && bounded && x + width > available + Tolerance))
            {
                rowHeights.Add(0);
                rowCounts.Add(0);
                x = 0;
            }

            int row = rowHeights.Count - 1;
            lefts[i] = x;
            rows[i] = row;
            rowHeights[row] = Math.Max(rowHeights[row], height);
            rowCounts[row]++;

            widest = Math.Max(widest, x + width);
            x += width + columnSpacing;
        }

        var tops = new double[rowHeights.Count];
        for (int row = 1; row < rowHeights.Count; row++)
        {
            tops[row] = tops[row - 1] + rowHeights[row - 1] + rowSpacing;
        }

        int last = sizes.Count - 1;
        bool pushed = pushLastToEnd && bounded && rowCounts[rows[last]] > 1;
        if (pushed)
        {
            lefts[last] = Math.Max(lefts[last], available - sizes[last].Width);
        }

        var placements = new FlowPlacement[sizes.Count];
        for (int i = 0; i < sizes.Count; i++)
        {
            int row = rows[i];
            placements[i] = new FlowPlacement(lefts[i], tops[row] + ((rowHeights[row] - sizes[i].Height) / 2), row);
        }

        return new FlowLayout(placements, pushed ? Math.Max(widest, available) : widest, tops[^1] + rowHeights[^1]);
    }
}
