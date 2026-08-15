namespace WinZ3805A.Services;

/// <summary>
/// Which of §9.8.2's navigation transitions a page change asks for.
/// </summary>
/// <remarks>
/// An enum rather than a <c>NavigationTransitionInfo</c> because the decision and the animation are
/// different things, and only the decision has rules worth asserting. The members are the outcomes
/// §9.8.2's "Nav page change" row allows, which is not the same list as the ones WinUI can draw —
/// see <c>DetailsWindow.TransitionTo</c>.
/// </remarks>
public enum NavigationMotion
{
    /// <summary>No transition: the page is simply there.</summary>
    /// <remarks>
    /// A11Y-13's reduced-motion answer, and also what an arrival with no page behind it gets in
    /// both motion settings. A slide states where the content came from, and the first page of a
    /// freshly opened window came from nowhere.
    /// </remarks>
    None,

    /// <summary>The new page rises into place: travel <i>down</i> the §9.7.1 pane.</summary>
    FromBottom,

    /// <summary>The new page drops into place: travel <i>up</i> the §9.7.1 pane.</summary>
    FromTop,
}

/// <summary>
/// §9.8.2's motion table, as the part of it that is a decision rather than an animation.
/// </summary>
/// <remarks>
/// <para>
/// Free of UI types so it can be linked into a headless test run. What it protects is the half of
/// A11Y-13 that is easy to get wrong twice: reduced motion must beat the direction rule rather than
/// combine with it, and the fallback must land on the same layout, which it does by being the same
/// page with nothing applied to it.
/// </para>
/// <para>
/// §9.8.2's "Directional consistency" paragraph is the reason the direction is taken from the pane
/// index rather than from a navigation stack. The pane is a vertical list and the transition says
/// where in that list the user just went; a back stack would say something about history instead,
/// which is not what the user is looking at.
/// </para>
/// </remarks>
public static class MotionPolicy
{
    /// <summary>Chooses the transition for a page change.</summary>
    /// <param name="animationsEnabled">
    /// <c>WzMotionService.AnimationsEnabled</c>, which is the system-wide setting. When it is
    /// <see langword="false"/> the direction is not consulted at all.
    /// </param>
    /// <param name="fromIndex">
    /// The §9.7.1 pane index of the page being left, or a negative number if there is none — the
    /// first navigation into a window, or a page that is not in the pane.
    /// </param>
    /// <param name="toIndex">The pane index of the page being shown, under the same convention.</param>
    public static NavigationMotion ForNavigation(bool animationsEnabled, int fromIndex, int toIndex)
    {
        if (!animationsEnabled || fromIndex < 0 || toIndex < 0)
        {
            return NavigationMotion.None;
        }

        // Equal indices are a re-navigation to the page already showing. Nothing travelled, so
        // nothing slides; sliding zero rows in an arbitrary direction would be motion that means
        // nothing, which is what §9.8's philosophy paragraph rules out.
        return toIndex.CompareTo(fromIndex) switch
        {
            > 0 => NavigationMotion.FromBottom,
            < 0 => NavigationMotion.FromTop,
            _ => NavigationMotion.None,
        };
    }
}
