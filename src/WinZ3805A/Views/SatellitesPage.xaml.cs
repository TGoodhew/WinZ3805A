using System.Globalization;

using Microsoft.Extensions.DependencyInjection;

using Windows.ApplicationModel;

using System.ComponentModel;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using WinZ3805A.Controls;
using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Views;

/// <summary>
/// The §10.5 Satellites page: the sky, in whichever of its two forms the user asked for.
/// </summary>
/// <remarks>
/// <para>
/// §9.10.2 requires the plot to offer "a <c>ListView</c> alternate view toggle for users who cannot
/// use the spatial form", and A11Y-11 requires that alternate to carry <b>the same data</b>. The
/// toggle on the sky card is that feature (#60, #31).
/// </para>
/// <para>
/// <b>The tracked and not-tracked tables are not it</b>, though this file used to say they were.
/// They split one sky across two cards by tracking state and neither prints that state as a column,
/// so a user reading one of them alone knows less than a user reading the plot. The alternate is a
/// single list over <see cref="SatellitesViewModel.SkyPlotSatellites"/> — literally the collection
/// the plot draws from — with the marker's shape written out as a word.
/// </para>
/// </remarks>
public sealed partial class SatellitesPage : Page
{
    private SatellitesViewModel? _model;
    private DeviceContext? _device;
    private CommandInvoker? _invoker;
    private ISatellitesViewPreferenceStore? _preferences;
    /// <summary>
    /// The mask editor's validator, built in <c>OnNavigatedTo</c> rather than the constructor
    /// (#287): its 0-90 range comes from the driver's catalog, and there is no driver until a
    /// device arrives.
    /// </summary>
    private NumberFieldValidator? _mask;
    private bool _busy;

    /// <summary>What the connected receiver's driver offers (#304), decided once per navigation.</summary>
    private bool _canSetMask;
    private bool _canManage;

    /// <summary>The last value this page wrote into the mask editor.</summary>
    /// <remarks>
    /// Compared against rather than guarded with a flag around the assignment, for the reason
    /// §10.8's duration limit gives: when <c>ValueChanged</c> arrives relative to the setter is the
    /// control's business, and a comparison does not depend on the answer. <c>double.Equals</c>, so
    /// the empty box compares equal to itself and an untouched field is never taken for an edited
    /// one.
    /// </remarks>
    private double _seededMask = double.NaN;

    /// <summary>Whether the number in the mask editor is the user's rather than the receiver's.</summary>
    private bool _maskEdited;

    /// <summary>Which form of the sky is showing.</summary>
    private SkyView _skyView = SkyView.Plot;

    /// <summary>
    /// False until the stored choice has been restored.
    /// </summary>
    /// <remarks>
    /// Setting <c>IsChecked</c> on a <c>RadioButton</c> raises <c>Checked</c>, so without this the
    /// act of restoring the preference writes it straight back — harmless here, but the same guard
    /// <c>DetailsWindow</c> needs for a reason, and a page that saves during its own initialisation
    /// is one refactor away from saving the default over the user's choice.
    /// </remarks>
    private bool _ready;

    /// <summary>True while the code is writing the selections, so its own writes do not echo back.</summary>
    private bool _syncing;

    private readonly DispatcherTimer _stalenessTicker = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>
    /// The one handler this page hands the dispatcher, reused for every notification (#399).
    /// </summary>
    /// <remarks>
    /// A field rather than a lambda or a method group because each of those allocates a fresh
    /// delegate, and a fresh delegate is a fresh COM wrapper the runtime can never reuse. See
    /// <see cref="MainPage"/> for the measurement.
    /// </remarks>
    private readonly DispatcherQueueHandler _render;

    /// <summary>Creates the page.</summary>
    public SatellitesPage()
    {
        InitializeComponent();

        _render = Render;

        // Empty until the receiver says otherwise (#320), for the reason §10.8's duration limit is:
        // a hard-coded 10 reads as the receiver's current mask and is not one. That it happened to
        // match this unit made it worse, not better - a default that is right by luck is a default
        // nobody checks.
        //
        // Assigned here and not in XAML either way: the parser widens a NumberBox.Value literal and
        // a round number arrives with a tail of decimals.
        MaskBox.Value = double.NaN;

        _stalenessTicker.Tick += (_, _) => _model?.RaiseAll();
        Unloaded += (_, _) => Detach();
    }

    /// <summary>Undoes everything <see cref="OnNavigatedTo"/> subscribed to (#388).</summary>
    /// <remarks>
    /// Idempotent: both <c>Unloaded</c> and <see cref="OnNavigatedFrom"/> call it, and neither is
    /// reliable alone. Disposing the model is the half that matters - it is what lets go of the
    /// store, which outlives every page and was keeping this one alive after it left the screen.
    /// </remarks>
    private void Detach()
    {
        _stalenessTicker.Stop();

        if (_device is DeviceContext device)
        {
            device.Session.StatusChanged -= OnStatusChanged;
        }

        if (_model is SatellitesViewModel model)
        {
            model.PropertyChanged -= OnModelChanged;
            model.Dispose();
            _model = null;
        }
    }

    /// <summary>Renders on a model notification. Named so <see cref="Detach"/> can remove it (#388).</summary>
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(_render);

    /// <inheritdoc />
    /// <remarks>
    /// <b>The Frame's hook, not Unloaded (#388).</b> Everything this page subscribed to in
    /// <see cref="OnNavigatedTo"/> is undone here, and the model is disposed so it lets go of the
    /// store. Unloaded was doing half the job and could not do the other half: the store outlives
    /// every page, so store -> model -> page kept the page alive and rendering on every reading
    /// after it left the screen, once per visit. Four visits to Overview left four of them.
    /// </remarks>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Detach();
    }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e?.Parameter is not DeviceContext device)
        {
            return;
        }

        _device = device;
        _invoker = new CommandInvoker(device.Session);

        // §8.3's elevation mask, with its 0-90 range taken from the driver's catalog.
        // A talker has none of §10.5's commands (#304), so the spec is looked up rather than
        // required and the controls are disabled below instead of the navigation throwing.
        _mask = new NumberFieldValidator(MaskBox, MaskError, minimum: null, maximum: null);
        _mask.ValidityChanged += (_, _) => Render();

        BindDriver();

        MaskBox.ValueChanged += (_, args) =>
        {
            if (!(args?.NewValue ?? double.NaN).Equals(_seededMask))
            {
                _maskEdited = true;
            }
        };

        _model = new SatellitesViewModel(device.Store) { Connection = device.Session.Status };
        _model.PropertyChanged += OnModelChanged;
        device.Session.StatusChanged += OnStatusChanged;

        // Application-scoped rather than per-device: which form of the sky a user can read is a
        // fact about the user, not about the receiver they happen to be looking at.
        _preferences = App.Services?.GetService<ISatellitesViewPreferenceStore>();
        _skyView = _preferences?.Load().SkyView ?? SkyView.Plot;

        PlotViewChoice.IsChecked = _skyView == SkyView.Plot;
        ListViewChoice.IsChecked = _skyView == SkyView.List;
        _ready = true;

        ApplySkyView();
        _stalenessTicker.Start();
        Render();

        _ = ReadExclusionsAsync();
    }

    /// <summary>
    /// Reads the receiver's exclusion list, for §10.5's <i>ignored</i> status (#320).
    /// </summary>
    /// <remarks>
    /// <para>
    /// On navigation, on reconnect, and after the Manage dialog — not on the sweep. The list changes
    /// only when someone changes it, and a second query on the 1 s cadence to catch an event that
    /// happens twice a year would be paying wire time for nothing (§7.3). The three moments it is
    /// read are the three at which it can have changed without this page knowing.
    /// </para>
    /// <para>
    /// A failure leaves the set empty, which means no row claims to be ignored. That is the safe
    /// direction: an unread list must not make a satellite look excluded when it is not, and §11.1's
    /// rule is that what could not be read says nothing rather than guessing.
    /// </para>
    /// </remarks>
    private async Task ReadExclusionsAsync()
    {
        if (_device is not DeviceContext device ||
            _model is not SatellitesViewModel model ||
            device.Session.Status != ConnectionStatus.Connected ||
            device.Driver.Find(":GPS:SAT:TRAC:IGN?") is not ScpiCommand query)
        {
            return;
        }

        Transaction reply = await device.Session.ExecuteAsync(query).ConfigureAwait(true);

        model.ExcludedPrns = reply.Succeeded
            ? SatelliteTrackingParser.ParsePrnList(reply.Lines)
            : new HashSet<int>();
    }

    private void OnStatusChanged(object? sender, ConnectionStatusChanged e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_model is SatellitesViewModel model)
            {
                model.Connection = e.Status;
            }

            if (e?.Status == ConnectionStatus.Connected)
            {
                // The receiver on the port can have been swapped while the link was down, so the
                // session re-selects a driver on every connect (#287) and this page's answer to
                // "what may I offer" has to be asked again rather than kept from navigation (#304).
                BindDriver();
                Render();

                _ = ReadExclusionsAsync();
            }
        });

    /// <summary>
    /// Re-reads everything this page takes from the connected receiver's driver (#304).
    /// </summary>
    /// <remarks>
    /// Called at navigation and again on every connect. Nothing here subscribes or allocates a
    /// validator: <see cref="NumberFieldValidator.Rebind"/> exists so the bounds can move without
    /// a second validator being left listening to the same field.
    /// </remarks>
    private void BindDriver()
    {
        IReceiverDriver? driver = _device?.Driver;

        _canSetMask = Capability.Offers(driver, ":GPS:SAT:TRAC:EMANgle");
        _canManage = Capability.Offers(
            driver,
            ":GPS:SAT:TRAC:INCLude",
            ":GPS:SAT:TRAC:INCLude ALL",
            ":GPS:SAT:TRAC:INCLude NONE",
            ":GPS:SAT:TRAC:IGNore ALL",
            ":GPS:SAT:TRAC:IGNore NONE");

        ParameterSpec? maskRange = Capability.SpecFor(driver, ":GPS:SAT:TRAC:EMANgle");
        _mask?.Rebind(maskRange);

        // §9.10.1's slider takes its bounds from the same catalog entry the validator does, rather
        // than restating 0 and 90 in XAML where the two could drift apart. A slider physically
        // cannot leave its range, which is why the error text below it is for typed entry only.
        MaskSlider.Minimum = maskRange?.Minimum ?? 0;
        MaskSlider.Maximum = maskRange?.Maximum ?? 90;
    }

    private void Render()
    {
        if (_model is not SatellitesViewModel model)
        {
            return;
        }

        CountSummaryText.Text = model.CountSummary;

        // The column heading is the receiver's own — C/N or SS — because the two scales differ by a
        // factor of five and a heading that guessed would be the most misleading label on the page.
        SignalStrengthScale scale = SignalStrengthScale.For(model.SignalStrengthKind);
        StrengthHeader.Text = scale.IsKnown ? scale.Label : "Signal";

        // Only when the rows have actually changed. The view model hands back the same instances
        // between screens, and reassigning ItemsSource rebuilds every container - which would
        // discard the user's selection on every staleness tick.
        if (!ReferenceEquals(TrackedRows.ItemsSource, model.Tracked))
        {
            TrackedRows.ItemsSource = model.Tracked;
        }

        if (!ReferenceEquals(NotTrackedRows.ItemsSource, model.NotTracked))
        {
            NotTrackedRows.ItemsSource = model.NotTracked;
        }

        ShowEmpty(TrackedEmptyText, model.Tracked.Count == 0, model.EmptyMessage);
        ShowEmpty(NotTrackedEmptyText, model.NotTracked.Count == 0, "Nothing else is expected in view.");

        // The editor opens on the receiver's own mask (#320). It arrives on the status screen, so
        // this costs no wire time - unlike §10.8's duration limit, which needed a query. Not
        // overwritten once the user has typed: a sweep lands every second and would otherwise undo
        // them mid-edit.
        if (!_maskEdited && model.ElevationMaskDegrees is int current && !_seededMask.Equals((double)current))
        {
            _seededMask = current;
            MaskBox.Value = current;
            _mask?.Revalidate();
        }

        ElevationMaskText.Text = model.ElevationMaskDegrees is int mask
            ? $"{mask}° — satellites below this are not used"
            : ReadoutFormatter.NoValue;

        SkyPlot.Satellites = model.SkyPlotSatellites;
        SkyPlot.ElevationMaskDegrees = model.ElevationMaskDegrees;

        // Same guard, same reason as the two tables above.
        if (!ReferenceEquals(SkyRows.ItemsSource, model.SkyPlotSatellites))
        {
            SkyRows.ItemsSource = model.SkyPlotSatellites;
        }

        SkyListStrengthHeader.Text = StrengthHeader.Text;

        // The plot draws rings whether or not there is anything on them, so an empty one looks like
        // a plot that failed rather than a receiver that can see nothing (§9.11). The list needs the
        // same sentence for the same reason: an empty list is indistinguishable from a broken one.
        ShowEmpty(SkyPlotEmptyText, model.SkyPlotEmptyMessage is not null, model.SkyPlotEmptyMessage ?? string.Empty);
        ApplySkyView(model.SkyPlotEmptyMessage is null);

        // #47. Offered only when there is a sky to record: an empty plot exports to a picture of
        // three rings, which looks like a working antenna seeing nothing rather than a receiver that
        // is not connected, and that is the one misreading a calibration record must not invite.
        ExportImageButton.IsEnabled = model.SkyPlotEmptyMessage is null;

        SkyPlotSelectionText.Text = DescribeSelection();

        // Capability first, then state (#304).
        MaskBox.IsEnabled = _canSetMask;
        MaskSlider.IsEnabled = _canSetMask;
        MaskUnsupportedText.Text = _canSetMask
            ? string.Empty
            : Capability.NotOffered(_device?.Driver, "an elevation mask");
        MaskUnsupportedText.Visibility = _canSetMask ? Visibility.Collapsed : Visibility.Visible;

        ApplyMaskButton.IsEnabled = _canSetMask
            && !_busy && _mask is { IsValid: true } && model.Connection == ConnectionStatus.Connected;

        ManageButton.IsEnabled = _canManage && !_busy;
        ManageUnsupportedText.Text = _canManage
            ? string.Empty
            : Capability.NotOffered(_device?.Driver, "choosing which satellites to track");
        ManageUnsupportedText.Visibility = _canManage ? Visibility.Collapsed : Visibility.Visible;

        FooterText.Text = $"Satellite table {model.AgeDescription}";
    }

    /// <summary>
    /// §8.3's elevation mask. Degrees on both sides, so the number the user typed is the number
    /// that goes on the wire and the one §8.3's sentence quotes.
    /// </summary>
    private async void OnApplyMaskClicked(object sender, RoutedEventArgs e)
    {
        if (_invoker is not CommandInvoker invoker ||
            _device is not DeviceContext device ||
            _mask?.Value is not double degrees ||
            _busy)
        {
            return;
        }

        _busy = true;
        MaskOutcome.Clear();
        Render();

        try
        {
            string value = degrees.ToString("0.###", CultureInfo.InvariantCulture);
            MaskOutcome.Show(await CommandConfirmation.RunAsync(
                XamlRoot,
                invoker,
                CommandConfirmation.Require(device.Driver, ":GPS:SAT:TRAC:EMANgle"),
                argument: value,
                displayValue: value));
        }
        finally
        {
            _busy = false;
            Render();
        }
    }

    /// <summary>
    /// §10.5: tapping a marker selects the matching table row.
    /// </summary>
    /// <remarks>
    /// The satellite may be in either table, and which one is the interesting part of the answer —
    /// a user clicking a hollow marker is usually asking "why is that one not being used", and the
    /// row it lands on is where that is answered.
    /// </remarks>
    private void OnSkyPlotSatelliteInvoked(object? sender, int prn)
    {
        if (_model is not SatellitesViewModel model)
        {
            return;
        }

        _syncing = true;
        try
        {
            SelectInTables(prn);
            SelectInSkyList(prn);
        }
        finally
        {
            _syncing = false;
            SkyPlotSelectionText.Text = DescribeSelection();
        }
    }

    /// <summary>And the other way: picking a row rings its marker.</summary>
    private void OnTrackedSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            if (TrackedRows.SelectedItem is TrackedSatelliteRow row)
            {
                NotTrackedRows.SelectedItem = null;
                SkyPlot.SelectedPrn = row.Prn;
                SelectInSkyList(row.Prn);
            }
        }
        finally
        {
            _syncing = false;
            SkyPlotSelectionText.Text = DescribeSelection();
        }
    }

    private void OnNotTrackedSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            if (NotTrackedRows.SelectedItem is PredictedSatelliteRow row)
            {
                TrackedRows.SelectedItem = null;
                SkyPlot.SelectedPrn = row.Prn;
                SelectInSkyList(row.Prn);
            }
        }
        finally
        {
            _syncing = false;
            SkyPlotSelectionText.Text = DescribeSelection();
        }
    }

    /// <summary>§10.5's Manage dialog (P1-3).</summary>
    /// <remarks>
    /// Opened with the shared <c>DeviceContext</c> rather than its own session. The dialog writes
    /// nothing itself — every command on it goes through §8.3's confirmation — so there is no
    /// result to bring back here beyond what the receiver's own next poll reports.
    /// </remarks>
    private async void OnManageClicked(object sender, RoutedEventArgs e)
    {
        if (_device is not DeviceContext device || _invoker is not CommandInvoker invoker
            || XamlRoot is null || _busy)
        {
            return;
        }

        _busy = true;
        ManageOutcome.Clear();
        Render();

        try
        {
            // Show, act, show again. The dialog closes before §8.3's confirmation opens because
            // WinUI permits only one ContentDialog at a time — and enforces it by killing the
            // process, from an async void handler, with nothing in the log. Reopening afterwards is
            // what keeps "adjust the selection, apply, adjust again" a single flow.
            while (true)
            {
                SatelliteManagementDialog dialog = new(device) { XamlRoot = XamlRoot };
                await dialog.ShowAsync();

                if (dialog.ChosenCommand is not ScpiCommand command)
                {
                    return;
                }

                ManageOutcome.Show(await CommandConfirmation.RunAsync(
                    XamlRoot, invoker, command, dialog.ChosenArgument, dialog.ChosenArgument));
            }
        }
        finally
        {
            _busy = false;
            Render();

            // The dialog exists to change the exclusion list, so this is the one moment it is
            // certain to be stale. In the finally and not after it: the loop above leaves only by
            // returning, so anything written past the try is unreachable — the compiler said so.
            //
            // Re-read whether or not a command was sent. The user may have cancelled after changing
            // the list from something else, and a read costs one transaction. Render has already run
            // by then, so the rows correct themselves when the answer arrives rather than waiting.
            await ReadExclusionsAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Shows whichever form of the sky is selected, and hides the other.</summary>
    /// <param name="hasSatellites">
    /// False when there is nothing to show at all, in which case both forms stay hidden and
    /// <c>SkyPlotEmptyText</c> is what the user reads instead.
    /// </param>
    private void ApplySkyView(bool hasSatellites = true)
    {
        bool plot = _skyView == SkyView.Plot;

        SkyCardHeading.Text = plot ? "Sky plot" : "Satellites";

        SkyPlot.Visibility = plot && hasSatellites ? Visibility.Visible : Visibility.Collapsed;
        SkyListHeader.Visibility = !plot && hasSatellites ? Visibility.Visible : Visibility.Collapsed;
        SkyRows.Visibility = !plot && hasSatellites ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// §10.5's image export (#47): the sky card, as it stands, as a PNG.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caption is shown, laid out, captured and hidden again inside one handler. It exists in
    /// the tree the whole time — <c>RenderTargetBitmap</c> will not render a collapsed element, so
    /// there is no version of this that captures something the screen never held. The
    /// <c>UpdateLayout</c> is what makes the card measure its new height before the bitmap is sized
    /// from <c>ActualHeight</c>; without it the caption is laid out but cropped off the bottom.
    /// </para>
    /// <para>
    /// <c>finally</c>, so a picker the user cancels or a save that throws still leaves the card as
    /// they found it. A caption stuck permanently under the plot would be a worse defect than a
    /// failed export, because nothing about it says which one went wrong.
    /// </para>
    /// </remarks>
    private async void OnExportImageClicked(object sender, RoutedEventArgs e)
    {
        if (_model is not SatellitesViewModel model || _device is not DeviceContext device)
        {
            return;
        }

        SkyPlotCaptionText.Text = SkyPlotExport.Caption(
            Package.Current.DisplayName,
            device.TimeProvider.GetUtcNow(),
            model.TrackedCount,
            model.NotTrackedCount,
            model.ElevationMaskDegrees);

        SkyPlotCaptionText.Visibility = Visibility.Visible;
        SkyCard.UpdateLayout();

        try
        {
            double raster = XamlRoot?.RasterizationScale ?? 1d;

            await VisualPngExport.SaveAsync(
                SkyCard,
                XamlRoot,
                SkyPlotExport.SuggestedFileName(Package.Current.DisplayName, device.TimeProvider.GetUtcNow()),
                SkyPlotExport.ScaleFor(SkyCard.ActualWidth * raster, SkyCard.ActualHeight * raster));
        }
        finally
        {
            SkyPlotCaptionText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>A11Y-11's toggle.</summary>
    private void OnSkyViewChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        _skyView = ListViewChoice.IsChecked == true ? SkyView.List : SkyView.Plot;
        _preferences?.Save(new SatellitesViewPreferences { SkyView = _skyView });

        ApplySkyView(_model?.SkyPlotEmptyMessage is null);
        Render();
    }

    /// <summary>Picking a row in the list alternate rings the same marker the plot would.</summary>
    private void OnSkyRowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || SkyRows.SelectedItem is not SkyPlotSatellite satellite)
        {
            return;
        }

        _syncing = true;
        try
        {
            SkyPlot.SelectedPrn = satellite.Prn;
            SelectInTables(satellite.Prn);
        }
        finally
        {
            _syncing = false;
            SkyPlotSelectionText.Text = DescribeSelection();
        }
    }

    /// <summary>
    /// Points the two tables at a PRN, whichever of them holds it.
    /// </summary>
    /// <remarks>
    /// Extracted when the list alternate became a fourth surface sharing one selection. Four
    /// handlers each setting three others is twelve assignments to keep in agreement; this is one
    /// place where "selected" means the same thing everywhere.
    /// </remarks>
    private void SelectInTables(int prn)
    {
        if (_model is not SatellitesViewModel model)
        {
            return;
        }

        if (model.Tracked.FirstOrDefault(row => row.Prn == prn) is TrackedSatelliteRow tracked)
        {
            NotTrackedRows.SelectedItem = null;
            TrackedRows.SelectedItem = tracked;
            TrackedRows.ScrollIntoView(tracked);
            return;
        }

        if (model.NotTracked.FirstOrDefault(row => row.Prn == prn) is PredictedSatelliteRow predicted)
        {
            TrackedRows.SelectedItem = null;
            NotTrackedRows.SelectedItem = predicted;
            NotTrackedRows.ScrollIntoView(predicted);
        }
    }

    /// <summary>And points the list alternate at it.</summary>
    private void SelectInSkyList(int prn)
    {
        if (_model?.SkyPlotSatellites.FirstOrDefault(candidate => candidate.Prn == prn)
            is SkyPlotSatellite satellite)
        {
            SkyRows.SelectedItem = satellite;
            SkyRows.ScrollIntoView(satellite);
        }
    }

    /// <summary>
    /// The line under the plot naming what is selected.
    /// </summary>
    /// <remarks>
    /// A ring on a marker says <em>which</em> without saying <em>what</em>, and it is 12 px across.
    /// This is the same sentence the marker carries for assistive technology, put on screen — which
    /// is the cheapest way for the two to stay in step.
    /// </remarks>
    private string DescribeSelection()
    {
        if (_model is not SatellitesViewModel model || SkyPlot.SelectedPrn is not int prn)
        {
            return "Select a satellite on the plot or in a table to see it in both.";
        }

        SkyPlotSatellite? satellite = model.SkyPlotSatellites.FirstOrDefault(candidate => candidate.Prn == prn);
        return satellite?.Description ?? string.Empty;
    }

    private static void ShowEmpty(TextBlock block, bool isEmpty, string message)
    {
        block.Text = message;
        block.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
    }
}
