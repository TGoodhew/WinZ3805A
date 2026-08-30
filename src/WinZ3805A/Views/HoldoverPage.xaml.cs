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

    /// <summary>Whether the value in the editor came from the user rather than from the receiver.</summary>
    /// <remarks>
    /// A reconnect re-reads the limit, and a re-read must not overwrite a number the user is part
    /// way through typing. Cleared after a successful Apply, because at that point the user's value
    /// <i>is</i> the receiver's and re-reading it confirms what the receiver actually took — which
    /// can differ, the limit having one-second resolution.
    /// </remarks>
    private bool _thresholdEdited;

    /// <summary>The last value the page itself wrote into the editor.</summary>
    /// <remarks>
    /// Compared against, rather than guarded with a flag around the assignment: whether
    /// <c>ValueChanged</c> arrives inside the setter or after it is the control's business, and a
    /// comparison does not depend on the answer. A user who types the receiver's own number by hand
    /// is not counted as having edited it, which costs nothing — the value is the same either way.
    /// </remarks>
    private double _seededThreshold = double.NaN;
    private readonly DispatcherTimer _stalenessTicker = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>Creates the page.</summary>
    public HoldoverPage()
    {
        InitializeComponent();

        // Empty until the receiver says otherwise (§10.8, #320). This was a hard-coded 1, which
        // looked like a readback and was not one: a user could open the page, read "1", and believe
        // that was the limit the receiver was holding. An empty box says only that nothing has been
        // read, which is true, and Apply is disabled while it is empty because there is nothing to
        // apply.
        //
        // Assigned here rather than in XAML either way: the parser reads a NumberBox.Value literal
        // as a float and widens it, so a round number arrives with a tail of decimals.
        ThresholdBox.Value = double.NaN;
        ThresholdBox.ValueChanged += OnThresholdEdited;

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

        _ = ReadThresholdAsync();
    }

    private void OnStatusChanged(object? sender, ConnectionStatusChanged e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_model is HoldoverViewModel model)
            {
                model.Connection = e.Status;
            }

            if (e?.Status == ConnectionStatus.Connected)
            {
                _ = ReadThresholdAsync();
            }
        });

    /// <summary>Notes that the number in the editor is the user's and not the receiver's.</summary>
    /// <remarks>
    /// <c>double.Equals</c> and not <c>==</c>, so that the empty box — <c>NaN</c> on both sides —
    /// compares equal to itself and an untouched field is never mistaken for an edited one.
    /// </remarks>
    private void OnThresholdEdited(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!(args?.NewValue ?? double.NaN).Equals(_seededThreshold))
        {
            _thresholdEdited = true;
        }
    }

    /// <summary>
    /// Reads the receiver's current holdover duration limit into the editor (§10.8, #320).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>:SYNC:HOLD:DUR:THR?</c> has sat in the catalog unused since it was written, and the editor
    /// beside it opened at a hard-coded 1 — which is worse than an empty box, because it reads as
    /// the receiver's answer. On this unit the limit is not 1.
    /// </para>
    /// <para>
    /// Resolved through the driver's catalog like every other command (§8.1), so a driver without
    /// the query — the NMEA one has no thresholds at all — leaves the box empty rather than
    /// throwing. A read that fails leaves it empty too, for the same reason the default went: an
    /// unread field must not show a number.
    /// </para>
    /// </remarks>
    private async Task ReadThresholdAsync()
    {
        if (_device is not DeviceContext device ||
            device.Session.Status != ConnectionStatus.Connected ||
            _thresholdEdited ||
            device.Driver.Find(":SYNC:HOLD:DUR:THR?") is not ScpiCommand query)
        {
            return;
        }

        Transaction reply = await device.Session.ExecuteAsync(query).ConfigureAwait(true);

        // Responses carry a leading space (#78), and the receiver answers in its own invariant
        // format whatever the operator's locale is.
        if (!reply.Succeeded ||
            reply.Lines.Count == 0 ||
            !double.TryParse(reply.Lines[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
        {
            return;
        }

        // The user may have started typing during the round trip.
        if (_thresholdEdited)
        {
            return;
        }

        _seededThreshold = seconds;
        ThresholdBox.Value = seconds;

        _threshold?.Revalidate();
        Render();
    }

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

        // Read back what the receiver actually took, which need not be what was sent: the limit has
        // one-second resolution, so 90.4 becomes 90. The editor is the only place that figure is
        // shown, so leaving the sent value in it would misreport the receiver.
        _thresholdEdited = false;
        await ReadThresholdAsync().ConfigureAwait(true);
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
