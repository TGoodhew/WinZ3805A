using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using Microsoft.Extensions.DependencyInjection;

using Windows.ApplicationModel;
using Windows.Storage;
using Windows.System;

using WinZ3805A.Device.Commands;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Views;

/// <summary>
/// The §10.9 Diagnostics page.
/// </summary>
public sealed partial class DiagnosticsPage : Page, ICsvExportSource
{
    /// <summary>§8.3's log clear — the one tier C command on this page.</summary>
    private static readonly ScpiCommand ClearLog = CommandConfirmation.Require(":DIAG:LOG:CLEar");

    private DiagnosticsViewModel? _model;
    private DeviceContext? _device;
    private FileLoggerProvider? _logProvider;

    /// <summary>Where the log really is, once MSIX's redirection has been resolved.</summary>
    private string? _logFolder;
    private CommandInvoker? _invoker;
    private CancellationTokenSource? _reading;
    private bool _ready;

    /// <summary>Creates the page.</summary>
    public DiagnosticsPage()
    {
        InitializeComponent();

        Unloaded += (_, _) =>
        {
            _reading?.Cancel();
            _reading?.Dispose();
            _reading = null;

            if (_device is DeviceContext device)
            {
                device.Session.StatusChanged -= OnStatusChanged;
            }
        };
    }

    /// <inheritdoc />
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e?.Parameter is not DeviceContext device)
        {
            return;
        }

        _device = device;
        _invoker = new CommandInvoker(device.Session);

        // Application-scoped, so it comes from the composition root rather than the device context.
        // Null when logging failed to start, which the card handles by disabling its button rather
        // than by hiding: a missing log is worth noticing.
        _logProvider = App.Services?.GetService<FileLoggerProvider>();
        _logFolder = ResolveLogFolder(_logProvider);
        _model = new DiagnosticsViewModel(device.Session);
        _model.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(Render);
        device.Session.StatusChanged += OnStatusChanged;

        _ready = true;
        Render();

        // The log and the self-test result are safe to read on arrival. The error queue is not —
        // reading it empties it — so that one waits for the button.
        await RefreshAsync();
    }

    private void OnStatusChanged(object? sender, ConnectionStatusChanged e) =>
        DispatcherQueue.TryEnqueue(() => _model?.RaiseAll());

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

        SelfTestText.Text = model.SelfTestResultText;
        LogHeaderText.Text = model.LogHeaderText;

        LogRows.ItemsSource = model.Filtered;
        LogEmptyText.Text = model.LogEmptyText;
        LogEmptyText.Visibility = model.Filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ErrorSummaryText.Text = model.ErrorSummaryText;
        ErrorRows.ItemsSource = model.Errors;

        ReadingRing.IsActive = model.IsReading;
        RefreshButton.IsEnabled = model.CanRead;
        ReadErrorsButton.IsEnabled = model.CanRead;
        ClearLogButton.IsEnabled = model.CanRead;

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

    /// <summary>
    /// §8.3's log clear. Nothing is re-read afterwards on purpose: the log is now empty, and a
    /// refresh that showed the empty state would look like the read failing.
    /// </summary>
    private async void OnClearLogClicked(object sender, RoutedEventArgs e)
    {
        if (_invoker is not CommandInvoker invoker || _model is not DiagnosticsViewModel model || !model.CanRead)
        {
            return;
        }

        ClearLogButton.IsEnabled = false;
        LogOutcome.Clear();

        try
        {
            CommandOutcome? outcome = await CommandConfirmation.RunAsync(XamlRoot, invoker, ClearLog);
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
