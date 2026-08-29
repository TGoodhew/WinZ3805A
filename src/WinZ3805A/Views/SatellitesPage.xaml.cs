using System.Globalization;

using Microsoft.Extensions.DependencyInjection;

using Windows.ApplicationModel;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using WinZ3805A.Controls;
using WinZ3805A.Device.Commands;
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

    /// <summary>Creates the page.</summary>
    public SatellitesPage()
    {
        InitializeComponent();

        // Assigned here, not in XAML: the parser widens the literal and 10 arrives with a tail.
        MaskBox.Value = 10;

        _stalenessTicker.Tick += (_, _) => _model?.RaiseAll();
        Unloaded += (_, _) =>
        {
            _stalenessTicker.Stop();
            if (_device is DeviceContext device)
            {
                device.Session.StatusChanged -= OnStatusChanged;
            }
        };
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
        _mask = new NumberFieldValidator(
            MaskBox,
            MaskError,
            CommandConfirmation.Require(device.Driver, ":GPS:SAT:TRAC:EMANgle").Parameters[0]);
        _mask.ValidityChanged += (_, _) => Render();

        _model = new SatellitesViewModel(device.Store) { Connection = device.Session.Status };
        _model.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(Render);
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
    }

    private void OnStatusChanged(object? sender, ConnectionStatusChanged e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_model is SatellitesViewModel model)
            {
                model.Connection = e.Status;
            }
        });

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

        ApplyMaskButton.IsEnabled =
            !_busy && _mask is { IsValid: true } && model.Connection == ConnectionStatus.Connected;

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
