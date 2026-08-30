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
