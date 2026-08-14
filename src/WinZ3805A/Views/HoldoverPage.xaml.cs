using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using WinZ3805A.Controls;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Views;

/// <summary>
/// The §10.8 Holdover page, read-only.
/// </summary>
public sealed partial class HoldoverPage : Page
{
    private HoldoverViewModel? _model;
    private DeviceContext? _device;
    private readonly DispatcherTimer _stalenessTicker = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>Creates the page.</summary>
    public HoldoverPage()
    {
        InitializeComponent();

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
        _model = new HoldoverViewModel(device.Store) { Connection = device.Session.Status };
        _model.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(Render);
        device.Session.StatusChanged += OnStatusChanged;

        _stalenessTicker.Start();
        Render();
    }

    private void OnStatusChanged(object? sender, ConnectionStatusChanged e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_model is HoldoverViewModel model)
            {
                model.Connection = e.Status;
            }
        });

    private void Render()
    {
        if (_model is not HoldoverViewModel model)
        {
            return;
        }

        StatePill.Severity = model.StateSeverity;
        StatePill.Text = model.StateText;

        PredictedText.Text = WithUnit(model.Predicted);
        PresentErrorText.Text = WithUnit(model.PresentError);
        DurationText.Text = model.DurationText;
        WaitingReasonText.Text = model.WaitingReasonText;

        ThresholdText.Text = WithUnit(model.Threshold);
        ThresholdPill.Severity = model.ThresholdSeverity;
        ThresholdPill.Text = model.ThresholdExceededText;

        FooterText.Text = model.AgeDescription;
    }

    private static string WithUnit((string Value, string Unit) reading) =>
        reading.Unit.Length == 0
            ? reading.Value
            : $"{reading.Value}{ReadoutFormatter.HairSpace}{reading.Unit}";
}
