using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

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

    /// <summary>The §10.3 floor: 380 x 240 standard, 380 x 144 in the compact layout (#99).</summary>
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

    /// <summary>§10.3's title bar, for the one moment before it has been laid out.</summary>
    private const double TitleBarHeight = 32;

    private readonly IWindowPlacementStore _placements;
    private readonly IServiceProvider _services;
    private readonly ILogger? _log;

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

    /// <summary>The user's guide while it is open — one, reachable from either window (#312).</summary>
    private HelpWindow? _help;

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

    /// <summary>
    /// The window size of the standard layout, remembered across compact mode (#307).
    /// </summary>
    /// <remarks>
    /// Kept current from <c>AppWindow.Changed</c> while the window is restored and not compact,
    /// captured as compact is entered, and persisted beside the placement so a launch straight into
    /// compact still knows what to leave to. Null until a standard-layout size has been seen.
    /// </remarks>
    private SizeInt32? _standardSize;

    /// <summary>Recomputes the §10.3 floor when the display scaling under the window changes.</summary>
    private readonly ScalingWatch _scaling;

    /// <summary>
    /// Whether the opening size still has to be applied at the display's real scaling (#578).
    /// </summary>
    /// <remarks>
    /// Set when there was no placement to restore. The size is applied at once against 1.0, which
    /// is right at 100 %, and again when the first scaling arrives with the content — without the
    /// second, a 150 % display would open two-thirds of the size, under the height the footer needs.
    /// </remarks>
    private bool _openingSizePending;

    /// <summary>
    /// Set when the opening size was owed while the page was not showing its whole layout, until it
    /// does and the size can be measured (#678).
    /// </summary>
    private bool _openingSizeAwaitingLayout;

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
        _log = services.GetService<ILoggerFactory>()?.CreateLogger("Window");

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
        page.WholeLayoutShown += OnWholeLayoutShown;
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

        _scaling = new ScalingWatch(OnScalingChanged);

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
            _help?.Close();

            // Nothing is gained here — this window dies with the process — and it is done so that
            // the teardown is the same in all three places. A rule only some callers follow is one
            // nobody can reason about, which is how #487 survived (#487).
            _scaling.Stop();

            _saveAfterIdle.Stop();
            SavePlacement();
        };

        AddAccelerators();
    }

    private OverlappedPresenter? Presenter => AppWindow.Presenter as OverlappedPresenter;

    /// <summary>
    /// Brings this window to the user, from wherever it was (#46).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Activate</c> alone is not enough for the case this exists to serve. A user who launches
    /// the application a second time is very often a user who minimised the first one and forgot,
    /// and <c>Activate</c> on a minimised window raises it without restoring it — the taskbar button
    /// flashes and nothing appears, which looks exactly like the launch having failed.
    /// </para>
    /// <para>
    /// <c>Restore</c> is asked for only when the window is actually minimised, so a second launch
    /// while the window is maximised does not quietly un-maximise it.
    /// </para>
    /// <para>
    /// <b>The one route back to this window (#553)</b> — the tray's click and its <i>Open</i> come
    /// here too — and it puts the window back on a display before showing it. The window can sit
    /// hidden for weeks while the display it was on is unplugged or undocked; see
    /// <see cref="WindowPlacementPolicy.Reopen"/>.
    /// </para>
    /// </remarks>
    public void BringToFront()
    {
        if (Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }

        EnsureOnScreen();

        Activate();
        TakeForeground();
    }

    /// <summary>Puts this window in front of the others, not only on screen (#627).</summary>
    /// <remarks>
    /// <para>
    /// <c>Activate</c> shows a hidden window, which is why reopening from the notification area
    /// always worked. A window that is open but covered is another matter: on Windows 10 a second
    /// launch left it behind whatever covered it, with or without the runtime's companions
    /// (measured on a 22H2 VM, 1 Oct 2026), although the second process hands its foreground right
    /// over before redirecting (<c>App.RedirectToRunningInstance</c>). So the window asks for the
    /// foreground itself, as the tray icon's menu already does.
    /// </para>
    /// <para>
    /// Windows can still refuse - the foreground belongs to whoever the user last gave input to -
    /// and then the taskbar button flashes until the window is chosen, which is Windows' own way
    /// of saying an application wants attention. The log records which of the three happened.
    /// </para>
    /// </remarks>
    private void TakeForeground()
    {
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (GetForegroundWindow() == handle)
        {
            _log?.LogInformation("Brought to the front: it already was.");
            return;
        }

        if (SetForegroundWindow(handle))
        {
            _log?.LogInformation("Brought to the front: took the foreground.");
            return;
        }

        FlashWindowInfo flash = new()
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            Window = handle,
            Flags = FlashTray | FlashUntilForeground,
        };
        _ = FlashWindowEx(ref flash);
        _log?.LogWarning("Brought to the front: Windows kept the foreground elsewhere, so the taskbar button flashes.");
    }

    /// <summary><c>FLASHW_TRAY</c>: flash the taskbar button.</summary>
    private const uint FlashTray = 0x2;

    /// <summary><c>FLASHW_TIMERNOFG</c>: until the window comes to the foreground.</summary>
    private const uint FlashUntilForeground = 0xC;

    /// <summary><c>FLASHWINFO</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    // DllImport rather than LibraryImport, for the reason App gives for AllowSetForegroundWindow:
    // the generated form needs AllowUnsafeBlocks across the whole project.
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);

    /// <summary>Moves the window back onto a display if it is not on one (#553).</summary>
    /// <remarks>
    /// A maximised window is judged by the bounds it un-maximises to, since those say which display
    /// it belongs to; it is un-maximised to be moved and maximised again on the display it lands
    /// on. The move raises <c>AppWindow.Changed</c>, so the corrected bounds are saved by the
    /// ordinary debounce and the next launch agrees with what the user sees.
    /// </remarks>
    private void EnsureOnScreen()
    {
        bool maximised = Presenter?.State == OverlappedPresenterState.Maximized;

        WindowRect current = maximised && _restoredBounds is WindowRect restored
            ? restored
            : new WindowRect(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);

        if (WindowPlacementPolicy.Reopen(
                current,
                DisplayWorkAreas.Current(),
                DisplayWorkAreas.Primary(),
                _minimum.Width,
                _minimum.Height) is not WindowRect moved)
        {
            return;
        }

        // Logged because nothing else records it: a window that came back somewhere else and one
        // that was never away look the same once they are on screen.
        _log?.LogInformation(
            "The window was not on a display; moved from {FromLeft},{FromTop} to {ToLeft},{ToTop}, {Width} x {Height}.",
            current.Left,
            current.Top,
            moved.Left,
            moved.Top,
            moved.Width,
            moved.Height);

        if (maximised)
        {
            Presenter?.Restore();
        }

        AppWindow.MoveAndResize(new RectInt32(moved.Left, moved.Top, moved.Width, moved.Height));

        if (maximised)
        {
            Presenter?.Maximize();
        }
    }

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

            // F1 and the Help button in Details open the same window this one owns (#312).
            details.HelpRequested += (_, _) => ShowHelp();

            _details = details;
        }

        _details.Activate();
    }

    /// <summary>Opens the user's guide, or brings the open one forward (#312).</summary>
    public void ShowHelp()
    {
        if (_help is null)
        {
            HelpWindow help = new(_services);
            help.Closed += (_, _) => _help = null;
            _help = help;
        }

        _help.BringToFront();
    }

    /// <remarks>
    /// §9.7.5's <c>Ctrl+D</c>, <c>Ctrl+Shift+C</c>, <c>Ctrl+Shift+M</c>, <c>Ctrl+Shift+T</c> and <c>Esc</c>. On the
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

        // Hidden, or WinUI shows the first accelerator's keys as a tooltip on whatever owns them -
        // here the whole window, so a pointer resting anywhere without a tooltip of its own got a bare
        // "Ctrl+D" over the readings (#775, found by the 1.4.1 pass's whole-layout photographs). The
        // shortcuts are named where they belong: each button's own tooltip and the guide.
        root.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;

        Add(Windows.System.VirtualKey.D, Windows.System.VirtualKeyModifiers.Control, () =>
        {
            ShowDetails();
            return true;
        });

        // §9.7.5's F1: the user's guide (#312).
        Add(Windows.System.VirtualKey.F1, Windows.System.VirtualKeyModifiers.None, () =>
        {
            ShowHelp();
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
                // the command owns its own re-entrancy guard and its own error surface.
                //
                // That sentence was here before either was true (#503). The guard was a button's
                // IsEnabled, which this route never consults, and the branch a mid-connect press
                // actually takes had neither. A discarded task is only safe over a method that
                // cannot throw, so the claim is load-bearing rather than decorative: if
                // ToggleConnectionAsync ever stops catching, this becomes an unobserved exception
                // and a silent process death.
                _ = _page.ToggleConnectionAsync();
                return true;
            });

        // #568: pin and unpin. Here rather than on the footer's pin for the reason Ctrl+Shift+C is:
        // compact mode collapses the footer, and compact is where a pin is most wanted.
        Add(
            Windows.System.VirtualKey.T,
            Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift,
            () =>
            {
                _page?.ToggleAlwaysOnTop();
                return _page is not null;
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
        if (_page is null)
        {
            return;
        }

        if (Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = _page.IsAlwaysOnTop;
        }

        // #568: the title bar says so, in both layouts.
        PinnedIndicator.Visibility = _page.IsAlwaysOnTop ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Whether the window is pinned above others, for the notification-area menu (#568).</summary>
    public bool IsAlwaysOnTop => _page?.IsAlwaysOnTop == true;

    /// <summary>Pins or unpins the window from outside it - the notification-area menu (#568).</summary>
    public void ToggleAlwaysOnTop() => _page?.ToggleAlwaysOnTop();

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
    /// depends on it — restoring a 380 x 144 compact window against the standard 240 floor would
    /// silently grow its height on every launch. Setting it raises <see cref="OnCompactChanged"/>,
    /// which sizes the window for compact; the stored bounds then land on top, which is the same
    /// size unless the user resized the compact window, in which case theirs wins.
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
            // Nothing to restore, so the system has placed the window and sized it as it sizes any
            // new one - 3840 x 1023 on a 5120 x 1440 display, around a layout 380 wide (#578). The
            // position stays the system's; the size is ours. A compact window is already at its
            // floor, which is compact's whole size.
            _openingSizePending = _page?.IsCompact != true;
            ApplyOpeningSize();
            return;
        }

        AppWindow.MoveAndResize(new RectInt32(
            placement.Left,
            placement.Top,
            placement.Width,
            placement.Height));

        _restoredBounds = placement.Bounds;

        // What leaving compact returns to (#307). A window stored in the standard layout is its own
        // answer; one stored compact carries the standard size it had beside its bounds, if it ever
        // had one, and otherwise leaves to the floor.
        if (!placement.IsCompact)
        {
            _standardSize = new SizeInt32(placement.Width, placement.Height);
        }
        else if (placement.StandardWidth is int standardWidth && placement.StandardHeight is int standardHeight)
        {
            _standardSize = new SizeInt32(standardWidth, standardHeight);
        }
        else
        {
            _standardSize = null;
        }

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
        (int width, int height) = WindowSizeFor(MinimumContentWidth, MinimumContentHeight);

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

    /// <summary>
    /// Converts a §9.6.2 content size into this window's physical size, capped at its display.
    /// </summary>
    /// <remarks>
    /// The three steps are the Details window's, in the same order and for the same reasons: read
    /// the figure as content, convert it at this window's scaling and chrome, then cap it at the
    /// display so no size asked for here can exceed the screen it is applied on.
    /// </remarks>
    private (int Width, int Height) WindowSizeFor(int contentWidth, int contentHeight)
    {
        XamlRoot? root = Content?.XamlRoot;
        double scale = root?.RasterizationScale ?? 1.0;

        // AppWindow reports both, so the chrome is measured on the window it applies to rather than
        // assumed from a border width that varies with theme, DPI and window style.
        int chromeWidth = Math.Max(0, AppWindow.Size.Width - AppWindow.ClientSize.Width);
        int chromeHeight = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);

        // The client area is not all content. Windows 11 keeps a 1 px border across the top of a
        // window whose content extends into the title bar, and the XAML sits beneath it: a 380 x 472
        // client area carried a 380 x 471 root, so a window sized to 472 of content gave the page 439
        // against the 440 it then switched at, and opened with the footer collapsed (#578). Once there is a root it is the
        // measure. Restored only - a maximised window's frame is not the one being sized for.
        if (root is { Size.Height: > 0 } && Presenter is { State: OverlappedPresenterState.Restored })
        {
            int rootHeight = (int)Math.Floor(root.Size.Height * scale);
            chromeHeight = Math.Max(chromeHeight, AppWindow.Size.Height - rootHeight);
        }

        (int width, int height) = WindowSizing.PhysicalMinimum(
            contentWidth, contentHeight, scale, chromeWidth, chromeHeight);

        return WindowSizing.ClampToWorkArea(width, height, DisplayWorkAreas.ForWindow(AppWindow));
    }

    /// <summary>
    /// Sizes a window that had no placement to restore to the page's whole layout: wide enough for
    /// the clock line and the status line with the controls beside each, tall enough for every row
    /// (#578, #581).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The page calculates it (<see cref="MainPage.WholeLayoutSize"/>); this adds the title bar and
    /// converts. Before the content has loaded the rows have no size yet, so the first pass falls
    /// back to §9.6.2's width and the full-layout height, and the second, at the real scaling,
    /// has the page's own answer.
    /// </para>
    /// <para>
    /// Not the 240 floor, which is where the Details window opens and where this one would be
    /// simplest to put. At 240 §9.6.2 collapses the footer, and the launch with nothing to restore
    /// is a first run - the one launch where the user's next action is the Connect button.
    /// </para>
    /// </remarks>
    private void ApplyOpeningSize()
    {
        if (_page is not MainPage page
            || page.IsCompact
            || Presenter is { State: not OverlappedPresenterState.Restored })
        {
            return;
        }

        // #678: the rows are only measurable while they are laid out. Collapsed into the short layout,
        // or not laid out at all yet, they measure as nothing, the 495 floor wins, and at 150 % text the
        // window opened too short and cut the clock line in half. So until the page shows its whole
        // layout the window opens as tall as the work area, and the size is taken from the rows once
        // they have been laid out there (OnWholeLayoutShown).
        if (!page.IsWholeLayoutShowing)
        {
            _openingSizeAwaitingLayout = true;
            int tall = DisplayWorkAreas.ForWindow(AppWindow) is { IsEmpty: false } work ? work.Height : AppWindow.Size.Height;
            AppWindow.Resize(new SizeInt32(
                Math.Max(AppWindow.Size.Width, _minimum.Width),
                Math.Max(tall, _minimum.Height)));
            DisplayWorkAreas.KeepInside(AppWindow);
            return;
        }

        _openingSizeAwaitingLayout = false;
        Windows.Foundation.Size whole = page.WholeLayoutSize();
        double titleBar = AppTitleBar.ActualHeight > 0 ? AppTitleBar.ActualHeight : TitleBarHeight;

        int contentWidth = Math.Max(MinimumContentWidth, (int)Math.Ceiling(whole.Width));
        int contentHeight = Math.Max(
            MainPage.FullLayoutContentHeight,
            (int)Math.Ceiling(whole.Height + titleBar));

        (int width, int height) = WindowSizeFor(contentWidth, contentHeight);

        AppWindow.Resize(new SizeInt32(Math.Max(width, _minimum.Width), Math.Max(height, _minimum.Height)));

        // The position is still the one the system chose before this size existed (#675).
        DisplayWorkAreas.KeepInside(AppWindow);
    }

    /// <summary>Takes the opening size from the rows, now that they are laid out (#678).</summary>
    private void OnWholeLayoutShown(object? sender, EventArgs e)
    {
        if (_openingSizeAwaitingLayout)
        {
            ApplyOpeningSize();
        }
    }

    /// <summary>Recomputes the floor, and applies an opening size still owed at this scaling.</summary>
    private void OnScalingChanged()
    {
        ApplyMinimumSize();

        if (_openingSizePending)
        {
            _openingSizePending = false;
            ApplyOpeningSize();
        }
    }

    /// <summary>
    /// Resizes the window with the layout (#307): compact is for being small, so entering it takes
    /// the window down to §9.6.2's compact floor, and leaving it puts the standard size back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before this the toggle changed only the content, and a standard-sized window kept its frame
    /// around a 64 px medallion and one line of text until the user dragged it down by hand — the
    /// mode arrived and the smallness, which is its point, did not.
    /// </para>
    /// <para>
    /// Only a restored window is resized. A maximised one is un-maximised first, because compact in
    /// a maximised frame is the failure above at its largest; a minimised one is left alone and
    /// takes its size when it comes back. The size to return to is read <i>before</i> the floor is
    /// raised, because raising it grows the window, and the change event would otherwise record
    /// that grown size as the standard one.
    /// </para>
    /// </remarks>
    private void OnCompactChanged(object? sender, EventArgs e)
    {
        if (_page?.IsCompact == true)
        {
            if (Presenter?.State == OverlappedPresenterState.Maximized)
            {
                Presenter.Restore();
            }

            if (Presenter?.State == OverlappedPresenterState.Restored)
            {
                _standardSize = AppWindow.Size;
            }
            else if (_restoredBounds is WindowRect bounds)
            {
                _standardSize = new SizeInt32(bounds.Width, bounds.Height);
            }

            ApplyMinimumSize();

            if (Presenter?.State == OverlappedPresenterState.Restored)
            {
                AppWindow.Resize(_minimum);
            }
        }
        else
        {
            SizeInt32? remembered = _standardSize;

            ApplyMinimumSize();

            if (Presenter?.State == OverlappedPresenterState.Restored)
            {
                (int width, int height) = WindowSizing.SizeLeavingCompact(
                    remembered?.Width,
                    remembered?.Height,
                    _minimum.Width,
                    _minimum.Height,
                    DisplayWorkAreas.ForWindow(AppWindow));

                if (width != AppWindow.Size.Width || height != AppWindow.Size.Height)
                {
                    AppWindow.Resize(new SizeInt32(width, height));
                }
            }
        }

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

            // The standard layout's size follows the user's resizes; the compact layout's does not
            // count as one, since it is what leaving compact returns FROM (#307).
            if (_page?.IsCompact != true)
            {
                _standardSize = sender.Size;
            }
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
            StandardWidth = _standardSize?.Width,
            StandardHeight = _standardSize?.Height,
        });
    }
}
