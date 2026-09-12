using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel;

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
    /// <summary>Where a failed connect or disconnect goes, so that it is not a process death (#503).</summary>
    private readonly ILogger? _logger;

    /// <summary>
    /// Re-entrancy guard for <see cref="ToggleConnectionAsync"/>, covering both its branches and both
    /// its entry points — see that method's remarks for why a control's <c>IsEnabled</c> cannot (#503).
    /// </summary>
    private bool _toggling;

    private readonly bool _ready;

    /// <summary>The visual state last requested, so an unchanged one is not requested again (#403).</summary>
    private string? _stateShown;

    /// <summary>Collapses a burst of notifications into one render (#399).</summary>
    private readonly RenderCoalescer _renders;

    /// <summary>
    /// What was last handed to each attached property, so an unchanged value is not set again.
    /// </summary>
    /// <remarks>
    /// An attached property's value crosses to WinRT as an <c>IInspectable</c>, so setting one
    /// boxes the string and mints a COM callable wrapper — and the runtime appends every wrapper
    /// to a diagnostics list it never shrinks (#399). None of these values changes more than a
    /// handful of times in a session; all of them were being set several times a second.
    /// </remarks>
    private string? _coastingTooltipShown;
    private bool? _connectTooltipShown;
    private string? _rolloverNameShown;
    private bool _provisionalNameSet;

    /// <summary>What each merit pill was last given, so an unchanged one is left alone (#399).</summary>
    private (string Text, Severity Severity)? _tfomShown;
    private (string Text, Severity Severity)? _ffomShown;

    /// <summary>Creates the page over the application's services.</summary>
    /// <param name="services">The §12 composition root, which owns the receiver.</param>
    public MainPage(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        InitializeComponent();

        _renders = new RenderCoalescer(EnqueueRender);

        // §12: resolved by device key, never constructed here. The Details window binds to the
        // same context, and a page that built its own session would give it a second port.
        _device = services.GetRequiredKeyedService<DeviceContext>(DeviceKeys.Primary);
        _ports = services.GetRequiredService<SerialPortEnumerator>();
        _preferences = services.GetRequiredService<IConnectionPreferenceStore>();
        _logger = services.GetService<ILoggerFactory>()?.CreateLogger("Connection");

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
        GoToStateIfChanged(
            _compact ? "CompactDensity" : ActualHeight < ShortLayoutHeight ? "ShortLayout" : "Normal");

    /// <summary>
    /// Moves to a visual state, but only when it is not the state already (#403).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what #403 turned out to be.</b> <c>GoToState</c> takes the control as its first
    /// argument, so every call hands this page across to WinRT and mints a COM callable wrapper -
    /// and the runtime appends every wrapper to a per-object list it never shrinks. A heap dump
    /// after eight hours found one such list holding <b>65,536 slots for this page</b> against 214
    /// live wrappers in the whole process, which is what the growth was.
    /// </para>
    /// <para>
    /// The call is a no-op inside XAML when the state is already current, and that is exactly what
    /// made it invisible: nothing on screen changed, nothing was slow, and the cost was paid at the
    /// boundary before the framework ever looked at the name. Four earlier rounds of guards all
    /// targeted values being <i>assigned</i> and none of them moved the rate, because the object
    /// being handed over was the page itself, as an argument.
    /// </para>
    /// <para>
    /// Compared against what was last requested rather than asked of the framework, for the same
    /// reason every other guard here does: a read crosses the same boundary a write does.
    /// </para>
    /// </remarks>
    /// <param name="state">The state to move to.</param>
    private void GoToStateIfChanged(string state)
    {
        if (string.Equals(_stateShown, state, StringComparison.Ordinal))
        {
            return;
        }

        _stateShown = state;
        VisualStateManager.GoToState(this, state, useTransitions: false);
    }

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
        ConnectionDialog dialog = new(NewConnectionViewModel()) { XamlRoot = XamlRoot, Log = _logger };

        // Before ShowAsync, which is when the template is applied and the cap taken (#506).
        //
        // The available height is logged beside the cap because the two together are what say which
        // of them is wrong when the dialog still scrolls. Without it, "nothing changed" is all the
        // evidence there is — which is how the first attempt at this passed review.
        double available = XamlRoot?.Size.Height ?? 0;
        double cap = dialog.AllowHeightUpTo(available);

        _logger?.LogInformation(
            "Connection dialog: XamlRoot height {Available}, cap {Cap}{Note}.",
            available,
            cap,
            cap <= DialogHeight.Stock ? " (the stock cap — the window is too short to raise it)" : string.Empty);

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
    /// <b>The command is re-entrancy-guarded, and this used to say it was not (#503).</b> The old
    /// remark said the button being disabled around the dialog was guard enough, "and the dialog is
    /// modal, so a second Ctrl+Shift+C cannot arrive while one is open". Both halves failed on the
    /// same branch: <see cref="MainViewModel.CanConnect"/> is false while the session is
    /// <see cref="ConnectionStatus.Connecting"/>, so a press mid-connect took the <i>disconnect</i>
    /// branch — which opens no dialog, so there is no modality, and never reached the line that
    /// disables the button. And <c>IsEnabled</c> never guarded the accelerator in the first place,
    /// because that calls this method directly without consulting it.
    /// </para>
    /// <para>
    /// So the guard is a field rather than a control's state: it is the only form that covers both
    /// branches and both entry points, including compact mode, where §9.6.2 collapses the button
    /// entirely and the accelerator is the only route left.
    /// </para>
    /// <para>
    /// <b>Nothing here may throw to the caller.</b> One route in is an <c>async void</c> handler and
    /// the other is a discarded task on the accelerator, so an escaping exception is an unhandled
    /// one or an unobserved one — <c>STATUS_STOWED_EXCEPTION</c> either way, which is a process
    /// death with no message. Logged and swallowed is the correct trade for a command whose whole
    /// job is to open or close a link: the medallion goes on reporting whatever the session actually
    /// did, which is more use to the user than a crash.
    /// </para>
    /// </remarks>
    public async Task ToggleConnectionAsync()
    {
        if (_toggling)
        {
            return;
        }

        _toggling = true;

        // Harmless when the footer is collapsed (§9.6.2) — and useless there too, which is why it is
        // no longer the guard.
        ConnectButton.IsEnabled = false;
        try
        {
            if (!_model.CanConnect)
            {
                await _device.Poller.StopAsync();
                await _device.Session.DisconnectAsync();
                return;
            }

            await ShowConnectionDialogAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Connect/disconnect failed from the main window.");
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            _toggling = false;
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
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => _renders.Request();

    /// <summary>Reopens the coalescer's gate, then renders. Runs on the UI thread.</summary>
    private void RenderCoalesced()
    {
        _renders.Begin();
        Render();
    }

    /// <summary>Hands the dispatcher a fresh handler for one coalesced render.</summary>
    /// <remarks>
    /// <para>
    /// <b>A new delegate per hop, deliberately.</b> <c>TryEnqueue</c> mints a COM callable wrapper
    /// for the handler on every call - it does not reuse one by delegate identity - and the runtime
    /// records each wrapper in a list hung off the wrapped object by a dependent handle. Hand it the
    /// same instance every time and that list has an owner that never dies, so it grows for the life
    /// of the process: at about 2.6 renders a second it reached 8,192 slots in 38 minutes, and it was
    /// every byte of what remained of the leak after #399 (#403).
    /// </para>
    /// <para>
    /// A fresh handler gives each wrapper a list that dies with it, for one gen0 allocation per
    /// render. The field this replaced was introduced as the #399 fix on the guess that the wrapper
    /// was cached by delegate identity. It is not - so caching the delegate never reduced the
    /// minting, and did nothing but give the record an immortal owner. <b>Do not turn this back into
    /// a field.</b>
    /// </para>
    /// <para>
    /// <c>new DispatcherQueueHandler(...)</c> rather than a method group or a lambda: a delegate
    /// creation expression is specified to produce a fresh instance, where the other two forms are
    /// merely uncached by the compiler we happen to build with today.
    /// </para>
    /// </remarks>
    private bool EnqueueRender() =>
        DispatcherQueue.TryEnqueue(new DispatcherQueueHandler(RenderCoalesced));

    /// <summary>
    /// The installed version, for the §10.3 footer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read once, not per render.</b> This footer is rewritten on every notification - at least
    /// once a second - and <c>Package.Current</c> is a COM call. The version cannot change while
    /// the process runs, so it is resolved when the class is first touched and reused.
    /// </para>
    /// <para>
    /// From <c>Package.Current.Id.Version</c>, which is the manifest's own number and therefore the
    /// one the release tag had to match (§6.3). An assembly version would be a second number that
    /// could drift from it. The F1 help footer shows the same value the same way; this is the copy
    /// somebody can read without opening anything.
    /// </para>
    /// </remarks>
    private static readonly string PackageVersionText = FormatPackageVersion();

    private static string FormatPackageVersion()
    {
        PackageVersion version = Package.Current.Id.Version;
        return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
    }

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

        // An attached property takes its value as object, so the string is boxed into an
        // IInspectable and every set mints a COM wrapper the runtime never lets go of (#399).
        // This window renders for the life of the process, so it is the worst place to pay it.
        if (_coastingTooltipShown != _model.CoastingTooltip)
        {
            _coastingTooltipShown = _model.CoastingTooltip;
            ToolTipService.SetToolTip(CoastingPill, _model.CoastingTooltip);
        }

        Satellites.Value = _model.SatelliteCount;

        TimeInterval.Value = _model.TimeIntervalNanoseconds;

        // A READING THE FAMILY CANNOT PRODUCE IS ABSENT, NOT BLANK (#456). §9.11's em dash means
        // "not arrived yet"; for a talker there is no disciplined oscillator, so these three never
        // arrive and the dash never fills. §10.3's wireframe is written around this window's fixed
        // shape, and departing from it for such a receiver is deliberate - a dash that will never
        // fill is worse over the weeks §9.1 designs for than a shorter window.
        //
        // The rows are Auto-height, so a collapsed child costs no space rather than leaving a gap
        // where the readout was.
        TimeInterval.Visibility = _model.ShowsTimeInterval ? Visibility.Visible : Visibility.Collapsed;
        TfomPill.Visibility = _model.ShowsTfom ? Visibility.Visible : Visibility.Collapsed;
        FfomPill.Visibility = _model.ShowsFfom ? Visibility.Visible : Visibility.Collapsed;
        MeritRow.Visibility = _model.ShowsAnyMerit ? Visibility.Visible : Visibility.Collapsed;

        RenderMerit(TfomPill, "TFOM", _model.Tfom, ref _tfomShown);
        RenderMerit(FfomPill, "FFOM", _model.Ffom, ref _ffomShown);

        RenderClock();

        FooterText.Text = string.IsNullOrEmpty(_model.PortDescription)
            ? $"{PackageVersionText} · {_model.AgeDescription}"
            : $"{PackageVersionText} · {_model.PortDescription} · {_model.AgeDescription}";

        // The word is set here rather than in the visual state, because a Setter can only assign a
        // literal and this one comes from the same place the severity does — one switch in
        // Staleness, so the pill's three channels cannot get out of step with each other.
        FooterStalenessPill.Text = Staleness.LabelOf(_model.AgeSeverity) ?? string.Empty;

        // ONLY WHEN THE STATE CHANGES, AND THIS IS #403's CAUSE (see _stateShown). The staleness
        // state changes when a poll goes late; this method runs several times a second.
        GoToStateIfChanged(_model.AgeSeverity switch
        {
            Severity.Critical => "AgeCritical",
            Severity.Caution => "AgeCaution",
            _ => "AgeFresh",
        });

        // The label and its tooltip move together. The tooltip carries the accelerator, which is
        // registered on the window's content root rather than on this button (§9.6.2 collapses the
        // footer in compact mode), so nothing would otherwise tell a user the shortcut exists —
        // the Details button beside it has said so all along, and this one said nothing (#319).
        // Two possible strings, so both change only when the button's sense does (#399, #403).
        // Content is object-typed, so assigning it boxes the string and mints a COM wrapper exactly
        // as the tooltip does - it sat outside this guard until the #403 soak found it still doing
        // so several times a second.
        if (_connectTooltipShown != _model.CanConnect)
        {
            _connectTooltipShown = _model.CanConnect;
            ConnectButton.Content = _model.CanConnect ? "Connect" : "Disconnect";
            ToolTipService.SetToolTip(
                ConnectButton,
                _model.CanConnect
                    ? "Connect to a receiver (Ctrl+Shift+C)"
                    : "Disconnect from the receiver (Ctrl+Shift+C)");
        }

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
    /// <remarks>
    /// <para>
    /// Assigns only what changed (#399). A dependency property takes its value as an
    /// <c>IInspectable</c>, so setting one boxes the value and mints a COM callable wrapper whether
    /// or not the value differs — and a figure of merit changes far more slowly than this is
    /// called. The comparison is against what was last written rather than against the property
    /// read back, because a read crosses the same boundary the write does.
    /// </para>
    /// </remarks>
    private static void RenderMerit(
        SeverityPill pill, string label, int? value, ref (string Text, Severity Severity)? shown)
    {
        string text = value is int merit ? $"{label} {merit}" : $"{label} —";
        Severity severity = value switch
        {
            null => Severity.Neutral,
            <= 3 => Severity.Success,
            <= 6 => Severity.Caution,
            _ => Severity.Critical,
        };

        if (shown is { } was && was.Text == text && was.Severity == severity)
        {
            return;
        }

        shown = (text, severity);
        pill.Text = text;
        pill.Severity = severity;
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
        // Guarded for #399, not for speed: both a ToolTip's Content and SetName take their value
        // as object, so each boxes the string and mints a COM wrapper. The tooltip was outside this
        // guard and the name inside it, which meant half the saving (#403). A rollover explanation
        // changes at most once in a session.
        string rollover = _model.RolloverExplanation ?? string.Empty;
        if (_rolloverNameShown != rollover)
        {
            _rolloverNameShown = rollover;
            RolloverTip.Content = rollover;
            AutomationProperties.SetName(RolloverBadge, rollover);
        }

        // #245. The receiver's own marker for "this is the power-up default, not yet corrected from
        // GPS". It belongs on the primary window rather than only in Details, because §10.3's whole
        // premise is a window somebody leaves running and glances at - and this is the one reading
        // on that surface that can be arbitrarily wrong while looking entirely ordinary.
        const string provisional =
            "Power-up time. The receiver has not yet corrected this from GPS, so it may be wrong by "
            + "any amount until the first satellite is tracked.";

        ProvisionalBadge.Visibility = _model.IsTimeProvisional ? Visibility.Visible : Visibility.Collapsed;
        // A constant, so both are set once rather than several times a second (#399, #403). The
        // tooltip was outside this guard, which is a boxed assignment per render for a string that
        // never changes at all.
        if (!_provisionalNameSet)
        {
            _provisionalNameSet = true;
            ProvisionalTip.Content = provisional;
            AutomationProperties.SetName(ProvisionalBadge, provisional);
        }
    }
}
