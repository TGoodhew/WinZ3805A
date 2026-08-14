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

        TrackedRows.ItemsSource = model.Tracked;
        NotTrackedRows.ItemsSource = model.NotTracked;

        ShowEmpty(TrackedEmptyText, model.Tracked.Count == 0, model.EmptyMessage);
        ShowEmpty(NotTrackedEmptyText, model.NotTracked.Count == 0, "Nothing else is expected in view.");

        ElevationMaskText.Text = model.ElevationMaskDegrees is int mask
            ? $"{mask}° — satellites below this are not used"
            : ReadoutFormatter.NoValue;

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

    private static void ShowEmpty(TextBlock block, bool isEmpty, string message)
    {
        block.Text = message;
        block.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
    }
}
