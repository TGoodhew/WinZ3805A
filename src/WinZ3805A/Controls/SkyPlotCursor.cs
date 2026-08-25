namespace WinZ3805A.Controls;

/// <summary>
/// Where §9.10.2's arrow keys land, over a constellation that changes underneath them.
/// </summary>
/// <remarks>
/// <para>
/// Separated from the control for the same reason <see cref="SkyPlotGeometry"/> is: it is a half
/// that can be wrong silently. §9.10.2 says only "arrow keys move a ring through markers in PRN
/// order, Enter selects", which is complete right up until a satellite is acquired or lost — and
/// the plot redraws on the poll cadence, so that happens while the user is sitting on a marker
/// rather than at a moment they chose.
/// </para>
/// <para>
/// <b>The cursor is a PRN, not a position.</b> It used to be an index into the plotted list, which
/// is rebuilt and re-sorted on every reading: a satellite appearing with a lower PRN shifted every
/// index above it by one, so the ring moved to a different satellite with nothing said, and Enter
/// then selected one the user had never been on. Keying on the PRN makes the common case exact —
/// while the satellite you are on is still plotted, the ring stays on it however the list resorts.
/// </para>
/// <para>
/// <b>A PRN that is no longer plotted keeps the cursor.</b> It draws no ring, because it has no
/// position, and Enter does nothing, because the one thing worse than selecting nothing is
/// selecting a satellite the user was never on. The place is kept rather than reset: satellites at
/// the mask edge drop out and return within a reading or two, and <see cref="Step"/> resumes from
/// where the missing PRN <i>would</i> sit in the order, so a flicker costs the user nothing.
/// </para>
/// </remarks>
public static class SkyPlotCursor
{
    /// <summary>
    /// The PRN the cursor lands on after moving <paramref name="delta"/> places.
    /// </summary>
    /// <param name="prns">
    /// The plotted PRNs in ascending order — §9.10.2's keyboard order, and what
    /// <c>SkyPlotControl</c> draws.
    /// </param>
    /// <param name="cursorPrn">Where the cursor is now, or <see langword="null"/> before it has moved.</param>
    /// <param name="delta">Places to move. Positive is forward through the order; +1 and -1 are what the arrows send.</param>
    /// <returns>The PRN to sit on, or <see langword="null"/> when nothing is plotted.</returns>
    public static int? Step(IReadOnlyList<int> prns, int? cursorPrn, int delta)
    {
        ArgumentNullException.ThrowIfNull(prns);

        if (prns.Count == 0)
        {
            return null;
        }

        int index = IndexOf(prns, cursorPrn);

        // A cursor on a PRN that is not plotted — because it was never set, or because that
        // satellite has gone — moves relative to the gap the PRN would occupy rather than
        // relative to nothing. Forward lands on the first PRN above it and back on the last
        // below it, which is what "carry on from where I was" means when where you were is empty.
        int next = index >= 0
            ? index + delta
            : Rank(prns, cursorPrn) + delta - (delta > 0 ? 1 : 0);

        return prns[((next % prns.Count) + prns.Count) % prns.Count];
    }

    /// <summary>The position of <paramref name="cursorPrn"/> among <paramref name="prns"/>, or -1.</summary>
    /// <remarks>
    /// A linear scan rather than a binary search on purpose: the plot carries a dozen markers, and
    /// a binary search would silently depend on the ordering the caller is merely documented to
    /// supply.
    /// </remarks>
    public static int IndexOf(IReadOnlyList<int> prns, int? cursorPrn)
    {
        ArgumentNullException.ThrowIfNull(prns);

        if (cursorPrn is not int prn)
        {
            return -1;
        }

        for (int i = 0; i < prns.Count; i++)
        {
            if (prns[i] == prn)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>How many plotted PRNs sort below <paramref name="cursorPrn"/>.</summary>
    /// <remarks>
    /// The insertion point for a PRN that is not there. Zero for an unset cursor, which is what
    /// makes "never moved" and "was on a PRN below every plotted one" behave identically — forward
    /// to the first, back to the last.
    /// </remarks>
    private static int Rank(IReadOnlyList<int> prns, int? cursorPrn)
    {
        if (cursorPrn is not int prn)
        {
            return 0;
        }

        int rank = 0;

        for (int i = 0; i < prns.Count; i++)
        {
            if (prns[i] < prn)
            {
                rank++;
            }
        }

        return rank;
    }
}
