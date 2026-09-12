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
/// <b>The cursor is a satellite, not a position.</b> It used to be an index into the plotted list,
/// which is rebuilt and re-sorted on every reading: a satellite appearing earlier in the order
/// shifted every index above it by one, so the ring moved to a different satellite with nothing
/// said, and Enter then selected one the user had never been on. Keying on the satellite makes the
/// common case exact — while the one you are on is still plotted, the ring stays on it however the
/// list resorts.
/// </para>
/// <para>
/// <b>A satellite that is no longer plotted keeps the cursor.</b> It draws no ring, because it has
/// no position, and Enter does nothing, because the one thing worse than selecting nothing is
/// selecting a satellite the user was never on. The place is kept rather than reset: satellites at
/// the mask edge drop out and return within a reading or two, and <see cref="Step"/> resumes from
/// where the missing one <i>would</i> sit in the order, so a flicker costs the user nothing.
/// </para>
/// <para>
/// <b>Generic over the key, because the key changed and the behaviour did not (#424).</b> It was a
/// PRN, and a PRN stopped being an identity the moment a receiver that numbers per constellation
/// arrived; the control now keys on <c>SatelliteId</c>. Leaving these methods generic keeps the
/// tests that pin the ordering and the gap-resumption running against plain ints, so the widening
/// is demonstrably a change of key and not a change of behaviour.
/// </para>
/// </remarks>
public static class SkyPlotCursor
{
    /// <summary>
    /// The satellite the cursor lands on after moving <paramref name="delta"/> places.
    /// </summary>
    /// <typeparam name="T">The key a satellite is identified by, in the caller's order.</typeparam>
    /// <param name="keys">
    /// The plotted satellites' keys in ascending order — §9.10.2's keyboard order, and what
    /// <c>SkyPlotControl</c> draws.
    /// </param>
    /// <param name="cursor">Where the cursor is now, or <see langword="null"/> before it has moved.</param>
    /// <param name="delta">Places to move. Positive is forward through the order; +1 and -1 are what the arrows send.</param>
    /// <returns>The key to sit on, or <see langword="null"/> when nothing is plotted.</returns>
    public static T? Step<T>(IReadOnlyList<T> keys, T? cursor, int delta)
        where T : struct, IEquatable<T>, IComparable<T>
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Count == 0)
        {
            return null;
        }

        int index = IndexOf(keys, cursor);

        // A cursor on a satellite that is not plotted — because it was never set, or because that
        // satellite has gone — moves relative to the gap it would occupy rather than relative to
        // nothing. Forward lands on the first key above it and back on the last below it, which is
        // what "carry on from where I was" means when where you were is empty.
        int next = index >= 0
            ? index + delta
            : Rank(keys, cursor) + delta - (delta > 0 ? 1 : 0);

        return keys[((next % keys.Count) + keys.Count) % keys.Count];
    }

    /// <summary>The position of <paramref name="cursor"/> among <paramref name="keys"/>, or -1.</summary>
    /// <typeparam name="T">The key a satellite is identified by.</typeparam>
    /// <param name="keys">The plotted satellites' keys, in the caller's order.</param>
    /// <param name="cursor">Where the cursor is now, or <see langword="null"/>.</param>
    /// <remarks>
    /// A linear scan rather than a binary search on purpose: the plot carries a dozen markers, and
    /// a binary search would silently depend on the ordering the caller is merely documented to
    /// supply.
    /// </remarks>
    public static int IndexOf<T>(IReadOnlyList<T> keys, T? cursor)
        where T : struct, IEquatable<T>
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (cursor is not T key)
        {
            return -1;
        }

        for (int i = 0; i < keys.Count; i++)
        {
            if (keys[i].Equals(key))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>How many plotted keys sort below <paramref name="cursor"/>.</summary>
    /// <remarks>
    /// The insertion point for a satellite that is not there. Zero for an unset cursor, which is
    /// what makes "never moved" and "was on a satellite below every plotted one" behave identically
    /// — forward to the first, back to the last.
    /// </remarks>
    private static int Rank<T>(IReadOnlyList<T> keys, T? cursor)
        where T : struct, IComparable<T>
    {
        if (cursor is not T key)
        {
            return 0;
        }

        int rank = 0;

        for (int i = 0; i < keys.Count; i++)
        {
            if (keys[i].CompareTo(key) < 0)
            {
                rank++;
            }
        }

        return rank;
    }
}
