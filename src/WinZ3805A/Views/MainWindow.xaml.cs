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
    /// <summary>Names this window's placement file. Unchanged from before it was keyed.</summary>
    public const string PlacementKey = "window";

    /// <summary>The §10.3 floor: 380 x 240 standard, 380 x 120 in the compact layout.</summary>
    private const int MinimumWidth = 380;
    private const int MinimumStandardHeight = 240;
    private const int MinimumCompactHeight = 120;

    private readonly IWindowPlacementStore _placements;
    private readonly IServiceProvider _services;

    /// <summary>
    /// Coalesces the burst of <c>AppWindow.Changed</c> events a single drag produces into one write.
    /// </summary>
    private readonly DispatcherTimer _saveAfterIdle = new() { Interval = TimeSpan.FromSeconds(1) };

    private readonly MainPage? _page;

    /// <summary>
    /// The §10.4 Details window while it is open.
    /// </summary>
    /// <remarks>
    /// One at a time: a second would show the same receiver twice and give the user two places to
    /// press Refresh. Windows manage windows, so this lives here rather than on the page - the page
    /// is not told when its own window closes, and something has to close this one.
    /// </remarks>
    private DetailsWindow? _details;

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

        _services = services;
        _placements = services.GetRequiredKeyedService<IWindowPlacementStore>(PlacementKey);

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
        page.AlwaysOnTopChanged += (_, _) =>
        {
            ApplyAlwaysOnTop();

            // Saved explicitly, because nothing else will. The placement file is written on a
            // debounce from AppWindow.Changed, and pinning a window changes neither its size nor
            // its position — so §10.3's "persists across launches" was true of compact mode only by
            // accident, since that one resizes. Found by toggling it and restarting.
            SavePlacement();
        };
        page.DetailsRequested += (_, _) => ShowDetails();
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
            // Before App disposes the container. Details binds to the same session, and leaving it
            // open over a disposed one would be a window showing a receiver that no longer exists.
            _details?.Close();
            _saveAfterIdle.Stop();
            SavePlacement();
        };

        AddAccelerators();
    }

    private OverlappedPresenter? Presenter => AppWindow.Presenter as OverlappedPresenter;

    /// <summary>Opens the §10.4 Details window, or brings the open one forward.</summary>
    public void ShowDetails()
    {
        if (_details is null)
        {
            DetailsWindow details = new(_services);
            details.Closed += (_, _) => _details = null;

            // §10.12's dialog belongs to this window. Details asks rather than building a second
            // one, which would give one session two dialogs able to disconnect each other.
            details.ConnectionRequested += async (_, _) =>
            {
                Activate();
                if (_page is not null)
                {
                    await _page.ShowConnectionDialogAsync();
                }
            };

            _details = details;
        }

        _details.Activate();
    }

    /// <remarks>
    /// §9.7.5's <c>Ctrl+D</c>, <c>Ctrl+Shift+C</c>, <c>Ctrl+Shift+M</c> and <c>Esc</c>. On the
    /// content root rather than the window, because <c>Window</c> has no accelerator collection of
    /// its own - and, for <c>Ctrl+Shift+C</c>, because §9.6.2's compact mode collapses the footer
    /// that hosts <c>ConnectButton</c>. An accelerator attached to that button would vanish with
    /// it, which is precisely the state a keyboard-only user would need it in.
    /// </remarks>
    private void AddAccelerators()
    {
        if (Content is not FrameworkElement root)
        {
            return;
        }

        Add(Windows.System.VirtualKey.D, Windows.System.VirtualKeyModifiers.Control, () =>
        {
            ShowDetails();
            return true;
        });

        Add(
            Windows.System.VirtualKey.M,
            Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift,
            () =>
            {
                _page?.ToggleCompact();
                return true;
            });

        Add(
            Windows.System.VirtualKey.C,
            Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift,
            () =>
            {
                if (_page is null)
                {
                    return false;
                }

                // Fire and forget deliberately: KeyboardAccelerator.Invoked is void-returning, and
                // the command already owns its own re-entrancy guard and its own error surface.
                _ = _page.ToggleConnectionAsync();
                return true;
            });

        // §9.7.5 gives Escape three jobs — cancel a dialog, close a flyout, exit compact mode — and
        // the first two belong to the controls that own them. This one reports whether it acted, so
        // the key is left unhandled when the window is not compact rather than being swallowed from
        // whatever else wanted it.
        Add(Windows.System.VirtualKey.Escape, Windows.System.VirtualKeyModifiers.None, () =>
            _page?.ExitCompact() == true);

        void Add(Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers modifiers, Func<bool> action)
        {
            Microsoft.UI.Xaml.Input.KeyboardAccelerator accelerator = new()
            {
                Key = key,
                Modifiers = modifiers,
            };

            accelerator.Invoked += (_, args) => args.Handled = action();
            root.KeyboardAccelerators.Add(accelerator);
        }
    }

    /// <summary>Applies §10.3's always-on-top toggle to the window.</summary>
    /// <remarks>
    /// <c>IsAlwaysOnTop</c> is a presenter property, which is why this is the window's job and not
    /// the page's — the same division as compact mode, where the page holds the state and the window
    /// owns the size floor that follows from it.
    /// </remarks>
    private void ApplyAlwaysOnTop()
    {
        if (Presenter is OverlappedPresenter presenter && _page is not null)
        {
            presenter.IsAlwaysOnTop = _page.IsAlwaysOnTop;
        }
    }

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
            _page.IsAlwaysOnTop = stored.IsAlwaysOnTop;
        }

        ApplyAlwaysOnTop();

        ApplyMinimumSize();

        WindowPlacement? placement = WindowPlacementPolicy.Restore(
            stored,
            DisplayWorkAreas.Current(),
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
            IsAlwaysOnTop = _page?.IsAlwaysOnTop == true,
        });
    }
}
