using System.Globalization;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using WinZ3805A.Controls;
using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Views;

/// <summary>
/// The §10.8 Holdover page.
/// </summary>
public sealed partial class HoldoverPage : Page
{
    /// <summary>§8.3's manual holdover — one of §9.7.4's four strong variants.</summary>
    private static readonly ScpiCommand ForceHoldover = CommandConfirmation.Require(":SYNC:HOLDover:INITiate");

    /// <summary>
    /// The two recovery actions, which §8.2 classes tier S: both move the receiver toward lock and
    /// neither can lose anything, so they run on click with no dialog. Resolved from the catalog
    /// all the same, because §8.1 admits no other source of a command.
    /// </summary>
    private static readonly ScpiCommand Recover = CommandConfirmation.Require(":SYNC:HOLD:REC:INIT");
    private static readonly ScpiCommand IgnoreLimit = CommandConfirmation.Require(":SYNC:HOLD:REC:LIM:IGN");

    private HoldoverViewModel? _model;
    private DeviceContext? _device;
    private CommandInvoker? _invoker;
    private bool _busy;
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
        _invoker = new CommandInvoker(device.Session);
        _model = new HoldoverViewModel(device.Store)
        {
            Connection = device.Session.Status,
            PowerUp = device.PowerUp,
        };
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

        PowerUpText.Text = model.PowerUpText;
        PowerUpPill.Severity = model.PowerUpSeverity;
        PowerUpPill.Text = model.PowerUpVerdictText;

        ForceHoldoverButton.IsEnabled = !_busy && model.CanForceHoldover;
        RecoverButton.IsEnabled = !_busy && model.CanRecover;
        IgnoreLimitButton.IsEnabled = !_busy && model.CanRecover;

        FooterText.Text = model.AgeDescription;
    }

    /// <summary>
    /// §8.3's manual holdover. The dialog always carries the acknowledgement tick because §9.7.4
    /// makes this a strong variant; §10.8's guard decides what the user is ticking <i>against</i>,
    /// which is a different question and the one the caution answers.
    /// </summary>
    private async void OnForceHoldoverClicked(object sender, RoutedEventArgs e)
    {
        if (_invoker is not CommandInvoker invoker || _model is not HoldoverViewModel model || _busy)
        {
            return;
        }

        await RunAsync(async () => await CommandConfirmation.RunAsync(
            XamlRoot,
            invoker,
            ForceHoldover,
            caution: model.PowerUpCaution));
    }

    private async void OnRecoverClicked(object sender, RoutedEventArgs e) => await RunSafeAsync(Recover);

    private async void OnIgnoreLimitClicked(object sender, RoutedEventArgs e) => await RunSafeAsync(IgnoreLimit);

    /// <summary>
    /// Sends one of the two tier S recovery actions. No confirmation, and so no
    /// <see cref="CommandInvoker"/> either: §7.2 is explicit that the error-queue read belongs to
    /// tier C alone, and the invoker refuses anything else rather than quietly obliging.
    /// </summary>
    private async Task RunSafeAsync(ScpiCommand command)
    {
        if (_device is not DeviceContext device || _busy)
        {
            return;
        }

        await RunAsync(async () =>
        {
            Transaction reply = await device.Session.ExecuteAsync(command).ConfigureAwait(true);

            return new CommandOutcome
            {
                Kind = reply.Succeeded && !reply.HasDeviceError
                    ? CommandOutcomeKind.Succeeded
                    : CommandOutcomeKind.Failed,
                Command = command,
                Message = reply.Succeeded && !reply.HasDeviceError
                    ? $"{command.DisplayName} sent."
                    : $"Couldn't {char.ToLower(command.DisplayName[0], CultureInfo.CurrentCulture)}"
                      + $"{command.DisplayName[1..]}. {DescribeFailure(reply)}",
            };
        });
    }

    /// <summary>Runs one command with the button row disabled and the result on the card.</summary>
    private async Task RunAsync(Func<Task<CommandOutcome?>> operation)
    {
        _busy = true;
        ManualOutcome.Clear();
        Render();

        try
        {
            ManualOutcome.Show(await operation());
        }
        finally
        {
            _busy = false;
            Render();
        }
    }

    private static string DescribeFailure(Transaction reply) => reply.Outcome switch
    {
        TransactionOutcome.TimedOut => "The receiver did not answer.",
        TransactionOutcome.Faulted => reply.FaultMessage ?? "The serial link failed.",
        _ => $"The receiver rejected it ({reply.PromptStatus}).",
    };

    private static string WithUnit((string Value, string Unit) reading) =>
        reading.Unit.Length == 0
            ? reading.Value
            : $"{reading.Value}{ReadoutFormatter.HairSpace}{reading.Unit}";
}
