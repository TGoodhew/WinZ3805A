using System.Globalization;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using WinZ3805A.Controls;
using WinZ3805A.Device.Commands;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Views;

/// <summary>
/// The §10.5 Satellites page, without the sky plot.
/// </summary>
/// <remarks>
/// The tables come first deliberately. §9.10.2 requires the plot to offer "a <c>ListView</c>
/// alternate view toggle for users who cannot use the spatial form", so the tabular view is not
/// scaffolding for the plot — it is half the finished feature, and the half that works without it.
/// </remarks>
public sealed partial class SatellitesPage : Page
{
    /// <summary>§8.3's elevation mask, with its 0-90 range taken from the catalog.</summary>
    private static readonly ScpiCommand SetMask = CommandConfirmation.Require(":GPS:SAT:TRAC:EMANgle");

    private SatellitesViewModel? _model;
    private DeviceContext? _device;
    private CommandInvoker? _invoker;
    private readonly NumberFieldValidator _mask;
    private bool _busy;

    /// <summary>True while the code is writing the selections, so its own writes do not echo back.</summary>
    private bool _syncing;

    private readonly DispatcherTimer _stalenessTicker = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>Creates the page.</summary>
    public SatellitesPage()
    {
        InitializeComponent();

        // Assigned here, not in XAML: the parser widens the literal and 10 arrives with a tail.
        MaskBox.Value = 10;

        _mask = new NumberFieldValidator(MaskBox, MaskError, SetMask.Parameters[0]);
        _mask.ValidityChanged += (_, _) => Render();

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
        _model = new SatellitesViewModel(device.Store) { Connection = device.Session.Status };
        _model.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(Render);
        device.Session.StatusChanged += OnStatusChanged;

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

        // The plot draws rings whether or not there is anything on them, so an empty one looks like
        // a plot that failed rather than a receiver that can see nothing (§9.11).
        ShowEmpty(SkyPlotEmptyText, model.SkyPlotEmptyMessage is not null, model.SkyPlotEmptyMessage ?? string.Empty);
        SkyPlot.Visibility = model.SkyPlotEmptyMessage is null ? Visibility.Visible : Visibility.Collapsed;

        SkyPlotSelectionText.Text = DescribeSelection();

        ApplyMaskButton.IsEnabled =
            !_busy && _mask.IsValid && model.Connection == ConnectionStatus.Connected;

        FooterText.Text = $"Satellite table {model.AgeDescription}";
    }

    /// <summary>
    /// §8.3's elevation mask. Degrees on both sides, so the number the user typed is the number
    /// that goes on the wire and the one §8.3's sentence quotes.
    /// </summary>
    private async void OnApplyMaskClicked(object sender, RoutedEventArgs e)
    {
        if (_invoker is not CommandInvoker invoker || _mask.Value is not double degrees || _busy)
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
                XamlRoot, invoker, SetMask, argument: value, displayValue: value));
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
            TrackedSatelliteRow? tracked = model.Tracked.FirstOrDefault(row => row.Prn == prn);
            if (tracked is not null)
            {
                NotTrackedRows.SelectedItem = null;
                TrackedRows.SelectedItem = tracked;
                TrackedRows.ScrollIntoView(tracked);
                return;
            }

            PredictedSatelliteRow? predicted = model.NotTracked.FirstOrDefault(row => row.Prn == prn);
            if (predicted is not null)
            {
                TrackedRows.SelectedItem = null;
                NotTrackedRows.SelectedItem = predicted;
                NotTrackedRows.ScrollIntoView(predicted);
            }
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
            }
        }
        finally
        {
            _syncing = false;
            SkyPlotSelectionText.Text = DescribeSelection();
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
