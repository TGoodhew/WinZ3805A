using Microsoft.UI.Windowing;

using Windows.Graphics;

namespace WinZ3805A.Services;

/// <summary>
/// Reads the attached displays for <see cref="WindowPlacementPolicy"/>.
/// </summary>
/// <remarks>
/// The one line of this that touches WinUI, kept apart from the policy so the policy stays
/// headlessly testable — which matters more than usual here, because this machine has a single
/// display and the multi-display branches cannot be reached by running the application at all.
/// </remarks>
public static class DisplayWorkAreas
{
    /// <summary>The desktop area of every attached display, taskbar excluded.</summary>
    /// <remarks>
    /// <b>Indexed, never enumerated.</b> The <c>IReadOnlyList</c> that <c>FindAll</c> returns is a
    /// WinRT vector view that does not implement <c>IIterable</c>, so asking it for an enumerator —
    /// <c>foreach</c>, LINQ, a spread into a collection expression — fails the interface query and
    /// terminates the process: <c>0xc000027b</c> raised inside <c>Microsoft.UI.Xaml.dll</c> over
    /// <c>E_NOINTERFACE</c> from <c>combase.dll</c>, with nothing managed to catch, exactly like
    /// <c>ApplicationData.Current</c> before it. The app builds clean, every test passes, and it
    /// exits before showing a window. Reading it by index is fine.
    /// </remarks>
    public static IReadOnlyList<WindowRect> Current()
    {
        IReadOnlyList<DisplayArea> displays = DisplayArea.FindAll();
        List<WindowRect> areas = new(displays.Count);

        for (int i = 0; i < displays.Count; i++)
        {
            RectInt32 work = displays[i].WorkArea;
            areas.Add(new WindowRect(work.X, work.Y, work.Width, work.Height));
        }

        return areas;
    }

    /// <summary>
    /// The work area of the display <paramref name="window"/> is on, or <see langword="null"/> if
    /// no display could be identified.
    /// </summary>
    /// <remarks>
    /// A single display rather than the whole set, because the caller — <see cref="WindowSizing
    /// .ClampToWorkArea"/> — is asking how large this window is allowed to be, and that is a
    /// property of the screen it is on. <c>DisplayAreaFallback.Nearest</c> answers even for a
    /// window whose rectangle has been dragged wholly off the desktop, which is precisely the
    /// state the clamp exists to make unreachable.
    /// </remarks>
    public static WindowRect? ForWindow(AppWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        DisplayArea? display = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest);

        if (display is null)
        {
            return null;
        }

        RectInt32 work = display.WorkArea;
        return new WindowRect(work.X, work.Y, work.Width, work.Height);
    }
}
