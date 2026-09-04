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

    private readonly DispatcherTimer _loadingTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    /// <summary>
    /// The one handler this page hands the dispatcher, reused for every notification (#399).
    /// </summary>
    /// <remarks>
    /// A field rather than a lambda or a method group so the hop allocates nothing. See
    /// <see cref="MainPage"/> for why that is hygiene and not the fix, and for what the leak
    /// in #399 actually turned out to be.
    /// </remarks>
    private readonly Microsoft.UI.Dispatching.DispatcherQueueHandler _render;

    /// <summary>1 while a render is already queued, so a burst costs one (#399).</summary>
    private int _renderQueued;

    /// <summary>When the read in flight started, for the ladder above.</summary>
    private DateTimeOffset? _readingSince;

    public DiagnosticsPage()
    {
        InitializeComponent();

        _render = () =>
        {
            Interlocked.Exchange(ref _renderQueued, 0);
            Render();
        };

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
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // One hop and one render per burst (#399). The store raises about seven notifications per
        // sweep and Render rewrites everything, so six of them repaint what the seventh is about
        // to - and each repaint marshals boxed values into WinRT, minting a COM wrapper the
        // runtime appends to a list that never shrinks.
        if (Interlocked.Exchange(ref _renderQueued, 1) == 1)
        {
            return;
        }

        if (!DispatcherQueue.TryEnqueue(_render))
        {
            Interlocked.Exchange(ref _renderQueued, 0);
        }
    }

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
        ParseWarningItems.ItemsSource = model.ParseWarnings;

        PowerOnHoursText.Text = model.PowerOnHoursText;

        if (_selfTest is SelfTestViewModel selfTest)
        {
            RunTestButton.Content = selfTest.RunLabel;
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

        LogRows.ItemsSource = model.Filtered;
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
        RefreshButton.IsEnabled = model.CanRead;
        ReadErrorsButton.IsEnabled = model.CanRead;
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
