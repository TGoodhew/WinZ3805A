using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
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
    /// <remarks>
    /// <b>Content sizes, in effective pixels</b>, converted to a physical window size by
    /// <see cref="WindowSizing"/> — the reading #101 forced on the Details window's 1024 x 720,
    /// applied here because the two figures are the same kind of figure. §10.3 builds its compact
    /// wireframe out of a 32 px title bar and a 64 px medallion, which are effective pixels by
    /// construction, while <c>OverlappedPresenter.PreferredMinimum*</c> is physical. Written
    /// straight into the presenter the floor shrank with every step of display scaling — 380
    /// physical is 109 effective at 350% — beyond the 225% A11Y-7 now requires (#27), and still handled —
    /// and no chrome was added, so even at
    /// 100% the client area was about 364 px against the 380 the wireframe needs (#27).
    /// </remarks>
    private const int MinimumContentWidth = 380;
    private const int MinimumStandardContentHeight = 240;
    private const int MinimumCompactContentHeight = 144;

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

    /// <summary>The physical floor last applied, for the layout the page is currently showing.</summary>
    private SizeInt32 _minimum;

    /// <summary>Recomputes the §10.3 floor when the display scaling under the window changes.</summary>
    private readonly ScalingWatch _scaling;

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

        // §9.2's backdrop. The root already carries the solid fallback from XAML; this upgrades it
        // to Mica Alt where the platform has it. See Services/WindowBackdrop for why that direction
        // rather than the other one (#191).
        WindowBackdrop.Apply(
            this,
            (Panel)Content,
            services.GetService<ILoggerFactory>()?.CreateLogger("Backdrop"));

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

        _scaling = new ScalingWatch(ApplyMinimumSize);

        RestorePlacement();

        // The scaling is only knowable once there is a XamlRoot, which is after the content loads.
        // Until then the floor above was computed against 1.0 — right on a 100% display, and three
        // and a half times too small at the scaling A11Y-7 asks for.
        if (Content is FrameworkElement root)
        {
            root.Loaded += (_, _) => _scaling.Watch(root.XamlRoot);
        }

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

    /// <remarks>
    /// The compact floor grows with the user's text scale (#215). §9.6.2's 144 is a 100 %-text
    /// figure: at 200 % the mode line and the satellite count no longer fit inside it, and the
    /// count — which §9.6.2 requires and the detail line it does not — was the part pushed out.
    /// <see cref="WindowSizing.CompactMinimumHeight"/> holds the derivation and returns exactly 144
    /// at 100 %.
    ///
    /// The standard floor is left alone. 240 has room for the same growth, and #26's sweep found
    /// nothing clipped there at 200 %.
    /// </remarks>
    private int MinimumContentHeight =>
        _page?.IsCompact == true
            ? WindowSizing.CompactMinimumHeight(TextScale)
            : MinimumStandardContentHeight;

    /// <summary>The user's text scale, or 1.0 if the shell cannot be asked.</summary>
    /// <remarks>
    /// Constructed per read rather than held. <see cref="Windows.UI.ViewManagement.UISettings"/>
    /// reaches out to the shell and can throw while a WinAppSDK process is starting — the same
    /// hazard <c>AccentPalette</c> documents — and this is read rarely enough that caching it would
    /// trade a real failure mode for no measurable gain.
    /// </remarks>
    private static double TextScale
    {
        get
        {
            try
            {
                return new Windows.UI.ViewManagement.UISettings().TextScaleFactor;
            }
            catch (Exception)
            {
                return 1.0;
            }
        }
    }

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
            _minimum.Width,
            _minimum.Height);

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
    /// <para>
    /// <c>OverlappedPresenter</c> enforces the floor while the frame is being dragged, which is
    /// what §9.6.2 asks for and what the previous implementation — resizing back after the fact
    /// from inside the change handler — could not do without fighting the user's mouse.
    /// </para>
    /// <para>
    /// The three steps are the Details window's, in the same order and for the same reasons: read
    /// §10.3 as content, convert it to a window size at this window's scaling and chrome, then cap
    /// it at the display so the floor can never exceed the screen it is enforced on.
    /// </para>
    /// </remarks>
    private void ApplyMinimumSize()
    {
        double scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;

        // AppWindow reports both, so the chrome is measured on the window it applies to rather than
        // assumed from a border width that varies with theme, DPI and window style.
        int chromeWidth = Math.Max(0, AppWindow.Size.Width - AppWindow.ClientSize.Width);
        int chromeHeight = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);

        (int width, int height) = WindowSizing.PhysicalMinimum(
            MinimumContentWidth, MinimumContentHeight, scale, chromeWidth, chromeHeight);

        (width, height) = WindowSizing.ClampToWorkArea(
            width, height, DisplayWorkAreas.ForWindow(AppWindow));

        _minimum = new SizeInt32(width, height);

        if (Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        presenter.PreferredMinimumWidth = width;
        presenter.PreferredMinimumHeight = height;

        // Raising the floor does not grow a window that is already under it, which is the case
        // every time the user leaves compact mode.
        int grownWidth = Math.Max(AppWindow.Size.Width, width);
        int grownHeight = Math.Max(AppWindow.Size.Height, height);

        if (grownWidth != AppWindow.Size.Width || grownHeight != AppWindow.Size.Height)
        {
            AppWindow.Resize(new SizeInt32(grownWidth, grownHeight));
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

        // A move is how the window reaches a display of a different size, and two displays can
        // differ in resolution without differing in scaling, so the scaling watch does not cover
        // this. Position only: recomputing on a size change would fight a resize drag.
        if (args.DidPositionChange)
        {
            ApplyMinimumSize();
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
