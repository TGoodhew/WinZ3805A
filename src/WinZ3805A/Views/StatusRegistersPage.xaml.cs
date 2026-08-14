using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using WinZ3805A.Device.Models;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Views;

/// <summary>
/// The §10.10 Status Registers page.
/// </summary>
/// <remarks>
/// The first page that issues commands. Everything it sends is a query resolved from the §8.1
/// catalog, and it sends them only when asked — see the view model for why a cadence would be
/// actively harmful here.
/// </remarks>
public sealed partial class StatusRegistersPage : Page
{
    private StatusRegistersViewModel? _model;
    private DeviceContext? _device;
    private CancellationTokenSource? _reading;
    private bool _ready;

    /// <summary>Creates the page.</summary>
    public StatusRegistersPage()
    {
        InitializeComponent();

        RegisterPicker.ItemsSource = StatusRegistersViewModel.Registers;
        RegisterPicker.SelectedItem = StatusRegisterMaps.Operation;

        Unloaded += (_, _) =>
        {
            // A read in flight belongs to a page nobody is looking at any more.
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
        _model = new StatusRegistersViewModel(device.Session);
        _model.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(Render);
        device.Session.StatusChanged += OnStatusChanged;

        _ready = true;
        Render();

        // One read on arrival, so the page is not empty until someone finds the button.
        await RefreshAsync();
    }

    private void OnStatusChanged(object? sender, ConnectionStatusChanged e) =>
        DispatcherQueue.TryEnqueue(() => _model?.RaiseAll());

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnRegisterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _model is not StatusRegistersViewModel model)
        {
            return;
        }

        if (RegisterPicker.SelectedItem is StatusRegisterMap register)
        {
            model.Register = register;
            await RefreshAsync();
        }
    }

    private async Task RefreshAsync()
    {
        if (_model is not StatusRegistersViewModel model)
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
        if (_model is not StatusRegistersViewModel model)
        {
            return;
        }

        SummaryText.Text = model.Register.Summary;
        BitRows.ItemsSource = model.Rows;
        RawText.Text = model.RawText;

        ReadingRing.IsActive = model.IsReading;
        RefreshButton.IsEnabled = model.CanRead;

        ErrorBar.IsOpen = model.Error is not null;
        ErrorBar.Message = model.Error ?? string.Empty;
    }
}
