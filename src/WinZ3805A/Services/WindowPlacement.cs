using System.Text.Json.Serialization;

namespace WinZ3805A.Services;

/// <summary>
/// A rectangle in the coordinate space <c>AppWindow</c> works in — physical pixels, with the
/// origin at the top-left of the primary display, so a secondary display to its left or above it
/// sits at negative coordinates.
/// </summary>
/// <remarks>
/// <c>Windows.Graphics.RectInt32</c> says exactly this, but it is a WinRT projection, and
/// <see cref="WindowPlacementPolicy"/> has to be reachable from a headless test run. The
/// conversion happens once, at the window.
/// </remarks>
public readonly record struct WindowRect(int Left, int Top, int Width, int Height)
{
    /// <summary>The x coordinate one pixel past the right edge.</summary>
    public int Right => Left + Width;

    /// <summary>The y coordinate one pixel past the bottom edge.</summary>
    public int Bottom => Top + Height;

    /// <summary>Whether the rectangle encloses nothing.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>The enclosed area, in pixels. <see cref="long"/> because a 4K pair overflows an int.</summary>
    public long Area => IsEmpty ? 0L : (long)Width * Height;

    /// <summary>The overlap with <paramref name="other"/>, which may be empty.</summary>
    public WindowRect Intersect(WindowRect other)
    {
        int left = Math.Max(Left, other.Left);
        int top = Math.Max(Top, other.Top);
        return new WindowRect(left, top, Math.Min(Right, other.Right) - left, Math.Min(Bottom, other.Bottom) - top);
    }
}

/// <summary>
/// Where the main window was, how big it was, and which of the §10.3 layouts it was showing.
/// </summary>
/// <remarks>
/// The bounds are always the <i>restored</i> bounds, even when <see cref="IsMaximized"/> is set:
/// a window saved while maximised has to know where to go when the user un-maximises it, and
/// <c>AppWindow</c> reports the maximised rectangle rather than the one underneath. The window
/// therefore remembers the last restored bounds it saw rather than reading them at save time.
/// </remarks>
public sealed record WindowPlacement
{
    /// <summary>Left edge of the restored window, in physical pixels.</summary>
    public required int Left { get; init; }

    /// <summary>Top edge of the restored window, in physical pixels.</summary>
    public required int Top { get; init; }

    /// <summary>Width of the restored window, in physical pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Height of the restored window, in physical pixels.</summary>
    public required int Height { get; init; }

    /// <summary>Whether the window was maximised.</summary>
    public bool IsMaximized { get; init; }

    /// <summary>Whether the window was showing the §10.3 compact layout.</summary>
    public bool IsCompact { get; init; }

    /// <summary>Whether the window was pinned above others (§10.3).</summary>
    /// <remarks>
    /// Here rather than in a view-preferences record, unlike the Details window's pane state: this
    /// is a property of the window frame, set on the same <c>OverlappedPresenter</c> that owns the
    /// size and position beside it, and restored in the same breath. It says nothing about what is
    /// showing inside.
    /// </remarks>
    public bool IsAlwaysOnTop { get; init; }

    /// <summary>
    /// The window width of the standard layout, kept while the window is compact so that leaving
    /// compact has somewhere to go back to (#307).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Entering compact resizes the window to §9.6.2's compact floor, so <see cref="Width"/> and
    /// <see cref="Height"/> are the compact size while <see cref="IsCompact"/> is set — and a launch
    /// straight into compact would otherwise have nothing but the standard floor to leave to. These
    /// two carry the size the user last had in the standard layout, in physical pixels like the
    /// bounds beside them.
    /// </para>
    /// <para>
    /// Nullable, and absent from files written before #307, which must still load: null means
    /// "unknown", and the window falls back to the standard floor.
    /// </para>
    /// </remarks>
    public int? StandardWidth { get; init; }

    /// <summary>The window height of the standard layout; see <see cref="StandardWidth"/>.</summary>
    public int? StandardHeight { get; init; }

    /// <summary>The restored bounds as a rectangle.</summary>
    /// <remarks>
    /// Not serialised: it is the four fields above in another shape, and letting it into the file
    /// would write every derived edge of every rectangle out beside them.
    /// </remarks>
    [JsonIgnore]
    public WindowRect Bounds => new(Left, Top, Width, Height);

    /// <summary>Returns a copy carrying the given bounds.</summary>
    public WindowPlacement WithBounds(WindowRect bounds) => this with
    {
        Left = bounds.Left,
        Top = bounds.Top,
        Width = bounds.Width,
        Height = bounds.Height,
    };
}

/// <summary>
/// Where the main window's <see cref="WindowPlacement"/> is kept between launches.
/// </summary>
/// <remarks>
/// Separate from <see cref="IConnectionPreferenceStore"/> rather than another field on it: the two
/// are written at completely different moments — one when the user picks a port, the other on every
/// drag of the frame — and a window move has no business rewriting the connection settings.
/// </remarks>
public interface IWindowPlacementStore
{
    /// <summary>Reads the stored placement, or <see langword="null"/> if there has never been one.</summary>
    WindowPlacement? Load();

    /// <summary>Writes the placement.</summary>
    void Save(WindowPlacement placement);
}
