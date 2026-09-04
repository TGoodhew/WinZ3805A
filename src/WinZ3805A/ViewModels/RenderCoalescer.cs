namespace WinZ3805A.ViewModels;

/// <summary>
/// Collapses a burst of notifications into one render (#399).
/// </summary>
/// <remarks>
/// <para>
/// <c>ReceiverStateStore</c> raises about seven <c>PropertyChanged</c> notifications per
/// fast sweep and a view's <c>Render</c> rewrites everything it draws, so six of the seven repaint
/// what the seventh is about to. Each repaint hands boxed values to WinRT, and every one of those
/// mints a COM callable wrapper the runtime records in storage it never shrinks - which is what
/// #399 turned out to be.
/// </para>
/// <para>
/// <b>THE ORDERING IS LOAD-BEARING AND IS WHY THIS IS A CLASS RATHER THAN THREE LINES.</b>
/// <see cref="Begin"/> must be called at the <i>start</i> of the render, before it reads any state.
/// A notification arriving while the render runs then sees the gate open, schedules another, and is
/// drawn by it. Clearing the flag <i>after</i> the render instead loses that update silently: the
/// notification is swallowed by a render that had already read the old value. The two orderings
/// differ by one line and only one of them is correct, so it is asserted rather than argued -
/// <c>RenderCoalescerTests</c> covers it, and this pattern was previously written out by hand in
/// ten views, giving the eleventh page ten chances to copy the wrong one.
/// </para>
/// <para>
/// Deliberately free of WinUI: the dispatcher arrives as a <see cref="Func{TResult}"/> returning
/// whether the callback was accepted, which is what lets the race be tested headlessly.
/// </para>
/// </remarks>
public sealed class RenderCoalescer
{
    private readonly Func<bool> _enqueue;

    private int _queued;

    /// <summary>Creates a coalescer over a dispatcher.</summary>
    /// <param name="enqueue">
    /// Schedules the render callback, returning false when it could not be accepted - which a
    /// dispatcher does while it is shutting down.
    /// </param>
    public RenderCoalescer(Func<bool> enqueue)
    {
        ArgumentNullException.ThrowIfNull(enqueue);

        _enqueue = enqueue;
    }

    /// <summary>Asks for a render, unless one is already queued.</summary>
    /// <returns><see langword="true"/> when this call scheduled one.</returns>
    /// <remarks>
    /// Called from whatever thread raised the notification, which is the poll thread rather than
    /// the UI thread, so the gate is an interlocked exchange rather than a bool.
    /// </remarks>
    public bool Request()
    {
        if (Interlocked.Exchange(ref _queued, 1) == 1)
        {
            return false;
        }

        if (_enqueue())
        {
            return true;
        }

        // Nothing will run to reopen the gate, so reopen it here or this view never renders again.
        Interlocked.Exchange(ref _queued, 0);
        return false;
    }

    /// <summary>Opens the gate. Call this first thing in the render, before reading any state.</summary>
    public void Begin() => Interlocked.Exchange(ref _queued, 0);
}
