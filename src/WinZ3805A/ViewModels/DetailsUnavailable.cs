namespace WinZ3805A.ViewModels;

/// <summary>
/// A destination the connected receiver cannot fill, and why (#435).
/// </summary>
/// <remarks>
/// <para>
/// The navigation parameter for <c>DetailsPlaceholderPage</c> when a page is dimmed in the pane.
/// The pane already says why on hover, but <b>a tooltip is easy to miss and unreachable by touch</b>,
/// so selecting the entry lands on the same sentence written out as a page.
/// </para>
/// <para>
/// Deliberately a different parameter type from a bare <see cref="DetailsDestination"/>, which the
/// same page uses for a destination that is merely unbuilt. Those two look alike on screen and are
/// nothing alike: one is a promise that content is coming, the other is a statement that it never
/// will on this receiver, and a page that could not tell them apart would have to guess.
/// </para>
/// </remarks>
/// <param name="Destination">The page the user asked for.</param>
/// <param name="Reason">One sentence naming the family that cannot supply it.</param>
public sealed record DetailsUnavailable(DetailsDestination Destination, string Reason);
