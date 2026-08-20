using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

using Windows.ApplicationModel;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

using WinRT.Interop;

using Microsoft.UI;

using WinZ3805A.Controls;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Views;

/// <summary>
/// The §10.4-§10.13 Receiver Details window: the §9.7 <c>NavigationView</c> shell and its title bar.
/// </summary>
/// <remarks>
/// <para>
/// The shell only. Every destination currently shows <see cref="DetailsPlaceholderPage"/>; the real
/// pages arrive one at a time in §15 step 8's order, each replacing its placeholder.
/// </para>
/// <para>
/// It shares the main window's <see cref="DeviceContext"/> rather than opening a second link - that
/// is what the §12 composition root exists for, and a second session would want a port the first
/// one already holds.
/// </para>
/// </remarks>
public sealed partial class DetailsWindow : Window
{
    /// <summary>
    /// §10.2 as amended: 1024 x 720, chosen so the pane is never a rail at the minimum size.
    /// </summary>
    /// <remarks>
    /// Read as a <i>content</i> size in effective pixels and converted to a physical window size by
    /// <see cref="WindowSizing"/>, which is not what §10.2 says and is the only reading under which
    /// the sentence that follows the number in §9.6.2 — "so the default state is the Medium
    /// breakpoint" — is true. Taken literally, a 1024 px window has a 1008 px client area and opens
    /// in <c>LeftCompact</c> at 100% scaling, and in a narrower rail at every scaling above it.
    /// </remarks>
    private const int MinimumContentWidth = 1024;
    private const int MinimumContentHeight = 720;

    /// <summary>Names this window's placement file, keeping it apart from the main window's.</summary>
    public const string PlacementKey = "details-window";

    private readonly DeviceContext _device;
    private readonly IWindowPlacementStore _placements;
    private readonly IDetailsViewPreferenceStore _preferences;
    private readonly IMotionService _motion;
    private readonly DispatcherTimer _saveAfterIdle = new() { Interval = TimeSpan.FromSeconds(1) };

    private WindowRect? _restoredBounds;
    private readonly bool _ready;
    private SizeInt32 _minimum;

    /// <summary>
    /// The §9.7.1 pane index of the page currently showing, or -1 before the first navigation.
    /// </summary>
    /// <remarks>
    /// §9.8.2 takes the direction of the page transition from movement through the pane, and
    /// <c>NavigationView</c> reports only where the user is going. The -1 start is what gives the
    /// window's first page a fade rather than a slide out of nowhere.
    /// </remarks>
    private int _shownIndex = -1;

    /// <summary>The page currently showing, if it can export. Held so it can be unsubscribed.</summary>
    private ICsvExportSource? _exportSource;

    /// <summary>Creates the window over the application's services.</summary>
    /// <param name="services">The §12 composition root.</param>
    public DetailsWindow(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _device = services.GetRequiredKeyedService<DeviceContext>(DeviceKeys.Primary);
        _placements = services.GetRequiredKeyedService<IWindowPlacementStore>(PlacementKey);
        _preferences = services.GetRequiredService<IDetailsViewPreferenceStore>();
        _motion = services.GetRequiredService<IMotionService>();

        InitializeComponent();

        // §6.3: read from the manifest, never hard-coded.
        string displayName = Package.Current.DisplayName;
        Title = $"Receiver Details - {displayName}";
        AppTitleBar.Title = displayName;
        AppTitleBar.Subtitle = "Receiver Details";

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Navigated rather than straight after Navigate, and the page comes from the event args
        // rather than from ContentFrame.Content. Neither detail is optional: Navigate returns
        // before Content is the new page, and Content is still not reliably updated when Navigated
        // fires - both readings leave the command greyed out over a page that can export, which is
        // exactly what this replaced.
        ContentFrame.Navigated += (_, e) => RenderExportAvailability(e.Content);

        BuildNavigation();
        AddAccelerators();
        RestorePlacement();

        // The scaling is only knowable once there is a XamlRoot, which is after the content loads,
        // and it changes when the window is dragged to a display at a different scaling.
        if (Content is FrameworkElement root)
        {
            root.Loaded += (_, _) => ApplyMinimumSize();
        }

        _device.Session.StatusChanged += OnSessionStatusChanged;
        RenderConnection();

        Activated += OnActivated;
        AppWindow.Changed += OnAppWindowChanged;
        _saveAfterIdle.Tick += (_, _) =>
        {
            _saveAfterIdle.Stop();
            SavePlacement();
        };

        Closed += (_, _) =>
        {
            _device.Session.StatusChanged -= OnSessionStatusChanged;
            _saveAfterIdle.Stop();
            SavePlacement();
            SavePreferences();
        };

        _ready = true;
    }

    /// <summary>Raised when the user asks for the connection dialog, which belongs to the main window.</summary>
    /// <remarks>
    /// §9.10.2 says clicking the pill opens the connection dialog, and §10.12 puts that dialog on
    /// the main window. Rather than build a second one here - two dialogs over one session, each
    /// able to disconnect the other - this window asks its owner.
    /// </remarks>
    public event EventHandler? ConnectionRequested;

    /// <summary>The destinations whose real page exists, by tag. The rest fall back to a placeholder.</summary>
    private static readonly IReadOnlyDictionary<string, Type> Pages = new Dictionary<string, Type>
    {
        ["overview"] = typeof(OverviewPage),
        ["satellites"] = typeof(SatellitesPage),
        ["position"] = typeof(PositionPage),
        ["timing"] = typeof(TimingPage),
        ["holdover"] = typeof(HoldoverPage),
        ["registers"] = typeof(StatusRegistersPage),
        ["diagnostics"] = typeof(DiagnosticsPage),
        ["time"] = typeof(TimePage),
        ["settings"] = typeof(SettingsPage),
        ["console"] = typeof(AdvancedConsolePage),
    };

    private OverlappedPresenter? Presenter => AppWindow.Presenter as OverlappedPresenter;

    /// <summary>Selects a destination by its one-based §9.7.5 accelerator number.</summary>
    public void GoTo(int number)
    {
        if (DetailsDestinations.ByNumber(number) is DetailsDestination destination)
        {
            Select(destination);
        }
    }

    private void BuildNavigation()
    {
        foreach (DetailsDestination destination in DetailsDestinations.Numbered)
        {
            Nav.MenuItems.Add(NavigationItem(destination));
        }

        // Settings goes in the footer, which is what keeps it out of Ctrl+1..Ctrl+8. Placing it in
        // the main list would push a real destination past the eighth accelerator (§10.2).
        Nav.FooterMenuItems.Add(NavigationItem(DetailsDestinations.Settings));

        ApplyAdvancedVisibility();
        SettingsPage.AdvancedChanged += OnAdvancedChanged;

        Nav.IsPaneOpen = _preferences.Load().IsPaneOpen;
        Select(DetailsDestinations.Numbered[0]);
    }

    private void OnAdvancedChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(ApplyAdvancedVisibility);

    /// <summary>
    /// Adds or removes §10.11's console from the pane to match Settings - Advanced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Added and removed rather than shown and hidden, so a disabled console is not a
    /// <c>NavigationViewItem</c> a keyboard user can still Tab onto with <c>Visibility</c> as its
    /// only defence. §10.11 says hidden by default; absent is the stronger reading and the easier
    /// one to be sure of.
    /// </para>
    /// <para>
    /// If the console is showing when it is switched off, the pane falls back to the first
    /// destination. Leaving the frame on a page the pane no longer lists would be a page the user
    /// cannot navigate back to.
    /// </para>
    /// </remarks>
    private void ApplyAdvancedVisibility()
    {
        bool enabled = App.Services?.GetService<IAdvancedPreferenceStore>()?.Load().IsConsoleEnabled == true;

        NavigationViewItem? existing = Nav.FooterMenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => (string?)item.Tag == DetailsDestinations.AdvancedConsole.Tag);

        if (enabled && existing is null)
        {
            Nav.FooterMenuItems.Add(NavigationItem(DetailsDestinations.AdvancedConsole));
        }
        else if (!enabled && existing is not null)
        {
            bool showing = (string?)(Nav.SelectedItem as NavigationViewItem)?.Tag
                == DetailsDestinations.AdvancedConsole.Tag;

            Nav.FooterMenuItems.Remove(existing);

            if (showing)
            {
                Select(DetailsDestinations.Numbered[0]);
            }
        }
    }

    /// <remarks>
    /// The tooltip is set explicitly rather than left to the control. <c>NavigationView</c> supplies
    /// one from <c>Content</c> only while the pane is a rail, and §9.9 wants the label reachable in
    /// every mode - including <c>Left</c>, where a truncated long label is otherwise unreadable.
    /// </remarks>
    private static NavigationViewItem NavigationItem(DetailsDestination destination)
    {
        NavigationViewItem item = new()
        {
            Content = destination.Label,
            Tag = destination.Tag,
            Icon = new FontIcon { Glyph = destination.Glyph },
        };

        ToolTipService.SetToolTip(item, destination.Label);
        return item;
    }

    /// <remarks>
    /// §9.7.5's accelerators. <c>Ctrl+1</c>-<c>Ctrl+8</c> is built from the destination list rather
    /// than typed out, so the numbering cannot drift from the pane order - and a ninth destination
    /// is refused by <see cref="DetailsDestinations"/>, not silently left unreachable here.
    /// </remarks>
    private void AddAccelerators()
    {
        // Capped at nine, not at the destination count. VirtualKey.Number0 + 10 is not a digit —
        // it is the colon key — so looping to §10.2's cap of twelve would silently bind Ctrl+: and
        // two more keys nobody asked for. There is no Ctrl+10 to bind instead.
        int accelerated = Math.Min(DetailsDestinations.Numbered.Count, DetailsDestinations.MaxAccelerated);

        for (int number = 1; number <= accelerated; number++)
        {
            int target = number;
            Add(VirtualKey.Number0 + number, VirtualKeyModifiers.Control, () => GoTo(target));
        }

        Add(VirtualKey.F5, VirtualKeyModifiers.None, RefreshFullStatus);
        Add(VirtualKey.E, VirtualKeyModifiers.Control, ExportCurrentView);

        // VK_OEM_COMMA. VirtualKey has no member for it, and §9.7.5 asks for Ctrl+, by name.
        Add((VirtualKey)188, VirtualKeyModifiers.Control, OpenSettings);

        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
        {
            KeyboardAccelerator accelerator = new() { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                action();
                args.Handled = true;
            };
            Nav.KeyboardAccelerators.Add(accelerator);
        }
    }

    private void Select(DetailsDestination destination)
    {
        foreach (object item in Nav.MenuItems.Concat(Nav.FooterMenuItems))
        {
            if (item is NavigationViewItem candidate && (string?)candidate.Tag == destination.Tag)
            {
                Nav.SelectedItem = candidate;
                return;
            }
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args?.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        if (DetailsDestinations.ByTag((string?)item.Tag) is not DetailsDestination destination)
        {
            return;
        }

        // A page that has been built takes the shared DeviceContext; one that has not shows what it
        // will hold. The mapping lives here rather than on the destination record because that
        // record is compiled into a headless test run, where no View type exists.
        int index = DetailsDestinations.IndexOf(destination.Tag);
        NavigationTransitionInfo transition = TransitionTo(index);
        _shownIndex = index;

        if (Pages.TryGetValue(destination.Tag, out Type? page))
        {
            ContentFrame.Navigate(page, _device, transition);
        }
        else
        {
            ContentFrame.Navigate(typeof(DetailsPlaceholderPage), destination, transition);
        }
    }

    /// <summary>
    /// Greys out Export on a page that has nothing to export.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Until P0-13 this command was visible, enabled, and an empty method - it looked like it
    /// worked and did nothing, which is worse than absent, because a user cannot tell it from a
    /// silent failure. Most pages still have no table, so the honest state for them is disabled.
    /// </para>
    /// <para>
    /// The page is subscribed to rather than sampled once, because its data arrives after
    /// navigation completes - the log is read asynchronously and then narrowed by a filter box as
    /// the user types. Sampling on navigation alone would leave the command greyed out over a page
    /// that had since filled with entries.
    /// </para>
    /// </remarks>
    private void RenderExportAvailability(object? content)
    {
        if (!ReferenceEquals(_exportSource, content))
        {
            if (_exportSource is ICsvExportSource previous)
            {
                previous.ExportAvailabilityChanged -= OnExportAvailabilityChanged;
            }

            _exportSource = content as ICsvExportSource;

            if (_exportSource is ICsvExportSource current)
            {
                current.ExportAvailabilityChanged += OnExportAvailabilityChanged;
            }
        }

        ExportButton.IsEnabled = _exportSource is { CanExport: true };
    }

    private void OnExportAvailabilityChanged(object? sender, EventArgs e) =>
        ExportButton.IsEnabled = _exportSource is { CanExport: true };

    /// <summary>
    /// §9.8.2's "Nav page change" row, in as much of it as Windows App SDK 2.3 can draw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Passed to <c>Navigate</c> per call rather than set once as
    /// <c>ContentFrame.ContentTransitions</c>, for two reasons: the direction is not a property of
    /// the frame, and A11Y-13's requirement to <i>subscribe</i> to the setting is met for free by
    /// asking again each time. There is no transition already in flight to switch when the user
    /// changes the setting mid-session, so nothing has to be torn down — the next page change
    /// simply chooses differently.
    /// </para>
    /// <para>
    /// <b>Both halves of §9.8.2's row name an API that does not exist, so this is the defensible
    /// reading rather than the literal one. Filed as #120.</b>
    /// </para>
    /// <para>
    /// <b>Upward travel.</b> <c>SlideNavigationTransitionEffect</c> offers <c>FromBottom</c>,
    /// <c>FromLeft</c> and <c>FromRight</c> — there is no <c>FromTop</c>, in WinUI or in the UWP
    /// enumeration it was carried over from, so §9.8.2's <c>FromBottom</c>/<c>FromTop</c> pair is
    /// half-unbuildable. The two effects that do remain are horizontal, and the same section's
    /// "Directional consistency" paragraph forbids anything sliding horizontally in this
    /// application. Rather than break that rule or invent a hand-rolled storyboard for one
    /// direction, both directions rise: the transition keeps saying "the page changed, vertically"
    /// and stops saying which way. One line changes here if the effect is ever added.
    /// </para>
    /// <para>
    /// <b>Reduced motion.</b> §9.8.2's fallback column asks for <c>EntranceNavigationTransitionInfo</c>
    /// "with opacity only", which was expressible in UWP, where that type carried
    /// <c>FromHorizontalOffset</c> and <c>FromVerticalOffset</c>. WinUI 3 dropped both, leaving a
    /// fade with a short rise baked into it and no way to take the rise out — motion, for the user
    /// who turned motion off. <c>SuppressNavigationTransitionInfo</c> is used instead. It is
    /// stricter than the fallback column and it is exactly what A11Y-13 asks for in words: no
    /// animation runs, and the layout it lands on is the one the animated path lands on.
    /// </para>
    /// </remarks>
    private NavigationTransitionInfo TransitionTo(int index) =>
        MotionPolicy.ForNavigation(_motion.AnimationsEnabled, _shownIndex, index) switch
        {
            NavigationMotion.FromBottom or NavigationMotion.FromTop => new SlideNavigationTransitionInfo
            {
                Effect = SlideNavigationTransitionEffect.FromBottom,
            },
            _ => new SuppressNavigationTransitionInfo(),
        };

    private void OnPaneStateChanged(NavigationView sender, object args)
    {
        if (_ready)
        {
            SavePreferences();
        }
    }

    private void SavePreferences() =>
        _preferences.Save(new DetailsViewPreferences { IsPaneOpen = Nav.IsPaneOpen });

    private void OnSessionStatusChanged(object? sender, ConnectionStatusChanged e) =>
        DispatcherQueue.TryEnqueue(RenderConnection);

    /// <remarks>
    /// §9.11 keeps "Disconnected" and "Connection lost" apart, and the pill is the one place in this
    /// window that says which. Colour, shape and text together, through
    /// <see cref="ConnectionStatusPill"/>, because §9.4.3 permits nothing else.
    /// </remarks>
    private void RenderConnection()
    {
        (Severity severity, string text) = _device.Session.Status switch
        {
            ConnectionStatus.Connected => (Severity.Success, "Connected"),
            ConnectionStatus.Connecting => (Severity.Info, "Connecting"),
            ConnectionStatus.Reconnecting => (Severity.Caution, "Reconnecting"),
            ConnectionStatus.Faulted => (Severity.Critical, "Connection lost"),
            _ => (Severity.Neutral, "Disconnected"),
        };

        StatusPill.Severity = severity;
        StatusPill.StateText = text;
        StatusPill.PortName = _device.Session.Status == ConnectionStatus.Disconnected
            ? null
            : _device.Session.PortName;
    }

    /// <remarks>
    /// §9.7.3: title-bar text and icons drop to <c>WzTextTertiaryBrush</c> when the window is not
    /// active, and the connection pill is exempt - a deactivated window is exactly when someone is
    /// glancing at it from across the room. The exemption is that this touches the command buttons
    /// and nothing else.
    /// </remarks>
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        bool inactive = args.WindowActivationState == WindowActivationState.Deactivated;
        Brush brush = (Brush)Application.Current.Resources[
            inactive ? "WzTextTertiaryBrush" : "WzTextPrimaryBrush"];

        RefreshButton.Foreground = brush;
        ExportButton.Foreground = brush;
        SettingsButton.Foreground = brush;
    }

    private void OnStatusPillClicked(object sender, RoutedEventArgs e) =>
        ConnectionRequested?.Invoke(this, EventArgs.Empty);

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => RefreshFullStatus();

    private void OnExportClicked(object sender, RoutedEventArgs e) => ExportCurrentView();

    private void OnSettingsClicked(object sender, RoutedEventArgs e) => OpenSettings();

    /// <remarks>
    /// F5 takes the full §7.3 sweep ahead of its 10 s cadence. The poller owns both cadences (§12),
    /// so this asks it rather than issuing a command past it and racing a sweep already in flight.
    /// </remarks>
    private void RefreshFullStatus() => _device.Poller.RequestFullSweep();

    /// <summary>§9.7.5's <c>Ctrl+E</c>: writes the current page's table to a CSV file.</summary>
    private void ExportCurrentView() => ExportFrom(_exportSource, Content?.XamlRoot);

    /// <summary>
    /// Runs the save picker for a page and writes its CSV, reporting a failure to the user.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Static and taking its source explicitly, so the Diagnostics card's own Export button reaches
    /// the same code as the title bar rather than growing a second implementation. §9.7.4 shows
    /// both affordances and they must not diverge - one of them writing a different file to the
    /// other is the kind of thing nobody finds until a user reports it.
    /// </para>
    /// <para>
    /// <c>InitializeWithWindow</c> is not optional. <c>FileSavePicker</c> is a WinRT type that
    /// expects a CoreWindow, and in a WinUI 3 desktop app there is not one - without an owner HWND
    /// the call throws <c>COMException</c> at <c>PickSaveFileAsync</c> rather than at construction,
    /// so it looks like the picker failing rather than the setup being wrong.
    /// </para>
    /// <para>
    /// The file is written through the <c>StorageFile</c> stream rather than
    /// <c>File.WriteAllText</c> on its path. The picker hands back a broker-granted file that may
    /// live somewhere this process cannot reach by path - OneDrive, a removable volume, a folder
    /// the package has no capability for - and writing by path works in testing and fails for the
    /// user.
    /// </para>
    /// </remarks>
    internal static async void ExportFrom(ICsvExportSource? source, XamlRoot? xamlRoot)
    {
        if (xamlRoot is null ||
            source is not { CanExport: true } ||
            source.BuildCsv() is not CsvDocument document)
        {
            return;
        }

        FileSavePicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = source.SuggestedFileName,
        };

        picker.FileTypeChoices.Add("Comma-separated values", [".csv"]);

        // The owner comes from the XamlRoot rather than from a window reference, so the card's
        // Export button and the title bar's Ctrl+E both parent the dialog to the window the user is
        // actually looking at. A page cannot see its own Window in WinUI 3, and passing the main
        // window would put the picker behind the Details window it was raised from.
        InitializeWithWindow.Initialize(
            picker,
            Win32Interop.GetWindowFromWindowId(xamlRoot.ContentIslandEnvironment.AppWindowId));

        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            // Deferring updates keeps a cloud-backed file from syncing the half-written version,
            // and SetLength(0) is what makes overwriting an existing export replace it rather than
            // leave the tail of a longer previous log behind it.
            CachedFileManager.DeferUpdates(file);

            using (Stream stream = await file.OpenStreamForWriteAsync())
            {
                stream.SetLength(0);

                await using StreamWriter writer = new(stream, CsvDocument.Encoding);
                await writer.WriteAsync(document.ToText());
            }

            await CachedFileManager.CompleteUpdatesAsync(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // §9.11 reserves ContentDialog for a decision the user cannot proceed without, and this
            // is a stretch of that - but the window's chrome has no InfoBar, the failure is the
            // direct result of something they just asked for, and saying nothing after a save
            // dialog closes reads as success. It names the file so a read-only location or a full
            // disk is identifiable.
            await new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "Couldn't save the export",
                Content = $"{file.Name} could not be written. {exception.Message}",
                CloseButtonText = "Close",
            }.ShowAsync();
        }
    }

    private void OpenSettings() => Select(DetailsDestinations.Settings);

    /// <remarks>
    /// The same §10.3 policy the main window uses, against this window's own file. With no stored
    /// placement the window opens at its minimum rather than at whatever the system would choose:
    /// §9.6.2 sets 1024 x 720 precisely so the first thing the user sees is the Medium breakpoint.
    /// </remarks>
    private void RestorePlacement()
    {
        ApplyMinimumSize();

        WindowPlacement? placement = WindowPlacementPolicy.Restore(
            _placements.Load(),
            DisplayWorkAreas.Current(),
            _minimum.Width,
            _minimum.Height);

        if (placement is null)
        {
            AppWindow.Resize(_minimum);
            return;
        }

        AppWindow.MoveAndResize(new RectInt32(
            placement.Left, placement.Top, placement.Width, placement.Height));
        _restoredBounds = placement.Bounds;

        if (placement.IsMaximized)
        {
            Presenter?.Maximize();
        }
    }

    /// <summary>
    /// Sets the floor from the §9.6.2 content size, this window's scaling, and its own chrome.
    /// </summary>
    /// <remarks>
    /// Called again whenever the window's scaling changes, which is what happens when it is dragged
    /// between displays at different settings. A floor computed once at 100% would let the window
    /// be resized below the Expanded breakpoint on the second display and stay there.
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

        _minimum = new SizeInt32(width, height);

        if (Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = width;
            presenter.PreferredMinimumHeight = height;
        }

        if (AppWindow.Size.Width < width || AppWindow.Size.Height < height)
        {
            AppWindow.Resize(new SizeInt32(
                Math.Max(AppWindow.Size.Width, width),
                Math.Max(AppWindow.Size.Height, height)));
        }
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
                sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height);
        }

        _saveAfterIdle.Stop();
        _saveAfterIdle.Start();
    }

    private void SavePlacement()
    {
        WindowRect bounds = _restoredBounds ?? new WindowRect(
            AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);

        _placements.Save(new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = Presenter?.State == OverlappedPresenterState.Maximized,
        });
    }
}
