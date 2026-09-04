using System.Globalization;

using System.ComponentModel;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using WinZ3805A.Controls;
using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Drivers;
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

    /// <summary>What the connected receiver's driver offers (#304), decided once per navigation.</summary>
    /// <remarks>
    /// Fields rather than properties on the view model, because these are facts about the DRIVER and
    /// the view model is about the receiver's state. A talker has none of §10.8's commands, and the
    /// page has to read as a different instrument rather than as a broken one.
    /// </remarks>
    private bool _canSetDurationLimit;
    private bool _canForceHoldover;
    private bool _canRecover;

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
    public HoldoverPage()
    {
        InitializeComponent();

        _renders = new RenderCoalescer(EnqueueRender);

        _render = () =>
        {
            _renders.Begin();
            Render();
        };

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
        Unloaded += (_, _) => Detach();
    }

    /// <summary>Undoes everything <see cref="OnNavigatedTo"/> subscribed to (#388).</summary>
    /// <remarks>
    /// Idempotent: both <c>Unloaded</c> and <see cref="OnNavigatedFrom"/> call it, and neither is
    /// reliable alone. Disposing the model is the half that matters - it is what lets go of the
    /// store, which outlives every page and was keeping this one alive after it left the screen.
    /// </remarks>
    private void Detach()
    {
        _stalenessTicker.Stop();

        if (_device is DeviceContext device)
        {
            device.Session.StatusChanged -= OnStatusChanged;
        }

        if (_model is HoldoverViewModel model)
        {
            model.PropertyChanged -= OnModelChanged;
            model.Dispose();
            _model = null;
        }
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
    /// after it left the screen, once per visit. Four visits to Overview left four of them.
    /// </remarks>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Detach();
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

        // §8.3's holdover duration limit, with its range taken from the driver's catalog when the
        // driver has one. A talker has none of these commands (#304), so the spec is looked up
        // rather than required and the field is disabled below instead of the navigation throwing.
        _threshold = new NumberFieldValidator(ThresholdBox, ThresholdError, minimum: null, maximum: null);
        _threshold.ValidityChanged += (_, _) => Render();

        BindDriver();

        _model = new HoldoverViewModel(device.Store, device.Driver)
        {
            Connection = device.Session.Status,
            PowerUp = device.PowerUp,
        };
        _model.PropertyChanged += OnModelChanged;
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

                // Re-set on every connect, not captured once (#287, #304).
                if (_device is DeviceContext current)
                {
                    model.Driver = current.Driver;
                }
            }

            if (e?.Status == ConnectionStatus.Connected)
            {
                // The receiver on the port can have been swapped while the link was down, so the
                // session re-selects a driver on every connect (#287) and this page's answer to
                // "what may I offer" has to be asked again rather than kept from navigation (#304).
                BindDriver();
                Render();

                _ = ReadThresholdAsync();
            }
        });

    /// <summary>
    /// Re-reads everything this page takes from the connected receiver's driver (#304).
    /// </summary>
    /// <remarks>
    /// Called at navigation and again on every connect. Nothing here subscribes or allocates a
    /// validator: <see cref="NumberFieldValidator.Rebind"/> exists so the bounds can move without
    /// a second validator being left listening to the same field.
    /// </remarks>
    private void BindDriver()
    {
        IReceiverDriver? driver = _device?.Driver;

        _canSetDurationLimit = Capability.Offers(driver, ":SYNC:HOLD:DUR:THReshold");
        _canForceHoldover = Capability.Offers(driver, ":SYNC:HOLDover:INITiate");
        _canRecover = Capability.Offers(driver, ":SYNC:HOLD:REC:INIT", ":SYNC:HOLD:REC:LIM:IGN");

        _threshold?.Rebind(Capability.SpecFor(driver, ":SYNC:HOLD:DUR:THReshold"));
    }

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

        // The sentence is on the card and on the pill (#345). Both, because they answer for
        // different readers: the caption is what makes the row legible at a glance, and the tooltip
        // is what a pointer asks of the pill itself.
        ThresholdExplanationText.Text = model.ThresholdExplanation;
        ToolTipService.SetToolTip(ThresholdPill, model.ThresholdExplanation);

        // Both, and for the same reason as above: the caption is what makes the field legible
        // without touching anything, and the tooltip is what the pointer asks of the control it is
        // already over. The editor gets it too, not just the button - the question "what does this
        // number do" is asked at the box.
        DurationLimitExplanationText.Text = model.DurationLimitExplanation;
        ToolTipService.SetToolTip(ThresholdBox, model.DurationLimitExplanation);

        PowerUpText.Text = model.PowerUpText;
        PowerUpPill.Severity = model.PowerUpSeverity;
        PowerUpPill.Text = model.PowerUpVerdictText;

        // "Too soon" without a horizon reads as a fault rather than a wait (#345), so the tooltip
        // says when it stops being too soon.
        PowerUpExplanationText.Text = model.PowerUpExplanation;
        ToolTipService.SetToolTip(PowerUpPill, model.PowerUpExplanation);

        // Capability first, then state (#304). A receiver that cannot be told to hold over should
        // show the control disabled with a reason rather than enabled and failing on click.
        ThresholdBox.IsEnabled = _canSetDurationLimit;
        MaskUnsupportedText.Text = _canSetDurationLimit
            ? string.Empty
            : Capability.NotOffered(_device?.Driver, "a holdover duration limit");
        MaskUnsupportedText.Visibility = _canSetDurationLimit ? Visibility.Collapsed : Visibility.Visible;

        ApplyDurationLimitButton.IsEnabled = _canSetDurationLimit
            && !_busy && _threshold is { IsValid: true } && model.Connection == ConnectionStatus.Connected;

        ManualUnsupportedText.Text = _canForceHoldover || _canRecover
            ? string.Empty
            : Capability.NotOffered(_device?.Driver, "manual holdover");
        ManualUnsupportedText.Visibility = _canForceHoldover || _canRecover
            ? Visibility.Collapsed
            : Visibility.Visible;

        ForceHoldoverButton.IsEnabled = _canForceHoldover && !_busy && model.CanForceHoldover;
        RecoverButton.IsEnabled = _canRecover && !_busy && model.CanRecover;
        IgnoreLimitButton.IsEnabled = _canRecover && !_busy && model.CanRecover;

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
