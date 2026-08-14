using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Views;

/// <summary>
/// The §10.9 Diagnostics page.
/// </summary>
public sealed partial class DiagnosticsPage : Page
{
    private DiagnosticsViewModel? _model;
    private DeviceContext? _device;
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

        FaultBar.IsOpen = model.Fault is not null;
        FaultBar.Message = model.Fault ?? string.Empty;
    }
}
