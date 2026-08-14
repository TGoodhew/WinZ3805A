using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

using Windows.ApplicationModel;
using Windows.Graphics;

using WinZ3805A.Services;

namespace WinZ3805A.Views;

/// <summary>
/// The application window. Scaffolding only — §10.3 specifies the shipped main
/// window as a small status-medallion surface, and §9.7 puts the NavigationView
/// shell in a separate Receiver Details window.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>The §10.3 floor: 380 x 240 standard, 380 x 120 in the compact layout.</summary>
    private const int MinimumWidth = 380;
    private const int MinimumStandardHeight = 240;
    private const int MinimumCompactHeight = 120;

    private readonly IWindowPlacementStore _placements;

    /// <summary>
    /// Coalesces the burst of <c>AppWindow.Changed</c> events a single drag produces into one write.
    /// </summary>
    private readonly DispatcherTimer _saveAfterIdle = new() { Interval = TimeSpan.FromSeconds(1) };

    private readonly MainPage? _page;

    /// <summary>
    /// The last bounds seen while the window was neither maximised nor minimised.
    /// </summary>
    /// <remarks>
    /// Not read from <c>AppWindow</c> at save time: while maximised it reports the maximised
    /// rectangle, and storing that would leave the next launch with nowhere to un-maximise to.
    /// </remarks>
    private WindowRect? _restoredBounds;

    /// <summary>Creates the window over the application's services.</summary>
    /// <param name="services">
    /// The §12 composition root. Passed on to the page as the navigation parameter rather than
    /// resolved into it here: <c>Frame.Navigate</c> constructs the page itself and cannot call a
    /// constructor with arguments.
    /// </param>
    public MainWindow(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _placements = services.GetRequiredService<IWindowPlacementStore>();

        InitializeComponent();

        // §6.3: the display name is read from the manifest, never hard-coded.
        // Package identity is effectively permanent; the display name is a
        // one-line change, and coupling them in code destroys that option.
        string displayName = Package.Current.DisplayName;
        Title = displayName;
        AppTitleBar.Title = displayName;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Assigned rather than navigated to. Frame.Navigate constructs the page itself, so it
        // cannot pass the services in, and a page that arrives half-built - fields null until
        // OnNavigatedTo - is exactly the shape that has twice killed this application at start-up.
        MainPage page = new(services);
        page.CompactChanged += OnCompactChanged;
        _page = page;
        RootFrame.Content = page;

        RestorePlacement();

        AppWindow.Changed += OnAppWindowChanged;
        _saveAfterIdle.Tick += (_, _) =>
        {
            _saveAfterIdle.Stop();
            SavePlacement();
        };

        Closed += (_, _) =>
        {
            _saveAfterIdle.Stop();
            SavePlacement();
        };
    }

    private OverlappedPresenter? Presenter => AppWindow.Presenter as OverlappedPresenter;

    private int MinimumHeight => _page?.IsCompact == true ? MinimumCompactHeight : MinimumStandardHeight;

    /// <summary>
    /// Puts the window back where it was left, if that is still somewhere the user can see it.
    /// </summary>
    /// <remarks>
    /// The compact state is applied to the page <i>before</i> the size, because the §10.3 floor
    /// depends on it — restoring a 380 x 120 compact window against the standard 240 floor would
    /// silently double its height on every launch.
    /// </remarks>
    private void RestorePlacement()
    {
        WindowPlacement? stored = _placements.Load();

        if (stored is not null && _page is not null)
        {
            _page.IsCompact = stored.IsCompact;
        }

        ApplyMinimumSize();

        WindowPlacement? placement = WindowPlacementPolicy.Restore(
            stored,
            WorkAreas(),
            MinimumWidth,
            MinimumHeight);

        if (placement is null)
        {
            return;
        }

        AppWindow.MoveAndResize(new RectInt32(
            placement.Left,
            placement.Top,
            placement.Width,
            placement.Height));

        _restoredBounds = placement.Bounds;

        if (placement.IsMaximized)
        {
            Presenter?.Maximize();
        }
    }

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
    private static IReadOnlyList<WindowRect> WorkAreas()
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

    /// <summary>Applies the §10.3 minimum size for the layout the page is currently showing.</summary>
    /// <remarks>
    /// <c>OverlappedPresenter</c> enforces the floor while the frame is being dragged, which is
    /// what §9.6.2 asks for and what the previous implementation — resizing back after the fact
    /// from inside the change handler — could not do without fighting the user's mouse.
    /// </remarks>
    private void ApplyMinimumSize()
    {
        if (Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        presenter.PreferredMinimumWidth = MinimumWidth;
        presenter.PreferredMinimumHeight = MinimumHeight;

        // Raising the floor does not grow a window that is already under it, which is the case
        // every time the user leaves compact mode.
        int width = Math.Max(AppWindow.Size.Width, MinimumWidth);
        int height = Math.Max(AppWindow.Size.Height, MinimumHeight);

        if (width != AppWindow.Size.Width || height != AppWindow.Size.Height)
        {
            AppWindow.Resize(new SizeInt32(width, height));
        }
    }

    private void OnCompactChanged(object? sender, EventArgs e)
    {
        ApplyMinimumSize();
        ScheduleSave();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange && !args.DidPositionChange)
        {
            return;
        }

        if (Presenter?.State == OverlappedPresenterState.Restored)
        {
            _restoredBounds = new WindowRect(
                sender.Position.X,
                sender.Position.Y,
                sender.Size.Width,
                sender.Size.Height);
        }

        ScheduleSave();
    }

    /// <remarks>
    /// Restarting the timer on each change is the debounce: a drag raises dozens of events and
    /// writes one file, a second after the user lets go.
    /// </remarks>
    private void ScheduleSave()
    {
        _saveAfterIdle.Stop();
        _saveAfterIdle.Start();
    }

    private void SavePlacement()
    {
        WindowRect bounds = _restoredBounds ?? new WindowRect(
            AppWindow.Position.X,
            AppWindow.Position.Y,
            AppWindow.Size.Width,
            AppWindow.Size.Height);

        _placements.Save(new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,

            // Minimised is not a state worth restoring into — the user would launch the app and get
            // nothing. It is stored as the maximised or restored state it was in before.
            IsMaximized = Presenter?.State == OverlappedPresenterState.Maximized,
            IsCompact = _page?.IsCompact == true,
        });
    }
}
