using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
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

        _model = new MainViewModel(_device.Store, services.GetRequiredService<TimeProvider>());

        _model.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(Render);
        _device.Session.StatusChanged += (_, e) => DispatcherQueue.TryEnqueue(() =>
        {
            _model.Connection = e.Status;
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

        Loaded += async (_, _) =>
        {
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
            VisualStateManager.GoToState(this, value ? "CompactDensity" : "Normal", useTransitions: false);
            CompactChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when the user asks for the §10.4 Details window.</summary>
    /// <remarks>
    /// The page asks; the window opens it. A page that owned a second top-level window would have
    /// to close it, and it is not told when its own window closes.
    /// </remarks>
    public event EventHandler? DetailsRequested;

    /// <summary>Opens the connection dialog, which §10.12 puts on this window.</summary>
    public async Task ShowConnectionDialogAsync()
    {
        ConnectionDialog dialog = new(NewConnectionViewModel()) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }

    /// <summary>Toggles compact mode, which §10.3 binds to double-click and Ctrl+Shift+M.</summary>
    public void ToggleCompact() => IsCompact = !IsCompact;

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

    private async void OnConnectClicked(object sender, RoutedEventArgs e)
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
    private void Render()
    {
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

        VisualStateManager.GoToState(this, _model.AgeSeverity switch
        {
            Severity.Critical => "AgeCritical",
            Severity.Caution => "AgeCaution",
            _ => "AgeFresh",
        }, useTransitions: false);

        ConnectButton.Content = _model.CanConnect ? "Connect" : "Disconnect";

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

    private void RenderClock()
    {
        if (_model.ShownTime is not DisplayTime shown)
        {
            ClockText.Text = "—";
            RolloverBadge.Visibility = Visibility.Collapsed;
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
        ToolTipService.SetToolTip(RolloverBadge, _model.RawDeviceDate);
        AutomationProperties.SetName(RolloverBadge, _model.RawDeviceDate ?? string.Empty);
    }
}
