using Microsoft.UI.Xaml;

namespace WinZ3805A.Services;

/// <summary>
/// Calls back when a window's display scaling changes, and only when it changes.
/// </summary>
/// <remarks>
/// <para>
/// All three windows — main, Details and the Help window that joined them on 29 Aug 2026 — derive
/// a minimum size from <c>XamlRoot.RasterizationScale</c> (§9.6.2's, for the first two), and each
/// has to recompute it when the user drags the window to a display at a different setting or
/// changes the setting under it. <c>XamlRoot.Changed</c> is the event that carries that, but it
/// also fires on every size change and on host visibility, so subscribing to it directly would
/// rebuild the floor — and call into <c>DisplayArea</c> — on every frame of a resize drag.
/// </para>
/// <para>
/// The scale is therefore remembered and compared. The first <see cref="Watch"/> always reports,
/// because the constructor computed the floor with no <c>XamlRoot</c> at all and 1.0 stood in for
/// the scaling; on a 100% display that first callback confirms the guess rather than changing it.
/// </para>
/// </remarks>
public sealed class ScalingWatch
{
    private readonly Action _onChanged;

    private XamlRoot? _root;
    private double _scale;

    /// <summary>Creates a watch that invokes <paramref name="onChanged"/>.</summary>
    /// <param name="onChanged">Run on the UI thread, once per actual change of scaling.</param>
    public ScalingWatch(Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        _onChanged = onChanged;
    }

    /// <summary>
    /// Stops watching, and lets the window that owns this be collected (#487).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Without this, closing a window did not release it.</b> The subscription runs
    /// <c>XamlRoot → its event source → the handler → this watch → <see cref="_onChanged"/> → the
    /// window</c>, and <see cref="_onChanged"/> is a method group over the window that created it.
    /// The <c>XamlRoot</c> is the framework's, not ours, so nothing on our side ever dropped the
    /// chain and every window ever opened stayed alive.
    /// </para>
    /// <para>
    /// <b>Measured rather than argued.</b> Two gcdumps of two different builds each show exactly one
    /// live <c>XamlRoot</c>, one event source and one <c>ScalingWatch</c> per window <i>ever
    /// opened</i> — five of each against four closed Details windows plus the main one, and two of
    /// each against one. The counts move together, which is what a chain looks like.
    /// </para>
    /// <para>
    /// It matters for the Details and Help windows, which open and close repeatedly. The main window
    /// lives as long as the process, so calling this there changes nothing and is done anyway: a
    /// teardown that only some callers perform is one nobody can reason about.
    /// </para>
    /// </remarks>
    public void Stop()
    {
        // The same shape Watch uses to drop a previous root, so the two read alike.
        _root?.Changed -= OnRootChanged;
        _root = null;
    }

    /// <summary>
    /// Begins watching <paramref name="root"/>, and reports its scaling immediately.
    /// </summary>
    /// <remarks>
    /// Safe to call more than once with the same root — <c>Loaded</c> fires again whenever the
    /// content is re-parented, and a second subscription would run the callback twice per change.
    /// </remarks>
    public void Watch(XamlRoot? root)
    {
        if (root is null || ReferenceEquals(root, _root))
        {
            _onChanged();
            return;
        }

        _root?.Changed -= OnRootChanged;

        _root = root;
        _scale = root.RasterizationScale;
        root.Changed += OnRootChanged;

        _onChanged();
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (sender.RasterizationScale == _scale)
        {
            return;
        }

        _scale = sender.RasterizationScale;
        _onChanged();
    }
}
