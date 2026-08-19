using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using System.Globalization;

using WinZ3805A.Device.Commands;
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
    private CommandInvoker? _invoker;
    private CancellationTokenSource? _reading;
    private bool _ready;
    private bool _busy;

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
        _invoker = new CommandInvoker(device.Session);
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

        // Reassigned only when the collection is actually a different one. The rows are cached in
        // the view model so a pending edit survives, and handing ItemsSource the same list again
        // would rebuild the checkboxes underneath the user's fingers.
        if (!ReferenceEquals(BitRows.ItemsSource, model.Rows))
        {
            BitRows.ItemsSource = model.Rows;
        }

        RawText.Text = model.RawText;

        ReadingRing.IsActive = model.IsReading;
        RefreshButton.IsEnabled = model.CanRead;

        ApplyMasksButton.IsEnabled = !_busy && model.CanApplyMasks;
        DiscardMasksButton.IsEnabled = !_busy && model.IsDirty;
        PendingText.Text = model.PendingText;

        ErrorBar.IsOpen = model.Error is not null;
        ErrorBar.Message = model.Error ?? string.Empty;
    }

    private void OnDiscardMasksClicked(object sender, RoutedEventArgs e)
    {
        _model?.RevertEdits();
        MaskOutcome.Clear();
    }

    /// <summary>
    /// §8.3's mask write, once per changed mask.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §10.10 draws one button and §8.3 makes each of the three setters individually tier C, so a
    /// user who changed all three is asked three times. That is deliberate and the caption above the
    /// button says so before they press it. The alternative — one dialog covering three writes —
    /// would mean a confirmation that is not tied to a single catalog entry, and §8.1's rule that
    /// every destructive command goes through <see cref="CommandConfirmation.RunAsync"/> and
    /// nothing else is the one worth keeping.
    /// </para>
    /// <para>
    /// It stops at the first refusal or failure rather than pressing on. The masks interact — the
    /// enable mask decides whether the transition masks reach the summary byte at all — so applying
    /// two of three would leave the register in a state the user never asked for and did not see.
    /// </para>
    /// </remarks>
    private async void OnApplyMasksClicked(object sender, RoutedEventArgs e)
    {
        if (_invoker is not CommandInvoker invoker ||
            _model is not StatusRegistersViewModel model ||
            !model.CanApplyMasks)
        {
            return;
        }

        _busy = true;
        Render();

        try
        {
            foreach ((RegisterMask mask, int value) in model.PendingWrites)
            {
                ScpiCommand command = CommandConfirmation.Require(
                    $":STAT:{model.Register.Node}:{RegisterMaskEdit.Field(mask)}");

                string formatted = value.ToString(CultureInfo.InvariantCulture);

                CommandOutcome? outcome = await CommandConfirmation.RunAsync(
                    XamlRoot, invoker, command, formatted, formatted);

                // Null is the user cancelling, which is not a failure and gets no outcome bar -
                // they know they cancelled. It still stops the run: the remaining masks were part
                // of the same intent and applying them alone was never what was asked for.
                if (outcome is null)
                {
                    return;
                }

                MaskOutcome.Show(outcome);

                if (!outcome.Succeeded)
                {
                    return;
                }

                model.AcceptWrite(mask);
            }
        }
        finally
        {
            _busy = false;
            Render();
        }
    }
}
