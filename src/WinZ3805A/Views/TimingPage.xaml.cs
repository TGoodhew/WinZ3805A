using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using WinZ3805A.Device.Models;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Views;

/// <summary>
/// The §10.7 Timing &amp; Antenna page.
/// </summary>
public sealed partial class TimingPage : Page
{
    private TimingViewModel? _model;
    private DeviceContext? _device;
    private readonly DispatcherTimer _stalenessTicker = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>
    /// False until the view model exists.
    /// </summary>
    /// <remarks>
    /// <c>IsChecked="True"</c> on the preset radio raises <c>Checked</c> during
    /// <c>InitializeComponent</c>, before any field below is assigned — the same trap that once
    /// killed this application at start-up from <c>MainPage</c>'s toggle switch.
    /// </remarks>
    private bool _ready;

    /// <summary>Creates the page.</summary>
    public TimingPage()
    {
        InitializeComponent();

        CablePicker.ItemsSource = TimingViewModel.Cables;
        CablePicker.SelectedItem = AntennaCable.Lmr400;

        // Assigned here, not in XAML. The XAML parser reads a NumberBox.Value literal as a float
        // and widens it, so "0.85" reaches the control as 0.8500000238 and is displayed in full.
        VelocityFactorBox.Value = 0.85;
        LengthBox.Value = 20;

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
        _model = new TimingViewModel(device.Store) { Connection = device.Session.Status };
        _model.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(Render);
        device.Session.StatusChanged += OnStatusChanged;

        _ready = true;
        _stalenessTicker.Start();
        Render();
    }

    private void OnStatusChanged(object? sender, ConnectionStatusChanged e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_model is TimingViewModel model)
            {
                model.Connection = e.Status;
            }
        });

    private void OnCableSourceChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _model is not TimingViewModel model)
        {
            return;
        }

        bool custom = UseVelocityRadio.IsChecked == true;
        model.UseVelocityFactor = custom;

        CablePicker.IsEnabled = !custom;
        VelocityFactorBox.IsEnabled = custom;
    }

    private void OnCableChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && _model is TimingViewModel model && CablePicker.SelectedItem is AntennaCable cable)
        {
            model.Cable = cable;
        }
    }

    private void OnVelocityFactorChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_ready && _model is TimingViewModel model && !double.IsNaN(args.NewValue))
        {
            model.VelocityFactor = args.NewValue;
        }
    }

    private void OnLengthChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_ready && _model is TimingViewModel model && !double.IsNaN(args.NewValue))
        {
            model.LengthMetres = args.NewValue;
        }
    }

    private void Render()
    {
        if (_model is not TimingViewModel model)
        {
            return;
        }

        CurrentDelayText.Text = model.CurrentDelayText;

        ComputedDelay.Value = model.ComputedDelayNanoseconds;
        CableSourceText.Text = model.CableSourceText;

        // Only when there is something to compare against. A caution beside a receiver that has
        // not reported its own delay would be comparing a number with nothing.
        if (model.DifferenceNanoseconds is not null)
        {
            DifferencePill.Visibility = Visibility.Visible;
            DifferencePill.Severity = model.DifferenceSeverity;
            DifferencePill.Text = model.IsDifferenceSignificant
                ? model.DifferenceText
                : "Matches what the receiver is using";
        }
        else
        {
            DifferencePill.Visibility = Visibility.Collapsed;
        }

        ApplyNotice.IsOpen = model.IsComputedDelayAcceptable;
        if (!model.IsComputedDelayAcceptable && model.ComputedDelayNanoseconds is not null)
        {
            ApplyNotice.IsOpen = true;
            ApplyNotice.Severity = InfoBarSeverity.Warning;
            ApplyNotice.Title = "Out of range";
            ApplyNotice.Message = "The receiver accepts 0 to 999 999 ns. This cable run is longer than it can compensate for.";
        }
        else
        {
            ApplyNotice.Severity = InfoBarSeverity.Informational;
            ApplyNotice.Title = "Not applied";
            ApplyNotice.Message = "Changing the antenna delay while locked can send the receiver into holdover, "
                + "so it needs a confirmation dialog. Those arrive with §15 step 10.";
        }

        TimeInterval.Value = model.TimeIntervalNanoseconds;
        Deviation.Value = model.TimeIntervalDeviation;

        DeviationWindowText.Text = $"σ over the {model.DeviationWindow}. "
            + "The specification's one-hour window needs the trend history that arrives with P1 persistence.";

        FooterText.Text = model.AgeDescription;
    }
}
