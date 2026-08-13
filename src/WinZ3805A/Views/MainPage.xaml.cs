using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinZ3805A.Controls;
using WinZ3805A.Device.Transport;
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
    private readonly DeviceSessionService _session;
    private readonly ReceiverStateStore _store;
    private readonly PollingService _poller;
    private readonly MainViewModel _model;
    private readonly DispatcherTimer _stalenessTicker = new() { Interval = TimeSpan.FromSeconds(1) };

    private bool _compact;

    /// <summary>Creates the page and its session.</summary>
    public MainPage()
    {
        InitializeComponent();

        _session = new DeviceSessionService(
            (port, settings) => new SerialTransport(port, settings),
            TimeProvider.System);

        _store = new ReceiverStateStore(TimeProvider.System);
        _poller = new PollingService(_session, _store, TimeProvider.System);
        _model = new MainViewModel(_store, TimeProvider.System);

        _model.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(Render);
        _session.StatusChanged += (_, e) => DispatcherQueue.TryEnqueue(() =>
        {
            _model.Connection = e.Status;
            if (e.Status == ConnectionStatus.Connected)
            {
                _model.PortDescription = $"{_session.PortName} · {_session.Settings}";
                _poller.Start();
            }
        });

        // The footer says how old the readings are, so it has to keep counting even when nothing
        // new arrives — which is exactly the case where the user most needs to see it climbing.
        _stalenessTicker.Tick += (_, _) => _model.RaiseAll();
        _stalenessTicker.Start();

        Loaded += (_, _) => Render();
        Unloaded += async (_, _) =>
        {
            _stalenessTicker.Stop();
            await _poller.DisposeAsync();
            await _session.DisposeAsync();
        };
    }

    /// <summary>Whether the window is in the §10.3 compact layout.</summary>
    public bool IsCompact
    {
        get => _compact;
        set
        {
            _compact = value;
            VisualStateManager.GoToState(this, value ? "CompactDensity" : "Normal", useTransitions: false);
        }
    }

    /// <summary>Toggles compact mode, which §10.3 binds to double-click and Ctrl+Shift+M.</summary>
    public void ToggleCompact() => IsCompact = !IsCompact;

    private void OnMedallionDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ToggleCompact();

    private async void OnConnectClicked(object sender, RoutedEventArgs e)
    {
        if (!_model.CanConnect)
        {
            await _poller.StopAsync();
            await _session.DisconnectAsync();
            return;
        }

        // Auto-detect rather than a fixed rate: §10.12 puts 9600-8-N-1 first, so a Z3805A answers
        // on the first attempt and a sibling on the second. The connection dialog proper is P0-1.
        ConnectButton.IsEnabled = false;
        try
        {
            await _session.AutoDetectAsync(FirstAvailablePort());
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    /// <remarks>
    /// A placeholder for §10.12's port picker: it takes the first port the system reports. The real
    /// dialog, with enumeration, friendly names and the manual settings, is P0-1.
    /// </remarks>
    private static string FirstAvailablePort()
    {
        string[] ports = System.IO.Ports.SerialPort.GetPortNames();
        return ports.Length > 0 ? ports[0] : "COM1";
    }

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
        if (_model.DisplayTime is not DateTimeOffset shown)
        {
            ClockText.Text = "—";
            RolloverBadge.Visibility = Visibility.Collapsed;
            return;
        }

        string scale = _model.TimeScale == Device.Models.TimeScale.Unknown
            ? string.Empty
            : $" {_model.TimeScale.ToString().ToUpperInvariant()}";

        ClockText.Text = shown.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            + scale
            + shown.ToString(" · dd MMM yyyy", CultureInfo.CurrentCulture);

        // §7.4: show the corrected date, flag it, and keep what the hardware said in the tooltip.
        // Never substitute silently — a user who sees the wrong year and no explanation reasonably
        // concludes the receiver has failed.
        RolloverBadge.Visibility = _model.IsDateCorrected ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(RolloverBadge, _model.RawDeviceDate);
        AutomationProperties.SetName(RolloverBadge, _model.RawDeviceDate ?? string.Empty);
    }
}
