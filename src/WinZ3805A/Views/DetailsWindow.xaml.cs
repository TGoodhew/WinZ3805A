using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using Windows.ApplicationModel;
using Windows.Graphics;
using Windows.System;

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
    private readonly DispatcherTimer _saveAfterIdle = new() { Interval = TimeSpan.FromSeconds(1) };

    private WindowRect? _restoredBounds;
    private readonly bool _ready;
    private SizeInt32 _minimum;

    /// <summary>Creates the window over the application's services.</summary>
    /// <param name="services">The §12 composition root.</param>
    public DetailsWindow(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _device = services.GetRequiredKeyedService<DeviceContext>(DeviceKeys.Primary);
        _placements = services.GetRequiredKeyedService<IWindowPlacementStore>(PlacementKey);
        _preferences = services.GetRequiredService<IDetailsViewPreferenceStore>();

        InitializeComponent();

        // §6.3: read from the manifest, never hard-coded.
        string displayName = Package.Current.DisplayName;
        Title = $"Receiver Details - {displayName}";
        AppTitleBar.Title = displayName;
        AppTitleBar.Subtitle = "Receiver Details";

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

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

        Nav.IsPaneOpen = _preferences.Load().IsPaneOpen;
        Select(DetailsDestinations.Numbered[0]);
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
        for (int number = 1; number <= DetailsDestinations.Numbered.Count; number++)
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

        if (DetailsDestinations.ByTag((string?)item.Tag) is DetailsDestination destination)
        {
            ContentFrame.Navigate(typeof(DetailsPlaceholderPage), destination);
        }
    }

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

    private static void ExportCurrentView()
    {
        // §9.7.4 puts Export in the title bar, but what it exports is the current page's data and
        // there is no page yet. It is wired when the first page with a table lands (§10.5).
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
