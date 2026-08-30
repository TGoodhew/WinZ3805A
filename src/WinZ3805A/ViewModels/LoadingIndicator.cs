namespace WinZ3805A.ViewModels;

/// <summary>
/// What §9.11 shows while a read is in flight, according to how long it has been.
/// </summary>
public enum LoadingIndicator
{
    /// <summary>Nothing at all. Either no read is running, or it has not been running long enough.</summary>
    None = 0,

    /// <summary>A 20 px <c>ProgressRing</c> inline in the card header.</summary>
    Ring,

    /// <summary>Skeleton placeholders at the final layout dimensions, with the ring still showing.</summary>
    Skeleton,
}

/// <summary>
/// §9.11's loading ladder: nothing, then a ring, then a skeleton.
/// </summary>
/// <remarks>
/// <para>
/// Here rather than in a page for the reason <see cref="Staleness"/> gives: "how long is too long"
/// is a judgement the application makes once, and two pages implementing it from the prose would
/// drift. Pure, so the thresholds can be tested without a window or a clock.
/// </para>
/// <para>
/// <b>The first threshold is the one that is easy to skip and the one that matters most.</b>
/// §9.11 opens with <i>nothing under 500 ms</i>, and a ring bound straight to "is reading" breaks it
/// on every read that finishes quickly — which is most of them. What the user sees then is a flash:
/// a spinner appearing and vanishing inside a fifth of a second, which reads as a glitch rather than
/// as progress and draws the eye to a card that has nothing to say.
/// </para>
/// <para>
/// The skeleton does not replace the ring. The ring is status — something is happening — and the
/// skeleton is shape — this much is coming. §9.11 lists them as successive states rather than
/// alternatives, and the ring stays visible behind the placeholders.
/// </para>
/// </remarks>
public static class LoadingIndicators
{
    /// <summary>Past this, a read in flight shows a <c>ProgressRing</c> (§9.11).</summary>
    public static readonly TimeSpan RingThreshold = TimeSpan.FromMilliseconds(500);

    /// <summary>Past this, it also shows skeleton placeholders (§9.11).</summary>
    public static readonly TimeSpan SkeletonThreshold = TimeSpan.FromSeconds(2);

    /// <summary>What to show for a read that has been running for <paramref name="elapsed"/>.</summary>
    /// <param name="reading">Whether a read is in flight at all.</param>
    /// <param name="elapsed">How long it has been in flight.</param>
    /// <remarks>
    /// A finished read shows nothing whatever its elapsed time, which is why
    /// <paramref name="reading"/> is a separate argument rather than being encoded as a null
    /// duration: the caller has both facts and conflating them would let a stale elapsed time keep
    /// a ring spinning over data that had already arrived.
    /// </remarks>
    public static LoadingIndicator For(bool reading, TimeSpan elapsed) => (reading, elapsed) switch
    {
        (false, _) => LoadingIndicator.None,
        (_, TimeSpan value) when value >= SkeletonThreshold => LoadingIndicator.Skeleton,
        (_, TimeSpan value) when value >= RingThreshold => LoadingIndicator.Ring,
        _ => LoadingIndicator.None,
    };
}
