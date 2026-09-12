using System.ComponentModel;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using Microsoft.Extensions.DependencyInjection;

using Windows.ApplicationModel;
using Windows.Storage;
using Windows.System;

using WinZ3805A.Controls;
using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;
using WinZ3805A.Device.Parsing;
using WinZ3805A.Device.Models;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Views;

/// <summary>
/// The §10.9 Diagnostics page.
/// </summary>
public sealed partial class DiagnosticsPage : Page, ICsvExportSource
{
    private DiagnosticsViewModel? _model;
    private DeviceContext? _device;
    private FileLoggerProvider? _logProvider;

    /// <summary>Where the log really is, once MSIX's redirection has been resolved.</summary>
    private string? _logFolder;
    private CommandInvoker? _invoker;
    private SelfTestViewModel? _selfTest;
    private CancellationTokenSource? _reading;
    private bool _ready;

    /// <summary>§8.5's rows for this driver — the SmartClock family's six — or empty before navigation.</summary>
    private IReadOnlyList<ExperimentalQueryRow> _experimental = [];

    /// <summary>Creates the page.</summary>
    /// <summary>
    /// Drives §9.11's loading ladder, which is a function of elapsed time and so needs a clock.
    /// </summary>
    /// <remarks>
    /// A ticking timer rather than two one-shots, because <c>LoadingIndicators.For</c> takes the
    /// elapsed time and returns the whole answer: one tick asks it again and applies whatever comes
    /// back. Two one-shot timers would encode the same thresholds a second time, in the place most
    /// likely to drift from them.
    /// </remarks>
    /// <summary>What the connected receiver's driver offers (#304).</summary>
    private bool _canSelfTest;
    private bool _canClearLog;
    private bool _canRefresh;
    private bool _canReadErrors;

    private readonly DispatcherTimer _loadingTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    /// <summary>The self-test button's label as last written, so an unchanged one is skipped (#403).</summary>
    private string? _runLabelShown;

    /// <summary>Collapses a burst of notifications into one render (#399).</summary>
    private readonly RenderCoalescer _renders;

    /// <summary>When the read in flight started, for the ladder above.</summary>
    private DateTimeOffset? _readingSince;

    public DiagnosticsPage()
    {
        InitializeComponent();

        _renders = new RenderCoalescer(EnqueueRender);

        _loadingTimer.Tick += (_, _) => ApplyLoadingIndicator();

        Unloaded += (_, _) => Detach();
    }

    /// <summary>Keeps the parse-warnings card following the sweeps while the page is open.</summary>
    /// <summary>
    /// Puts §9.11's loading ladder on screen: nothing, then the ring, then the ring and skeleton.
    /// </summary>
    /// <remarks>
    /// <b>Nothing under 500 ms is the half that is easy to skip.</b> The ring used to be bound
    /// straight to <c>IsReading</c>, so a read that finished quickly — which is most of them — put a
    /// spinner on screen and took it away inside a fifth of a second. That reads as a glitch rather
    /// than as progress, and it draws the eye to a card with nothing to say.
    /// </remarks>
    private void ApplyLoadingIndicator()
    {
        bool reading = _model?.IsReading == true;
        TimeSpan elapsed = reading && _readingSince is DateTimeOffset since
            ? (_device?.TimeProvider.GetUtcNow() ?? DateTimeOffset.UtcNow) - since
            : TimeSpan.Zero;

        LoadingIndicator indicator = LoadingIndicators.For(reading, elapsed);

        ReadingRing.IsActive = indicator is LoadingIndicator.Ring or LoadingIndicator.Skeleton;
        LogSkeleton.Visibility = indicator == LoadingIndicator.Skeleton
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnStoreChanged(object? sender, PropertyChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_model is DiagnosticsViewModel model && _device is DeviceContext device)
            {
                model.ParseWarnings = device.Store.Status?.ParseWarnings ?? [];
            }
        });

    /// <inheritdoc />
    /// <summary>Undoes everything <see cref="OnNavigatedTo"/> subscribed to (#388).</summary>
    /// <remarks>
    /// Idempotent: both <c>Unloaded</c> and <see cref="OnNavigatedFrom"/> call it, and neither is
    /// reliable alone. Disposing the model is the half that matters - it is what lets go of the
    /// store, which outlives every page and was keeping this one alive after it left the screen.
    /// </remarks>
    private void Detach()
    {
        // A STATIC EVENT HOLDS ITS SUBSCRIBERS FOR THE LIFE OF THE PROCESS (#400). This page joined
        // SettingsPage.AdvancedChanged with an instance handler and never left, so one page per
        // visit was pinned by the event itself - no rendering, no CPU, invisible to #388's fix.
        SettingsPage.AdvancedChanged -= OnAdvancedChanged;

        // Rooted by the dispatcher while it runs, and its Tick captures this page.
        _loadingTimer.Stop();

        if (_device is DeviceContext device)
        {
            device.Session.StatusChanged -= OnStatusChanged;
            device.Store.PropertyChanged -= OnStoreChanged;
        }

        // No Dispose here, unlike the other pages: DiagnosticsViewModel does not subscribe to the
        // store itself. This page does, directly, and that subscription is removed above.
        if (_model is DiagnosticsViewModel model)
        {
            model.PropertyChanged -= OnModelChanged;
            _model = null;
        }
    }

    /// <summary>Renders on a model notification. Named so <see cref="Detach"/> can remove it (#388).</summary>
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => _renders.Request();

    /// <summary>True while a render is writing the lamp switch, so its Toggled event is ignored.</summary>
    /// <remarks>
    /// <b>Setting <c>IsOn</c> raises <c>Toggled</c>, which cannot be told from a click.</b> Without
    /// this, every render that reflected the receiver's state would send the state straight back to
    /// the receiver — a write a second on a node that costs a second to answer (#440).
    /// </remarks>
    /// <summary>True while the code is writing a switch, so its own write is not read as a click.</summary>
    private bool _writingEnabledSwitch;
    private bool _writingActiveSwitch;

    /// <summary>Whether each switch has been caught up with the receiver this connection.</summary>
    private bool _enabledLampSeeded;
    private bool _activeLampSeeded;

    /// <summary>
    /// Puts both front-panel lamps' real state on their switches, without sending anything.
    /// </summary>
    /// <remarks>
    /// <b>Two lamps since #462, and they mean different things.</b> Enabled is the application's —
    /// lit while it holds the link. Active is the receiver's — lit while it is locked to GPS. Both
    /// are here for the same reason #440 gave: a lamp left lit by a crash needs a person to be able
    /// to put it out, and that escape hatch has to exist for each one that can be left.
    /// </remarks>
    private void RenderLamps()
    {
        bool connected = _device?.Session.Status == ConnectionStatus.Connected;

        RenderLamp(
            EnabledLampSwitch,
            EnabledLampCaption,
            _device?.Lamp.IsSupported == true,
            connected,
            "The application's own front-panel lamp: lit while it holds this link. Use this to put it back if the application was closed while it was lit. It takes about a second to answer, the receiver servicing the lamp on its own once-a-second tick.",
            ref _enabledLampSeeded,
            SeedEnabledLampAsync);

        RenderLamp(
            ActiveLampSwitch,
            ActiveLampCaption,
            _device?.LockLamp.IsSupported == true,
            connected,
            "The receiver's front-panel lamp, followed to its lock state while the front-panel setting is on: lit when it is locked to GPS. Setting it here takes it over, and it stops following until you reconnect.",
            ref _activeLampSeeded,
            SeedActiveLampAsync);
    }

    /// <summary>One lamp's switch and caption, so the two cannot drift apart.</summary>
    private void RenderLamp(
        ToggleSwitch toggle,
        TextBlock caption,
        bool supported,
        bool connected,
        string description,
        ref bool seeded,
        Func<Task> seed)
    {
        toggle.IsEnabled = supported && connected;
        caption.Text = supported
            ? description
            : Capability.NotOffered(_device?.Driver, "the front-panel lamp");

        if (!connected || !supported)
        {
            seeded = false;
            return;
        }

        if (!seeded)
        {
            seeded = true;
            _ = seed();
        }
    }

    /// <summary>
    /// Asks the receiver what a lamp is doing and puts that on its switch (#464).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The switch used to report its own writes rather than the lamp</b>, so an application that
    /// closed while the lamp was lit came back showing <c>off</c> over a lit panel — and §16's
    /// documented escape hatch, "use the Diagnostics toggle to put it out", took two clicks instead
    /// of one because the first merely caught the control up with reality. §9.11 gives this control
    /// no success bar precisely because the switch's own position is the feedback, and a position
    /// that disagrees with the instrument is not feedback.
    /// </para>
    /// <para>
    /// <b>Once per connection, not once per render.</b> Rendering happens on every reading, and a
    /// query on that path would put the read on the poll loop. The read itself is cheap — ~30 ms
    /// against the ~900 ms a <c>:LED:</c> <i>write</i> costs on the receiver's 1 Hz tick — but cheap
    /// once a second is still once a second.
    /// </para>
    /// <para>
    /// Writing <c>IsOn</c> raises <c>Toggled</c>, which cannot be told from a click, so the write is
    /// made under a guard. Without it, seeding the switch from the receiver would send the value
    /// straight back to it.
    /// </para>
    /// </remarks>
    private Task SeedEnabledLampAsync() =>
        SeedLampAsync(
            EnabledLampSwitch,
            () => _device?.Lamp.ReadAsync() ?? Task.FromResult<bool?>(null),
            v => _writingEnabledSwitch = v,
            v => _enabledLampSeeded = v);

    /// <inheritdoc cref="SeedEnabledLampAsync"/>
    private Task SeedActiveLampAsync() =>
        SeedLampAsync(
            ActiveLampSwitch,
            () => _device?.LockLamp.ReadAsync() ?? Task.FromResult<bool?>(null),
            v => _writingActiveSwitch = v,
            v => _activeLampSeeded = v);

    private static async Task SeedLampAsync(
        ToggleSwitch toggle,
        Func<Task<bool?>> read,
        Action<bool> setWriting,
        Action<bool> setSeeded)
    {
        try
        {
            if (await read() is not bool lit)
            {
                // Unreadable, so the switch is left as it is rather than guessing — §11.1's rule
                // applied to a control: never show a value we do not have.
                setSeeded(false);
                return;
            }

            if (toggle.IsOn != lit)
            {
                setWriting(true);
                toggle.IsOn = lit;
                setWriting(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // A lamp that would not answer is not a page that failed to render.
            setSeeded(false);
        }
    }

    /// <summary>
    /// Sets the receiver's Enabled lamp from the switch (#440, #462).
    /// </summary>
    /// <remarks>
    /// No confirmation and no success bar: the write is tier S, and §9.11 gives a safe setter no UI
    /// at all — the switch's own position is the feedback. A write that fails puts the switch back
    /// where it was, which is the only honest thing a control over hardware can do.
    /// </remarks>
    private async void OnEnabledLampToggled(object sender, RoutedEventArgs e)
    {
        if (_writingEnabledSwitch || !_ready || _device is not DeviceContext device)
        {
            return;
        }

        await ToggleLampAsync(
            EnabledLampSwitch,
            on => device.Lamp.SetManuallyAsync(on),
            v => _writingEnabledSwitch = v);
    }

    /// <summary>
    /// Sets the receiver's Active lamp from the switch, taking it over from the lock state (#462).
    /// </summary>
    /// <remarks>
    /// Taking it over is the point rather than a side effect: once a person has set this lamp
    /// themselves, theirs is the value, and a lock transition a minute later must not undo what they
    /// just asked for — which would read as the control not working.
    /// </remarks>
    private async void OnActiveLampToggled(object sender, RoutedEventArgs e)
    {
        if (_writingActiveSwitch || !_ready || _device is not DeviceContext device)
        {
            return;
        }

        await ToggleLampAsync(
            ActiveLampSwitch,
            on => device.LockLamp.SetManuallyAsync(on),
            v => _writingActiveSwitch = v);
    }

    private async Task ToggleLampAsync(
        ToggleSwitch toggle,
        Func<bool, Task<bool>> set,
        Action<bool> setWriting)
    {
        bool wanted = toggle.IsOn;
        toggle.IsEnabled = false;

        try
        {
            if (!await set(wanted))
            {
                setWriting(true);
                toggle.IsOn = !wanted;
                setWriting(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            setWriting(true);
            toggle.IsOn = !wanted;
            setWriting(false);
        }
        finally
        {
            RenderLamps();
        }
    }

    /// <summary>Reopens the coalescer's gate, then renders. Runs on the UI thread.</summary>
    private void RenderCoalesced()
    {
        _renders.Begin();
        Render();
    }

    /// <summary>Hands the dispatcher a fresh handler for one coalesced render.</summary>
    /// <remarks>
    /// A fresh delegate per hop rather than a cached field, which is load-bearing rather than
    /// wasteful - see <see cref="MainPage"/> for the wrapper accounting that makes it so (#403).
    /// </remarks>
    private bool EnqueueRender() =>
        DispatcherQueue.TryEnqueue(
            new Microsoft.UI.Dispatching.DispatcherQueueHandler(RenderCoalesced));

    /// <inheritdoc />
    /// <remarks>
    /// <b>The Frame's hook, not Unloaded (#388).</b> Everything this page subscribed to in
    /// <see cref="OnNavigatedTo"/> is undone here, and the model is disposed so it lets go of the
    /// store. Unloaded was doing half the job and could not do the other half: the store outlives
    /// every page, so store -> model -> page kept the page alive and rendering on every reading
    /// after it left the screen, once per visit.
    /// </remarks>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Detach();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e?.Parameter is not DeviceContext device)
        {
            return;
        }

        _device = device;
        _invoker = new CommandInvoker(device.Session);

        _selfTest = new SelfTestViewModel(device.TimeProvider);
        SubsystemPicker.ItemsSource = _selfTest.Subsystems;
        SubsystemPicker.SelectedIndex = 0;
        SelfTestRows.ItemsSource = _selfTest.Rows;

        // Application-scoped, so it comes from the composition root rather than the device context.
        // Null when logging failed to start, which the card handles by disabling its button rather
        // than by hiding: a missing log is worth noticing.
        _logProvider = App.Services?.GetService<FileLoggerProvider>();
        _logFolder = ResolveLogFolder(_logProvider);
        _model = new DiagnosticsViewModel(device.Session) { ParseWarnings = device.Store.Status?.ParseWarnings ?? [] };
        _model.PropertyChanged += OnModelChanged;
        device.Session.StatusChanged += OnStatusChanged;

        // Parse warnings belong to a status screen, so they arrive with each full sweep rather than
        // with a query. Following the store keeps the card current while the page is open, which is
        // the state someone is in when they are reading it to find out what a firmware revision
        // broke.
        device.Store.PropertyChanged += OnStoreChanged;

        BindDriver();

        // §9.7.4's right-click layer, on the log's CARD and not on its rows.
        //
        // Measured, not assumed: a TextBlock with IsTextSelectionEnabled carries its own selection
        // flyout, and right-clicking a log entry opens that one — a ContextFlyout on the ItemsControl
        // above it never appears. Which is the right outcome rather than a defeat. On a row you want
        // that row's text, and on the card around it you want the table; the two menus divide the
        // surface between them instead of one shadowing the other.
        //
        // The card and not the whole page, because §9.7.4's "copy as CSV on tables" is a claim about
        // a specific table — this page has five ItemsControls and BuildCsv builds exactly one of them.
        CopyMenu.AttachCsv(LogCard, this);

        SettingsPage.AdvancedChanged += OnAdvancedChanged;

        _ready = true;
        Render();

        // The log and the self-test result are safe to read on arrival. The error queue is not —
        // reading it empties it — so that one waits for the button.
        await RefreshAsync();
    }

    private void OnStatusChanged(object? sender, ConnectionStatusChanged e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            _model?.RaiseAll();

            if (e?.Status == ConnectionStatus.Connected)
            {
                // The receiver on the port can have been swapped while the link was down, so the
                // session re-selects a driver on every connect (#287) and this page's answer to
                // "what may I offer" has to be asked again rather than kept from navigation (#304).
                BindDriver();
                Render();
            }
        });

    /// <summary>
    /// Re-reads everything this page takes from the connected receiver's driver (#304).
    /// </summary>
    /// <remarks>
    /// <para>
    /// §10.9's two tier C commands. A talker offers neither, so the controls are disabled with a
    /// reason rather than throwing when they are clicked.
    /// </para>
    /// <para>
    /// §8.5's card is rebuilt here too, because an undocumented node is a claim about one firmware
    /// family — a row list kept from navigation would offer a talker queries in a language it does
    /// not speak. Rows are created per page rather than shared: each holds its own last answer, and
    /// two pages over one set would show each other's. Rebuilding discards those answers, which is
    /// correct: they came from a receiver that may no longer be on the port.
    /// </para>
    /// </remarks>
    private void BindDriver()
    {
        IReceiverDriver? driver = _device?.Driver;

        _canSelfTest = Capability.Offers(driver, ":DIAG:TEST?");
        _canClearLog = Capability.Offers(driver, ":DIAG:LOG:CLEar");
        _canReadErrors = Capability.Offers(driver, ":SYST:ERR?");

        // ANY rather than ALL, which is the opposite of Offers' rule and deliberate. Refresh is
        // four independent reads filling four separate cards, not one operation that half works:
        // a driver with the log but no self test should still fill the log, and RefreshAsync
        // already skips a mnemonic its driver lacks. What it must not do is stay enabled for a
        // family that has none of them, which is where it raised a fault for a receiver that had
        // done nothing wrong (#435).
        _canRefresh =
            Capability.Offers(driver, ":DIAG:TEST:RES?") ||
            Capability.Offers(driver, ":DIAG:LOG:COUN?") ||
            Capability.Offers(driver, ":DIAG:LIF:COUN?") ||
            Capability.Offers(driver, ":DIAG:IDEN:GPS?") ||
            Capability.Offers(driver, ":DIAG:LOG:READ:ALL?");

        if (driver is not null)
        {
            _experimental = ExperimentalQueries.Create(driver);
            ExperimentalRows.ItemsSource = _experimental;
        }

        ApplyExperimentalVisibility();
    }

    private void OnAdvancedChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(ApplyExperimentalVisibility);

    /// <summary>
    /// Shows or hides §8.5's card to match Settings → Advanced.
    /// </summary>
    /// <remarks>
    /// Collapsed rather than disabled. A card of buttons nobody opted into is not something to grey
    /// out — it is something that should not be on the page, and collapsing it also keeps it out of
    /// the tab order without <c>IsEnabled</c> being the only thing standing between a keyboard user
    /// and six undocumented queries.
    /// </remarks>
    private void ApplyExperimentalVisibility()
    {
        bool enabled = App.Services?.GetService<IAdvancedPreferenceStore>()
            ?.Load().AreExperimentalQueriesEnabled == true;

        ExperimentalCard.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Runs one §8.5 query, on this click and no other trigger.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The command comes from the row, which came from the catalog. There is no string here and no
    /// way to reach a node the catalog does not hold — and §8.4 keeps the set forms of these nodes
    /// out of the catalog permanently, so the opt-in cannot reach them either.
    /// </para>
    /// <para>
    /// <b>Whatever comes back is shown, including an error.</b> §8.5 says results are raw text and
    /// any SCPI error is displayed rather than swallowed. An undocumented node answering E-113 is
    /// the most useful thing this card can tell anyone about that node.
    /// </para>
    /// </remarks>
    private async void OnRunExperimentalClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ExperimentalQueryRow row }
            || _device is not DeviceContext device
            || row.IsBusy)
        {
            return;
        }

        row.IsBusy = true;
        row.IsError = false;

        try
        {
            Transaction transaction = await device.Session
                .ExecuteAsync(row.Command, origin: CommandOrigin.User)
                .ConfigureAwait(true);

            if (transaction.Outcome == TransactionOutcome.TimedOut)
            {
                row.IsError = true;
                row.Result = "No answer within the timeout.";
            }
            else if (transaction.PromptStatus is string status)
            {
                // The receiver rejected it, which for an undocumented node is a real answer about
                // that node rather than a failure of this card. E-113 is named because it is the
                // common case and because "undefined header" is not something a user should have to
                // look up: five of §8.5's six answer it on this model, and the specification now
                // records that as an expected result rather than an error.
                row.IsError = true;
                row.Result = status.Contains("113", StringComparison.Ordinal)
                    ? $"{status} — this receiver's firmware does not have that node."
                    : $"The receiver answered {status}.";
            }
            else if (transaction.Lines.Count == 0)
            {
                row.Result = "(no output)";
            }
            else
            {
                row.Result = string.Join(Environment.NewLine, transaction.Lines);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or TransportException)
        {
            row.IsError = true;
            row.Result = exception.Message;
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnReadErrorsClicked(object sender, RoutedEventArgs e)
    {
        if (_model is not DiagnosticsViewModel model)
        {
            return;
        }

        _reading?.Cancel();
        _reading?.Dispose();
        _reading = new CancellationTokenSource();

        await model.ReadErrorQueueAsync(_reading.Token);
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready && _model is DiagnosticsViewModel model)
        {
            model.Filter = FilterBox.Text;
        }
    }

    private async Task RefreshAsync()
    {
        if (_model is not DiagnosticsViewModel model)
        {
            return;
        }

        _reading?.Cancel();
        _reading?.Dispose();
        _reading = new CancellationTokenSource();

        await model.RefreshAsync(_reading.Token);
    }

    private void Render()
    {
        if (_model is not DiagnosticsViewModel model)
        {
            return;
        }

        // What the receiver holds from whenever it last ran a test - possibly before this
        // application started, possibly at the factory. Kept separate from the rows below, which
        // are only what this session measured, because the two make different claims.
        LastReadText.Text = $"The receiver reports its last stored result as {model.SelfTestResultText}.";

        ParseWarningSummaryText.Text = model.ParseWarningSummary;
        if (!ReferenceEquals(ParseWarningItems.ItemsSource, model.ParseWarnings))
        {
            ParseWarningItems.ItemsSource = model.ParseWarnings;
        }

        PowerOnHoursText.Text = model.PowerOnHoursText;
        PowerOnHoursCaption.Text = model.PowerOnHoursCaption;

        // One line per populated field, or a single em dash when there are none. The dash is a
        // separate TextBlock rather than a placeholder row so that an empty list renders §11.1's
        // "not read" mark and never an empty card (#443).
        GpsEngineRows.ItemsSource = model.GpsEngineFields;
        GpsEngineRows.Visibility = model.GpsEngineFields.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GpsEngineEmptyText.Visibility = model.GpsEngineFields.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        GpsEngineEmptyText.Text = model.GpsEngineText;
        GpsEngineCaption.Text = model.GpsEngineCaption;

        RenderLamps();

        if (_selfTest is SelfTestViewModel selfTest)
        {
            // Content is object-typed, so an unchanged label still boxes and mints a COM wrapper
            // (#403). Compared against what was last written rather than read back, because a read
            // crosses the same boundary a write does.
            if (!string.Equals(_runLabelShown, selfTest.RunLabel, StringComparison.Ordinal))
            {
                _runLabelShown = selfTest.RunLabel;
                RunTestButton.Content = selfTest.RunLabel;
            }
            // Capability first, then state (#304).
            RunTestButton.IsEnabled = _canSelfTest && model.CanRead && !selfTest.IsRunning;
            SubsystemPicker.IsEnabled = _canSelfTest && !selfTest.IsRunning;
            SelfTestSummary.Text = selfTest.Summary;

            SelfTestUnsupportedText.Text = _canSelfTest
                ? string.Empty
                : Capability.NotOffered(_device?.Driver, "a self test");
            SelfTestUnsupportedText.Visibility =
                _canSelfTest ? Visibility.Collapsed : Visibility.Visible;
        }
        LogHeaderText.Text = model.LogHeaderText;

        // Only when the rows have actually changed, as SatellitesPage and StatusRegistersPage
        // already do: reassigning ItemsSource boxes the list and rebuilds every container (#403).
        if (!ReferenceEquals(LogRows.ItemsSource, model.Filtered))
        {
            LogRows.ItemsSource = model.Filtered;
        }
        LogEmptyText.Text = model.LogEmptyText;
        LogEmptyText.Visibility = model.Filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ErrorSummaryText.Text = model.ErrorSummaryText;
        ErrorRows.ItemsSource = model.Errors;

        // §9.11's loading ladder. Render is called on property changes and nothing changes at the
        // 500 ms or 2 s marks, so the timer below is what makes those thresholds exist at all.
        if (model.IsReading)
        {
            _readingSince ??= _device?.TimeProvider.GetUtcNow() ?? DateTimeOffset.UtcNow;
            _loadingTimer.Start();
        }
        else
        {
            _readingSince = null;
            _loadingTimer.Stop();
        }

        ApplyLoadingIndicator();

        // Capability first, then state (#304) — these two were state only, so with a talker
        // connected they sat enabled and inviting, and pressing either raised a fault InfoBar for a
        // receiver that had done nothing wrong (#435). Neither needs its own sentence: every card
        // they would have filled now says on its own face why it is empty, which is closer to the
        // control than a caption on the button would be.
        RefreshButton.IsEnabled = _canRefresh && model.CanRead;
        ReadErrorsButton.IsEnabled = _canReadErrors && model.CanRead;
        ClearLogButton.IsEnabled = _canClearLog && model.CanRead;

        ClearLogUnsupportedText.Text = _canClearLog
            ? string.Empty
            : Capability.NotOffered(_device?.Driver, "clearing the diagnostic log");
        ClearLogUnsupportedText.Visibility =
            _canClearLog ? Visibility.Collapsed : Visibility.Visible;

        // Not model.CanRead: exporting what is already on screen does not need the receiver, and a
        // user whose link has just dropped is exactly the one who wants the log they were reading
        // when it happened.
        ExportLogButton.IsEnabled = CanExport;
        ExportAvailabilityChanged?.Invoke(this, EventArgs.Empty);

        LogPathText.Text = _logFolder ?? _logProvider?.Path ?? string.Empty;
        ShowLogFolderButton.IsEnabled = _logProvider is not null;

        FaultBar.IsOpen = model.Fault is not null;
        FaultBar.Message = model.Fault ?? string.Empty;

        // A capability gap is not a fault (#435). The severity and the title both change, because
        // an error icon over "this receiver has no self test" says the application is broken when
        // it is working exactly as designed.
        FaultBar.Severity = model.FaultIsUnsupported
            ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational
            : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
        FaultBar.Title = model.FaultIsUnsupported
            ? "Not available on this receiver"
            : "Could not read from the receiver";
    }

    /// <inheritdoc />
    public event EventHandler? ExportAvailabilityChanged;

    /// <inheritdoc />
    public bool CanExport => _model?.Filtered.Count > 0;

    /// <inheritdoc />
    public string SuggestedFileName =>
        DiagnosticLogCsv.SuggestedFileName(_device?.TimeProvider ?? TimeProvider.System);

    /// <inheritdoc />
    /// <remarks>
    /// The <i>filtered</i> list, not the whole log — see <see cref="DiagnosticLogCsv.From"/> for
    /// why, and note the caption under the card tells the user so before they press it.
    /// </remarks>
    /// <remarks>
    /// The rollover epoch count comes from the current status rather than from the entries: an
    /// entry carries a date and nothing to check it against, while the status screen is where §7.4's
    /// comparison against the host clock is made. See <see cref="DiagnosticLogCsv.From"/>.
    /// </remarks>
    public CsvDocument? BuildCsv() =>
        DiagnosticLogCsv.From(_model?.Filtered, _device?.Store.Status?.WeekRolloverEpochs ?? 0);

    private void OnExportLogClicked(object sender, RoutedEventArgs e) =>
        DetailsWindow.ExportFrom(this, XamlRoot);

    /// <summary>
    /// Opens the folder holding this application's own log (#127).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The folder rather than the file. Rotation means the interesting entries may be in
    /// <c>app.log.1</c> rather than <c>app.log</c>, and a user chasing something that happened
    /// yesterday wants to see all of them; opening the newest file directly would hide the rest.
    /// </para>
    /// <para>
    /// <c>Launcher</c> rather than <c>Process.Start</c>: this is a packaged application, and
    /// shelling out to explorer.exe from inside the package is the kind of thing that works in
    /// development and is refused at certification.
    /// </para>
    /// </remarks>
    private async void OnShowLogFolderClicked(object sender, RoutedEventArgs e)
    {
        if (_logProvider is not FileLoggerProvider provider || _logFolder is not string folder)
        {
            return;
        }

        // Flushed first, so what the user is about to read includes the line they just caused.
        provider.Flush();

        try
        {
            await Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(folder));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Nothing to escalate: the resolved path is on screen beside the button, so a user
            // whose shell will not open it can still get there by hand.
        }
    }

    /// <summary>
    /// Works out where the log actually is on disk, as opposed to where this process asked for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>MSIX redirects <c>LocalApplicationData</c> into the package's writable store.</b>
    /// <c>Environment.GetFolderPath</c> returns a path that works from inside the package and does
    /// not exist outside it, so showing it to the user is worse than showing nothing: it looks like
    /// an address, and pasting it into Explorer finds nothing. Measured on this machine — the
    /// application asks for <c>…\Local\WinZ3805A\logs</c> and the file lands in
    /// <c>…\Local\Packages\{family}\LocalCache\Local\WinZ3805A\logs</c>.
    /// </para>
    /// <para>
    /// <b><c>StorageFolder.GetFolderFromPathAsync</c> does not help</b>, which was the first thing
    /// tried: it reports the same virtual path back, because the redirection is consistent from
    /// inside the package. <c>ApplicationData.Current.LocalCacheFolder</c> would answer correctly
    /// and terminate the process doing it, so the real path is composed from the package family
    /// name — which <c>Package.Current</c> already supplies safely elsewhere in this application.
    /// </para>
    /// <para>
    /// Composed rather than discovered, so it is checked before being shown: if the folder is not
    /// there, the asked-for path is returned instead. The button is unaffected either way —
    /// <c>Launcher</c> resolves the redirection itself, so only the text beside it was ever wrong.
    /// </para>
    /// </remarks>
    private static string? ResolveLogFolder(FileLoggerProvider? provider)
    {
        if (provider is null || Path.GetDirectoryName(provider.Path) is not string asked)
        {
            return null;
        }

        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!asked.StartsWith(local, StringComparison.OrdinalIgnoreCase))
            {
                return asked;
            }

            string redirected = Path.Combine(
                local,
                "Packages",
                Package.Current.Id.FamilyName,
                "LocalCache",
                "Local",
                asked[local.Length..].TrimStart(Path.DirectorySeparatorChar));

            return Directory.Exists(redirected) ? redirected : asked;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return asked;
        }
    }

    /// <summary>Keeps the run label naming whichever subsystem is selected.</summary>
    private void OnSubsystemChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selfTest is SelfTestViewModel selfTest &&
            SubsystemPicker.SelectedItem is SelfTestSubsystem chosen)
        {
            selfTest.Selected = chosen;
            RunTestButton.Content = selfTest.RunLabel;
        }
    }

    /// <summary>
    /// Runs one subsystem's diagnostic, or the receiver's own sweep (#53).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tier C, and the confirmation says the receiver will <b>drop out of lock and re-acquire</b>.
    /// That wording was measured rather than assumed: running the twelve tests took the receiver
    /// from LOCK/TFOM 3 to POW/TFOM 9, re-acquiring over several minutes. §8.3 previously said
    /// "may briefly interrupt normal operation", which reads as a second or two.
    /// </para>
    /// <para>
    /// The result is read back from <c>:DIAG:TEST:RES?</c> rather than from the run's own reply.
    /// Both carry the same code, but the read-back also names the subsystem the receiver believes
    /// it tested — so a mismatch between what was asked for and what ran is visible rather than
    /// assumed away.
    /// </para>
    /// </remarks>
    private async void OnRunTestClicked(object sender, RoutedEventArgs e)
    {
        if (_invoker is not CommandInvoker invoker ||
            _model is not DiagnosticsViewModel model ||
            _selfTest is not SelfTestViewModel selfTest ||
            _device is not DeviceContext device ||
            !model.CanRead)
        {
            return;
        }

        selfTest.SetRunning(true);
        SelfTestOutcome.Clear();
        Render();

        try
        {
            CommandOutcome? outcome = await CommandConfirmation.RunAsync(
                XamlRoot,
                invoker,
                // §8.3's subsystem diagnostic, which costs the receiver its lock (#53).
                CommandConfirmation.Require(device.Driver, ":DIAG:TEST?"),
                selfTest.Selected.Keyword,
                selfTest.Selected.DisplayName);

            SelfTestOutcome.Show(outcome);

            if (outcome is { Succeeded: true })
            {
                if (selfTest.Selected.Keyword == SelfTestSubsystem.All.Keyword)
                {
                    // THE SWEEP'S OWN REPLY, not :DIAG:TEST:RES?. The manual gives the parameter as
                    // "ALL returns test information for all of the tests" and the response as a
                    // single value where zero is a pass — so this answer covers the whole set.
                    // :DIAG:TEST:RES? would name only the last test the sweep finished with, which
                    // is how this card used to run every test and then show twelve dashes.
                    selfTest.RecordSweep(
                        SelfTestResult.ParseRun(outcome.Lines.FirstOrDefault(), SelfTestSubsystem.All));
                }
                else
                {
                    // For one subsystem, :DIAG:TEST:RES? is worth the extra round trip: it echoes
                    // the keyword, so the row is credited against what the receiver says it tested
                    // rather than against what was asked for.
                    string? read = await model.ReadSelfTestResultAsync();
                    selfTest.Record(SelfTestResult.Parse(read));
                }
            }
        }
        finally
        {
            selfTest.SetRunning(false);
            Render();
        }
    }

    /// <summary>
    /// §8.3's log clear. Nothing is re-read afterwards on purpose: the log is now empty, and a
    /// refresh that showed the empty state would look like the read failing.
    /// </summary>
    private async void OnClearLogClicked(object sender, RoutedEventArgs e)
    {
        if (_invoker is not CommandInvoker invoker ||
            _model is not DiagnosticsViewModel model ||
            _device is not DeviceContext device ||
            !model.CanRead)
        {
            return;
        }

        ClearLogButton.IsEnabled = false;
        LogOutcome.Clear();

        // §8.3's log clear — the one other tier C command on this page.
        ScpiCommand clearLog = CommandConfirmation.Require(device.Driver, ":DIAG:LOG:CLEar");

        try
        {
            CommandOutcome? outcome = await CommandConfirmation.RunAsync(XamlRoot, invoker, clearLog);
            LogOutcome.Show(outcome);

            if (outcome is { Succeeded: true })
            {
                model.ForgetLog();
            }
        }
        finally
        {
            Render();
        }
    }
}
