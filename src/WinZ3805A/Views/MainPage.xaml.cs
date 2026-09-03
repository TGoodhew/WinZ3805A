using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;

using Microsoft.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinZ3805A.Controls;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Views;

/// <summary>
/// The §10.3 main window surface, and the §15 step 7 vertical slice that proves the whole stack.
/// </summary>
/// <remarks>
/// <para>
/// The judgement lives in <see cref="MainViewModel"/>, which is tested; this file is the wiring
/// that puts it on screen. Bindings are written by hand rather than through <c>x:Bind</c> because
/// several of them drive visual states rather than properties, and splitting the two mechanisms
/// across one small surface would be harder to follow than doing all of it in one place.
/// </para>
/// <para>
/// The session, store and poller are created here for now. §12 wants them resolved from keyed DI,
/// and they are written to allow it — nothing static, one instance per device — but the composition
/// root proper arrives with the second window, since a container holding exactly one object is
/// ceremony rather than architecture.
/// </para>
/// </remarks>
public sealed partial class MainPage : Page
{
    private readonly DeviceContext _device;
    private readonly MainViewModel _model;
    private readonly SerialPortEnumerator _ports;
    private readonly IConnectionPreferenceStore _preferences;
    private readonly DispatcherTimer _stalenessTicker = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly StateAnnouncer _announcer = new();

    private bool _compact;
    private bool _launchAttempted;

    /// <summary>
    /// False until the constructor has finished building the view model.
    /// </summary>
    /// <remarks>
    /// <c>IsOn="True"</c> on the toggle raises <c>Toggled</c> during <c>InitializeComponent</c>,
    /// which is before any of the fields below exist. Without this guard the handler dereferences a
    /// null view model and the process exits before a window is ever shown — a failure that builds
    /// cleanly and passes every test.
    /// </remarks>
    private readonly bool _ready;

    /// <summary>Creates the page over the application's services.</summary>
    /// <param name="services">The §12 composition root, which owns the receiver.</param>
    public MainPage(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        InitializeComponent();

        // §12: resolved by device key, never constructed here. The Details window binds to the
        // same context, and a page that built its own session would give it a second port.
        _device = services.GetRequiredKeyedService<DeviceContext>(DeviceKeys.Primary);
        _ports = services.GetRequiredService<SerialPortEnumerator>();
        _preferences = services.GetRequiredService<IConnectionPreferenceStore>();

        _model = new MainViewModel(
            _device.Store, services.GetRequiredService<TimeProvider>(), _device.Driver);

        _model.PropertyChanged += OnModelChanged;
        _device.Session.StatusChanged += (_, e) => DispatcherQueue.TryEnqueue(() =>
        {
            _model.Connection = e.Status;

            // Re-set on every connect, not captured once: the session re-selects a driver each time
            // the link comes up (#287), and the medallion's mode is that driver's reading of the
            // receiver's token (#304).
            _model.Driver = _device.Driver;

            if (e.Status == ConnectionStatus.Connected)
            {
                _model.PortDescription = $"{_device.Session.PortName} · {_device.Session.Settings}";
                _device.Poller.Start();
            }
        });

        // The footer says how old the readings are, so it has to keep counting even when nothing
        // new arrives — which is exactly the case where the user most needs to see it climbing.
        _stalenessTicker.Tick += (_, _) => _model.RaiseAll();
        _stalenessTicker.Start();

        _ready = true;

        // §9.6.2's main row. The window enforces the floor; the page decides what fits inside it,
        // and it has to re-decide on every resize because the two thresholds sit 232 px apart.
        SizeChanged += (_, _) => ApplyLayoutState();

        Loaded += async (_, _) =>
        {
            ApplyLayoutState();
            Render();
            await ConnectOnLaunchAsync();
        };
        // The session and poller belong to the container now, and are let go when the window that
        // opened the receiver closes. Disposing them here would take the port away from the §10.4
        // Details window, which shares this context - and Unloaded is not raised on window close
        // anyway, so this was never the teardown it looked like.
        Unloaded += (_, _) => _stalenessTicker.Stop();
    }

    /// <summary>
    /// Raised when <see cref="IsCompact"/> changes.
    /// </summary>
    /// <remarks>
    /// The window owns the consequences of the toggle that the page cannot reach: §10.3 gives the
    /// two layouts different minimum heights, and the compact state is part of what persists across
    /// launches. An event rather than a call back into the window keeps the page usable on its own,
    /// which is what the §9.8.2 Details transition will need.
    /// </remarks>
    public event EventHandler? CompactChanged;

    /// <summary>Whether the window is in the §10.3 compact layout.</summary>
    public bool IsCompact
    {
        get => _compact;
        set
        {
            if (_compact == value)
            {
                return;
            }

            _compact = value;
            ApplyLayoutState();
            CompactChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The height below which §9.6.2's main row applies, as <b>this page</b> measures it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured, not derived.</b> Swept at 8 px steps against the built layout: the footer's
    /// Connect button has no height at all below 436 px of content, 4 px at 440, and first reaches
    /// §9.6.3's fixed 32 px pointer floor — the one no mode may reduce — at 472. A button that is
    /// present and 20 px tall is worse than one that is not present, because it is still a target.
    /// </para>
    /// <para>
    /// <b>440, not §9.6.2's 472, and the difference is the title bar.</b> §9.6.2's figures are the
    /// window's whole content area, which under <c>ExtendsContentIntoTitleBar</c> includes the
    /// 32 px <c>TitleBar</c>; this page is in the row beneath it, so its <c>ActualHeight</c> is
    /// always 32 less. Comparing §9.6.2's number against the page's own height directly would put
    /// the switch a title bar too late — which it did, and the sweep found it.
    /// </para>
    /// </remarks>
    private const double ShortLayoutHeight = 472 - 32;

    /// <summary>Picks the one §10.3 layout that fits, and applies it.</summary>
    /// <remarks>
    /// <para>
    /// One visual state group and therefore one decision, rather than a height group layered over
    /// the density group. Two groups would set <c>Visibility</c> on the same four rows, and a
    /// <c>VisualState</c> reverts its setters when it is left — so leaving the short state while
    /// compact mode was on would restore the rows compact mode had collapsed. States within a group
    /// are mutually exclusive, which removes the interaction rather than documenting it.
    /// </para>
    /// <para>
    /// Compact wins, because the user asked for it (§10.3) and the height did not.
    /// </para>
    /// </remarks>
    private void ApplyLayoutState() =>
        VisualStateManager.GoToState(
            this,
            _compact ? "CompactDensity" : ActualHeight < ShortLayoutHeight ? "ShortLayout" : "Normal",
            useTransitions: false);

    /// <summary>Raised when the user asks for the §10.4 Details window.</summary>
    /// <remarks>
    /// The page asks; the window opens it. A page that owned a second top-level window would have
    /// to close it, and it is not told when its own window closes.
    /// </remarks>
    public event EventHandler? DetailsRequested;

    /// <summary>Opens the connection dialog, which §10.12 puts on this window.</summary>
    /// <summary>Shows or hides §9.11's first-run surface (#253).</summary>
    /// <remarks>
    /// The copy comes from <see cref="FirstRun"/> rather than the markup so it is asserted rather
    /// than eyeballed, and the rule for <i>when</i> lives there too — see that type for why first run
    /// ends when a port is chosen rather than when one connects.
    /// </remarks>
    private void RenderFirstRun()
    {
        bool show = FirstRun.ShouldShow(_preferences.Load().PortName, _model.Connection);

        if (show)
        {
            FirstRunHeadline.Text = FirstRun.Headline;
            FirstRunBody.Text = FirstRun.Body;
            FirstRunAction.Content = FirstRun.ActionLabel;
        }

        FirstRunPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnFirstRunActionClicked(object sender, RoutedEventArgs e) =>
        await ShowConnectionDialogAsync();

    public async Task ShowConnectionDialogAsync()
    {
        ConnectionDialog dialog = new(NewConnectionViewModel()) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }

    /// <summary>Toggles compact mode, which §10.3 binds to double-click and Ctrl+Shift+M.</summary>
    public void ToggleCompact() => IsCompact = !IsCompact;

    /// <summary>
    /// §9.7.5's <c>Esc</c>: leaves compact mode, and does nothing anywhere else.
    /// </summary>
    /// <remarks>
    /// Reports whether it acted, so the caller can leave the key unhandled when the window was not
    /// compact. Swallowing Escape unconditionally on the main window would take it away from
    /// anything else that wants it.
    /// </remarks>
    public bool ExitCompact()
    {
        if (!IsCompact)
        {
            return false;
        }

        IsCompact = false;
        return true;
    }

    /// <summary>Whether the window is pinned above others (§10.3).</summary>
    /// <remarks>
    /// The page owns the toggle's state because the toggle is on the page; the window owns the
    /// consequence, because <c>IsAlwaysOnTop</c> is a presenter property the page cannot reach.
    /// Same division as compact mode.
    /// </remarks>
    public bool IsAlwaysOnTop
    {
        get => AlwaysOnTopButton.IsChecked == true;
        set => AlwaysOnTopButton.IsChecked = value;
    }

    /// <summary>Raised when the user toggles always-on-top.</summary>
    public event EventHandler? AlwaysOnTopChanged;

    private void OnAlwaysOnTopClicked(object sender, RoutedEventArgs e) =>
        AlwaysOnTopChanged?.Invoke(this, EventArgs.Empty);

    private void OnMedallionDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ToggleCompact();

    private void OnDetailsClicked(object sender, RoutedEventArgs e) =>
        DetailsRequested?.Invoke(this, EventArgs.Empty);

    /// <remarks>
    /// Populated once, on first open, rather than in the constructor: enumerating every system zone
    /// costs more than a window that may never have its picker opened should pay at start-up.
    /// </remarks>
    private void EnsureZonesLoaded()
    {
        if (ZonePicker.Items.Count > 0)
        {
            return;
        }

        foreach (TimeZoneInfo zone in TimeZoneInfo.GetSystemTimeZones())
        {
            ZonePicker.Items.Add(zone);
        }

        ZonePicker.DisplayMemberPath = nameof(TimeZoneInfo.DisplayName);
        ZonePicker.SelectedItem = TimeZoneInfo.GetSystemTimeZones()
            .FirstOrDefault(z => z.Id == _model.DisplayZone.Id);
    }

    private void OnUseMachineZoneToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        EnsureZonesLoaded();
        ZonePicker.IsEnabled = !UseMachineZone.IsOn;

        if (UseMachineZone.IsOn)
        {
            _model.DisplayZone = TimeZoneInfo.Local;
        }
        else if (ZonePicker.SelectedItem is TimeZoneInfo chosen)
        {
            _model.DisplayZone = chosen;
        }
    }

    private void OnZoneSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !UseMachineZone.IsOn && ZonePicker.SelectedItem is TimeZoneInfo chosen)
        {
            _model.DisplayZone = chosen;
        }
    }

    private async void OnConnectClicked(object sender, RoutedEventArgs e) =>
        await ToggleConnectionAsync();

    /// <summary>
    /// §9.7.5's <c>Ctrl+Shift+C</c>: connects if disconnected, disconnects if connected.
    /// </summary>
    /// <remarks>
    /// Public because the accelerator lives on the window's content root, not on
    /// <c>ConnectButton</c> - and it has to. §9.6.2's compact mode collapses the footer that hosts
    /// that button, so an accelerator attached to the button would go with it and leave a
    /// keyboard-only user in compact mode with no route to connect or disconnect at all. Ctrl+D and
    /// Ctrl+Shift+M already survive compact for the same reason.
    /// <para>
    /// The button is disabled around the dialog rather than the command being re-entrancy-guarded,
    /// which is why the guard has to tolerate the button being collapsed: setting
    /// <c>IsEnabled</c> on a collapsed element is harmless, and the dialog is modal, so a second
    /// Ctrl+Shift+C cannot arrive while one is open.
    /// </para>
    /// </remarks>
    public async Task ToggleConnectionAsync()
    {
        if (!_model.CanConnect)
        {
            await _device.Poller.StopAsync();
            await _device.Session.DisconnectAsync();
            return;
        }

        ConnectButton.IsEnabled = false;
        try
        {
            await ShowConnectionDialogAsync();
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Honours §10.12's "Connect to this device on launch" without showing the dialog.
    /// </summary>
    /// <remarks>
    /// Awaited rather than fired and forgotten, so an exception has somewhere to surface, but not
    /// blocking: <c>Loaded</c> has already returned by the time the first probe goes out, and the
    /// window paints its disconnected state while the port opens behind it. Guarded because
    /// <c>Loaded</c> raises again if the page is ever re-parented, and a second attempt would tear
    /// down a session that is already working.
    /// </remarks>
    private async Task ConnectOnLaunchAsync()
    {
        if (_launchAttempted)
        {
            return;
        }

        _launchAttempted = true;
        if (!await NewConnectionViewModel().ConnectOnLaunchAsync())
        {
            // §9.11 keeps "Disconnected" and "Connection lost" apart, and the session reports a
            // failed attempt as a fault. A remembered port that did not answer at start-up has lost
            // nothing — the window must not open claiming it has.
            await _device.Session.DisconnectAsync();
        }
    }

    private ConnectionViewModel NewConnectionViewModel() => new(_device.Session, _ports, _preferences);

    /// <summary>Pushes the view model onto the surface.</summary>
    /// <summary>
    /// Renders on a model notification (#388).
    /// </summary>
    /// <remarks>
    /// Named rather than a lambda even though this page is never navigated away from - it is the
    /// main window's content for the window's whole life. An unremovable subscription is a defect
    /// waiting for the day that stops being true, and the rule is cheaper to keep than to argue
    /// about per page.
    /// </remarks>
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(Render);

    private void Render()
    {
        RenderFirstRun();

        Medallion.Mode = _model.Mode;
        Medallion.Samples = _model.TimeIntervalSamples;
        Medallion.SatelliteCount = _model.SatelliteCount;
        Medallion.TimeIntervalNanoseconds = _model.TimeIntervalNanoseconds;
        Medallion.ModeDetail = _model.ModeDetail;

        ModeText.Text = _model.ModeText;
        ModeDetailText.Text = _model.ModeDetail ?? string.Empty;
        ModeDetailText.Visibility = string.IsNullOrEmpty(_model.ModeDetail)
            ? Visibility.Collapsed
            : Visibility.Visible;

        // §10.3: the coasting pill is the single most useful diagnostic the application surfaces.
        CoastingPill.Visibility = _model.IsCoasting ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(CoastingPill, _model.CoastingTooltip);

        Satellites.Value = _model.SatelliteCount;

        TimeInterval.Value = _model.TimeIntervalNanoseconds;

        RenderMerit(TfomPill, "TFOM", _model.Tfom);
        RenderMerit(FfomPill, "FFOM", _model.Ffom);

        RenderClock();

        FooterText.Text = string.IsNullOrEmpty(_model.PortDescription)
            ? _model.AgeDescription
            : $"{_model.PortDescription} · {_model.AgeDescription}";

        // The word is set here rather than in the visual state, because a Setter can only assign a
        // literal and this one comes from the same place the severity does — one switch in
        // Staleness, so the pill's three channels cannot get out of step with each other.
        FooterStalenessPill.Text = Staleness.LabelOf(_model.AgeSeverity) ?? string.Empty;

        VisualStateManager.GoToState(this, _model.AgeSeverity switch
        {
            Severity.Critical => "AgeCritical",
            Severity.Caution => "AgeCaution",
            _ => "AgeFresh",
        }, useTransitions: false);

        // The label and its tooltip move together. The tooltip carries the accelerator, which is
        // registered on the window's content root rather than on this button (§9.6.2 collapses the
        // footer in compact mode), so nothing would otherwise tell a user the shortcut exists —
        // the Details button beside it has said so all along, and this one said nothing (#319).
        ConnectButton.Content = _model.CanConnect ? "Connect" : "Disconnect";
        ToolTipService.SetToolTip(
            ConnectButton,
            _model.CanConnect
                ? "Connect to a receiver (Ctrl+Shift+C)"
                : "Disconnect from the receiver (Ctrl+Shift+C)");

        // A11Y-9. Last, so that the announcement is never made about a surface that has not been
        // written yet: a reader that follows it straight to the medallion must find the state it
        // was just told about. The announcer returns null on the staleness tick, which is most of
        // the calls into this method.
        if (_announcer.Observe(_model.Connection, _model.Mode, _model.IsCoasting, _device.Session.PortName)
            is Announcement announcement)
        {
            LiveRegion.Announce(Announcer, announcement);
        }
    }

    /// <remarks>
    /// Lower is better for both figures of merit, and §9.4.3 forbids conveying that by colour alone
    /// — so each renders through a pill, which carries a shape and the number in text as well.
    /// </remarks>
    private static void RenderMerit(SeverityPill pill, string label, int? value)
    {
        pill.Text = value is int merit ? $"{label} {merit}" : $"{label} —";
        pill.Severity = value switch
        {
            null => Severity.Neutral,
            <= 3 => Severity.Success,
            <= 6 => Severity.Caution,
            _ => Severity.Critical,
        };
    }

    /// <summary>
    /// Lights a badge under the pointer, and unlights it again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The time-zone button next to these gets its hover fill from the stock <c>Button</c> template.
    /// A <c>Border</c> has no template and therefore no <c>PointerOver</c> state, so enlarging the
    /// badges to A11Y-5's 32 px fixed <i>where</i> the pointer worked and left them looking exactly
    /// as unresponsive as before — which is how the defect was reported a second time, after the
    /// size was already right. A tooltip's delay sits in front of the only other feedback there is.
    /// </para>
    /// <para>
    /// Handled in code rather than through a <c>VisualStateGroup</c> because these are page
    /// elements rather than templated controls, and <c>GoToState</c> needs a control to target.
    /// </para>
    /// </remarks>
    private void OnBadgePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border badge)
        {
            badge.Background = (Brush)Application.Current.Resources["WzSurfaceHoverBrush"];
        }
    }

    /// <summary>Restores the badge's transparent fill when the pointer leaves.</summary>
    /// <remarks>
    /// Transparent and never null. An unset <c>Background</c> is not hit-testable at all, so
    /// clearing it here would shrink the target back to the glyph on the first hover — the original
    /// defect, reintroduced by the fix for it.
    /// </remarks>
    private void OnBadgePointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border badge)
        {
            badge.Background = new SolidColorBrush(Colors.Transparent);
        }
    }

    private void RenderClock()
    {
        if (_model.ShownTime is not DisplayTime shown)
        {
            ClockText.Text = "—";
            RolloverBadge.Visibility = Visibility.Collapsed;
            ProvisionalBadge.Visibility = Visibility.Collapsed;
            return;
        }

        // The zone label is never omitted. A time without one invites the reader to assume it is
        // theirs, and near local midnight the date is a whole day out if it is not (#95).
        ClockText.Text = shown.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            + $" {shown.ZoneLabel}"
            + shown.Value.ToString(" · dd MMM yyyy", CultureInfo.CurrentCulture);

        // §7.4: show the corrected date, flag it, and keep what the hardware said in the tooltip.
        // Never substitute silently — a user who sees the wrong year and no explanation reasonably
        // concludes the receiver has failed.
        RolloverBadge.Visibility = _model.IsDateCorrected ? Visibility.Visible : Visibility.Collapsed;
        // The same sentence to the tooltip and to the automation name, because §9.9 wants an
        // icon-only control to have both and a screen-reader user has no other route to it.
        RolloverTip.Content = _model.RolloverExplanation ?? string.Empty;
        AutomationProperties.SetName(RolloverBadge, _model.RolloverExplanation ?? string.Empty);

        // #245. The receiver's own marker for "this is the power-up default, not yet corrected from
        // GPS". It belongs on the primary window rather than only in Details, because §10.3's whole
        // premise is a window somebody leaves running and glances at - and this is the one reading
        // on that surface that can be arbitrarily wrong while looking entirely ordinary.
        const string provisional =
            "Power-up time. The receiver has not yet corrected this from GPS, so it may be wrong by "
            + "any amount until the first satellite is tracked.";

        ProvisionalBadge.Visibility = _model.IsTimeProvisional ? Visibility.Visible : Visibility.Collapsed;
        ProvisionalTip.Content = provisional;
        AutomationProperties.SetName(ProvisionalBadge, provisional);
    }
}
