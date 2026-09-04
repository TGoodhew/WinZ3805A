using System.ComponentModel;

using Microsoft.UI.Dispatching;
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

    /// <summary>
    /// The one handler this page hands the dispatcher, reused for every notification (#399).
    /// </summary>
    /// <remarks>
    /// A field rather than a lambda or a method group so the hop allocates nothing. See
    /// <see cref="MainPage"/> for why that is hygiene and not the fix, and for what the leak
    /// in #399 actually turned out to be.
    /// </remarks>
    private readonly DispatcherQueueHandler _render;

    /// <summary>Collapses a burst of notifications into one render (#399).</summary>
    private readonly RenderCoalescer _renders;

    /// <summary>Creates the page.</summary>
    public StatusRegistersPage()
    {
        InitializeComponent();

        _renders = new RenderCoalescer(EnqueueRender);

        _render = () =>
        {
            _renders.Begin();
            Render();
        };

        RegisterPicker.ItemsSource = StatusRegistersViewModel.Registers;
        RegisterPicker.SelectedItem = StatusRegisterMaps.Operation;

        Unloaded += (_, _) => Detach();
    }

    /// <inheritdoc />
    /// <summary>Undoes everything <see cref="OnNavigatedTo"/> subscribed to (#388).</summary>
    /// <remarks>
    /// Idempotent: both <c>Unloaded</c> and <see cref="OnNavigatedFrom"/> call it, and neither is
    /// reliable alone. Disposing the model is the half that matters - it is what lets go of the
    /// store, which outlives every page and was keeping this one alive after it left the screen.
    /// </remarks>
    private void Detach()
    {
        if (_device is DeviceContext device)
        {
            device.Session.StatusChanged -= OnStatusChanged;
        }

        _model?.PropertyChanged -= OnModelChanged;

    }

    /// <summary>Renders on a model notification. Named so <see cref="Detach"/> can remove it (#388).</summary>
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => _renders.Request();

    /// <summary>Hands the cached handler to the dispatcher. A method, so the one delegate is reused.</summary>
    private bool EnqueueRender() => DispatcherQueue.TryEnqueue(_render);

    /// <inheritdoc />
    /// <remarks>
    /// <b>The Frame's hook, not Unloaded (#388).</b> Everything this page subscribed to in
    /// <see cref="OnNavigatedTo"/> is undone here, and the model is disposed so it lets go of the
    /// store. Unloaded was doing half the job and could not do the other half: the store outlives
    /// every page, so store -> model -> page kept the page alive and rendering on every reading
    /// after it left the screen, once per visit.
    /// </remarks>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Detach();
    }

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
        _model.PropertyChanged += OnModelChanged;
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

        // Capability first, then state (#304). All three writable fields, because §10.10 applies the
        // changed masks as three separate commands and offering the button with two of them present
        // would stop the run halfway.
        bool canApply = Capability.Offers(
            _device?.Driver,
            $":STAT:{model.Register.Node}:ENABle",
            $":STAT:{model.Register.Node}:NTRansition",
            $":STAT:{model.Register.Node}:PTRansition");

        ApplyMasksButton.IsEnabled = canApply && !_busy && model.CanApplyMasks;

        MasksUnsupportedText.Text = canApply
            ? string.Empty
            : Capability.NotOffered(_device?.Driver, "writing this register’s masks");
        MasksUnsupportedText.Visibility = canApply ? Visibility.Collapsed : Visibility.Visible;
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
            _device is not DeviceContext device ||
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
                // §10.10's masks are per-register SCPI nodes a talker does not have (#304). The
                // apply button is gated below, so reaching here with one absent would be a gating
                // bug - but this is an async void handler, where an exception has nowhere to go, so
                // it stops cleanly instead.
                if (device.Driver.Find($":STAT:{model.Register.Node}:{RegisterMaskEdit.Field(mask)}")
                    is not ScpiCommand command)
                {
                    break;
                }

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
