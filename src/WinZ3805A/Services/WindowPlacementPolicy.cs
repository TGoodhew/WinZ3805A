namespace WinZ3805A.Services;

/// <summary>
/// Decides what a stored <see cref="WindowPlacement"/> means on the displays attached
/// <i>this</i> launch.
/// </summary>
/// <remarks>
/// <para>
/// §10.3 asks only that size, position and compact state persist, which on its own is two lines in
/// the window. The reason this is a separate, tested class is the case the two lines get wrong: the
/// app is meant to be left open on a second monitor for weeks (§1), so the monitor it was left on
/// is precisely the one likely to be missing, moved or re-scaled by the next launch. Restoring the
/// saved rectangle verbatim then opens the window somewhere the user cannot see or reach it, and
/// the only remedy is to know where the file is.
/// </para>
/// <para>
/// Pure functions over plain rectangles, so the display topologies that matter — one monitor, a
/// monitor removed, a monitor to the left at negative coordinates, a resolution reduced under the
/// window — are all reachable from a headless test rather than from a bench with three cables.
/// </para>
/// </remarks>
public static class WindowPlacementPolicy
{
    /// <summary>
    /// How much of the window has to land on a display for the placement to be worth restoring.
    /// </summary>
    /// <remarks>
    /// Roughly the title bar's draggable extent. A window with less than this showing cannot be
    /// grabbed and moved back, which makes it indistinguishable from one that never opened.
    /// </remarks>
    public const int ReachableWidth = 120;

    /// <inheritdoc cref="ReachableWidth" />
    public const int ReachableHeight = 32;

    /// <summary>
    /// Returns the placement to apply, or <see langword="null"/> to let the system place the window.
    /// </summary>
    /// <param name="saved">What was stored, or <see langword="null"/> on a first run.</param>
    /// <param name="workAreas">
    /// The work area of every attached display — the desktop minus the taskbar. Assumed disjoint,
    /// which is how Windows arranges displays; the coverage sum below relies on it.
    /// </param>
    /// <param name="minimumWidth">The §10.3 floor for the layout being restored.</param>
    /// <param name="minimumHeight">The §10.3 floor for the layout being restored.</param>
    public static WindowPlacement? Restore(
        WindowPlacement? saved,
        IReadOnlyList<WindowRect> workAreas,
        int minimumWidth,
        int minimumHeight)
    {
        ArgumentNullException.ThrowIfNull(workAreas);

        if (saved is null || workAreas.Count == 0)
        {
            return null;
        }

        WindowRect wanted = saved.Bounds with
        {
            Width = Math.Max(saved.Width, minimumWidth),
            Height = Math.Max(saved.Height, minimumHeight),
        };

        // Disjoint work areas, so the overlaps sum to exactly the visible portion. A window spanning
        // two adjacent displays is fully visible and deliberate, and must not be pulled onto one of
        // them — which is why this is a coverage sum rather than a "fits inside one display" test.
        long covered = 0;
        WindowRect best = default;
        WindowRect bestOverlap = default;

        foreach (WindowRect area in workAreas)
        {
            WindowRect overlap = wanted.Intersect(area);
            covered += overlap.Area;

            if (overlap.Area > bestOverlap.Area)
            {
                bestOverlap = overlap;
                best = area;
            }
        }

        if (covered == wanted.Area)
        {
            return saved.WithBounds(wanted);
        }

        if (bestOverlap.Width < ReachableWidth || bestOverlap.Height < ReachableHeight)
        {
            // The display it was on is gone, or has shrunk out from under it. Nothing here is worth
            // guessing from: let the system open the window where it would open a new one.
            return null;
        }

        return saved.WithBounds(ClampInto(wanted, best, minimumWidth, minimumHeight));
    }

    /// <summary>
    /// Pulls <paramref name="window"/> wholly inside <paramref name="area"/>, shrinking it first if
    /// it does not fit, but never below the §10.3 minimum.
    /// </summary>
    private static WindowRect ClampInto(WindowRect window, WindowRect area, int minimumWidth, int minimumHeight)
    {
        int width = Math.Clamp(window.Width, minimumWidth, Math.Max(minimumWidth, area.Width));
        int height = Math.Clamp(window.Height, minimumHeight, Math.Max(minimumHeight, area.Height));

        // Math.Clamp throws if the maximum is below the minimum, which is the case for a window
        // larger than the display it is being pulled onto. That window is left at the work area's
        // origin, overhanging bottom-right, exactly as opening it too large would.
        int left = Math.Min(Math.Max(window.Left, area.Left), Math.Max(area.Left, area.Right - width));
        int top = Math.Min(Math.Max(window.Top, area.Top), Math.Max(area.Top, area.Bottom - height));

        return new WindowRect(left, top, width, height);
    }
}
