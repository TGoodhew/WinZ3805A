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
    private HoldoverViewModel? _model;
    private DeviceContext? _device;
    private CommandInvoker? _invoker;

    /// <summary>
    /// The threshold editor's validator, built in <c>OnNavigatedTo</c> rather than here (#287):
    /// its range comes from the driver's catalog, and there is no driver until a device arrives.
    /// </summary>
    private NumberFieldValidator? _threshold;
    private bool _busy;
    private readonly DispatcherTimer _stalenessTicker = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>Creates the page.</summary>
    public HoldoverPage()
    {
        InitializeComponent();

        // Assigned here rather than in XAML: the parser reads a NumberBox.Value literal as a float
        // and widens it, so a round number arrives with a tail of decimals.
        ThresholdBox.Value = 1;

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

        // §8.3's holdover threshold, with its range taken from the driver's catalog.
        _threshold = new NumberFieldValidator(
            ThresholdBox,
            ThresholdError,
            CommandConfirmation.Require(device.Driver, ":SYNC:HOLD:DUR:THReshold").Parameters[0]);
        _threshold.ValidityChanged += (_, _) => Render();

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

        ApplyThresholdButton.IsEnabled =
            !_busy && _threshold is { IsValid: true } && model.Connection == ConnectionStatus.Connected;

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
        if (_invoker is not CommandInvoker invoker ||
            _model is not HoldoverViewModel model ||
            _device is not DeviceContext device ||
            _busy)
        {
            return;
        }

        await RunAsync(async () => await CommandConfirmation.RunAsync(
            XamlRoot,
            invoker,
            // §8.3's manual holdover — one of §9.7.4's four strong variants.
            CommandConfirmation.Require(device.Driver, ":SYNC:HOLDover:INITiate"),
            caution: model.PowerUpCaution));
    }

    /// <summary>§8.3's holdover threshold. Seconds on both sides, so nothing is scaled.</summary>
    private async void OnApplyThresholdClicked(object sender, RoutedEventArgs e)
    {
        if (_invoker is not CommandInvoker invoker ||
            _device is not DeviceContext device ||
            _threshold?.Value is not double seconds ||
            _busy)
        {
            return;
        }

        ThresholdOutcome.Clear();

        await RunAsync(async () => await CommandConfirmation.RunAsync(
            XamlRoot,
            invoker,
            CommandConfirmation.Require(device.Driver, ":SYNC:HOLD:DUR:THReshold"),
            argument: seconds.ToString("0.###", CultureInfo.InvariantCulture),
            displayValue: seconds.ToString("0.###", CultureInfo.CurrentCulture)),
            ThresholdOutcome);
    }

    private async void OnRecoverClicked(object sender, RoutedEventArgs e) => await RunSafeAsync(":SYNC:HOLD:REC:INIT");

    private async void OnIgnoreLimitClicked(object sender, RoutedEventArgs e) => await RunSafeAsync(":SYNC:HOLD:REC:LIM:IGN");

    /// <summary>
    /// Sends one of the two tier S recovery actions. No confirmation, and so no
    /// <see cref="CommandInvoker"/> either: §7.2 is explicit that the error-queue read belongs to
    /// tier C alone, and the invoker refuses anything else rather than quietly obliging.
    /// </summary>
    /// <param name="mnemonic">
    /// Which action. §8.2 classes both tier S — they move the receiver toward lock and cannot lose
    /// anything — but they are resolved through the driver's catalog all the same, because §8.1
    /// admits no other source of a command.
    /// </param>
    private async Task RunSafeAsync(string mnemonic)
    {
        if (_device is not DeviceContext device || _busy)
        {
            return;
        }

        ScpiCommand command = CommandConfirmation.Require(device.Driver, mnemonic);

        await RunAsync(async () =>
        {
            Transaction reply = await device.Session.ExecuteAsync(command).ConfigureAwait(true);

            // These are setters, so the receiver answers with the prompt alone whether or not the
            // command worked, and §7.2 establishes that the prompt reports the *error queue* rather
            // than the command just sent. There is therefore nothing here that can distinguish
            // "rejected" from "succeeded while something older sat in the queue" — and the error
            // read that could is tier C's alone, which is why this path does not use CommandInvoker.
            //
            // So a dirty queue is reported as a qualifier rather than a verdict. Calling a recovery
            // that worked "couldn't recover" is the worse error of the two, and it is the one this
            // used to make (#173).
            return new CommandOutcome
            {
                Kind = reply.Succeeded ? CommandOutcomeKind.Succeeded : CommandOutcomeKind.Failed,
                Command = command,
                Message = !reply.Succeeded
                    ? $"Couldn't {char.ToLower(command.DisplayName[0], CultureInfo.CurrentCulture)}"
                      + $"{command.DisplayName[1..]}. {DescribeFailure(reply)}"
                    : reply.ErrorQueueNotEmpty
                        ? $"{command.DisplayName} sent. The receiver's error queue is not empty "
                          + $"({reply.PromptStatus}) — see Diagnostics for what is in it."
                        : $"{command.DisplayName} sent.",
            };
        });
    }

    /// <summary>Runs one command with the card's controls disabled and the result on that card.</summary>
    private async Task RunAsync(Func<Task<CommandOutcome?>> operation, CommandOutcomeBar? bar = null)
    {
        CommandOutcomeBar target = bar ?? ManualOutcome;

        _busy = true;
        target.Clear();
        Render();

        try
        {
            target.Show(await operation());
        }
        finally
        {
            _busy = false;
            Render();
        }
    }

    /// <remarks>
    /// Only reached when the transaction itself did not complete. The rejection arm no longer claims
    /// the receiver rejected the command — for a setter the prompt cannot establish that (§7.2) —
    /// and reports what is actually known instead.
    /// </remarks>
    private static string DescribeFailure(Transaction reply) => reply.Outcome switch
    {
        TransactionOutcome.TimedOut => "The receiver did not answer.",
        TransactionOutcome.Faulted => reply.FaultMessage ?? "The serial link failed.",
        _ => $"The transaction did not complete ({reply.PromptStatus ?? "no error reported"}).",
    };

    private static string WithUnit((string Value, string Unit) reading) =>
        reading.Unit.Length == 0
            ? reading.Value
            : $"{reading.Value}{ReadoutFormatter.HairSpace}{reading.Unit}";
}
