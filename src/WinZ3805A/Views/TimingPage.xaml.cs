using System.Globalization;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using WinZ3805A.Controls;
using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Models;
using WinZ3805A.Device.Parsing;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Views;

/// <summary>
/// The §10.7 Timing &amp; Antenna page.
/// </summary>
public sealed partial class TimingPage : Page, ICsvExportSource
{
    private TimingViewModel? _model;
    private DeviceContext? _device;
    private TrendStore? _trends;
    private StabilityViewModel? _stability;

    /// <summary>The selected range, in hours. §13's four settings are 1, 6, 24 and 168.</summary>
    private int _rangeHours = 1;

    /// <summary>The window currently on screen, which is also what Export writes.</summary>
    private IReadOnlyList<TrendRecord> _exportable = [];
    private CommandInvoker? _invoker;
    /// <summary>
    /// The direct-entry validator, built in <c>OnNavigatedTo</c> rather than the constructor
    /// (#287): §10.7's range comes from the driver's catalog entry for the delay command, and
    /// there is no driver until a device arrives.
    /// </summary>
    private NumberFieldValidator? _directDelay;
    private readonly NumberFieldValidator _length;
    private readonly NumberFieldValidator _velocityFactor;
    private bool _busy;
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
        DirectDelayBox.Value = 0;

        // §9.11's validation model. The delay's bounds come from the driver's catalog entry and
        // are wired in OnNavigatedTo; the cable length's are this page's own, since no command
        // takes a length.
        _length = new NumberFieldValidator(LengthBox, LengthError, 0, 10000, "m");

        // §9.7.4's right-click layer, on the trend CARD rather than on the charts inside it. What
        // BuildCsv writes is the trend samples the charts plot; the stability figures below them
        // are summary statistics over that same data rather than a second table to copy.
        //
        // The card also has a Background where a chart does not, and an element with no Background
        // is not hit-testable at all — so a menu hung on the chart would have been unreachable in
        // exactly the way §9.6.3's hit-target work recorded for Border.
        CopyMenu.AttachCsv(TrendCard, this);

        // The velocity factor never reaches the receiver - it feeds the local calculation - but it
        // is still a number a user types and gets wrong, so it validates like the other two. It
        // used to rely on the NumberBox clamping to Minimum and Maximum instead, which replaced an
        // out-of-range entry without saying so. A physically impossible factor is worth a sentence,
        // not a silent correction: above 1 means faster than light, and the user has almost
        // certainly read a percentage off a datasheet and not divided it by a hundred.
        _velocityFactor = new NumberFieldValidator(VelocityFactorBox, VelocityFactorError, 0.01, 0.99);

        _length.ValidityChanged += (_, _) => Render();
        _velocityFactor.ValidityChanged += (_, _) => Render();

        Range1h.IsChecked = true;

        // The trend redraws on the staleness tick rather than on every fast poll. One second of
        // new data cannot move a plot whose narrowest range is an hour, and redrawing a thousand
        // strokes at 1 Hz to show it would be work nobody can see.
        _stalenessTicker.Tick += (_, _) =>
        {
            _model?.RaiseAll();
            RenderTrend();
        };
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

        // §8.3's antenna delay is the one tier C command on this page; §10.7's field range is its
        // catalog entry's, taken from the driver rather than restated here.
        _directDelay = new NumberFieldValidator(
            DirectDelayBox,
            DirectDelayError,
            CommandConfirmation.Require(device.Driver, ":GPS:REF:ADELay").Parameters[0]);
        _directDelay.ValidityChanged += (_, _) => Render();

        _trends = App.Services?.GetService<TrendStore>();

        // #63. Built here rather than per render: it owns the last computed curve, and rebuilding
        // it on every poll would recompute the whole series for a card nobody asked to refresh.
        if (_trends is TrendStore trends)
        {
            _stability = new StabilityViewModel(trends, device.TimeProvider);
            StabilityRows.ItemsSource = _stability.Rows;
            RefreshStability();
        }
        _ = ReadEfcHardwareBitsAsync();
        _invoker = new CommandInvoker(device.Session);
        _model = new TimingViewModel(device.Store) { Connection = device.Session.Status };
        _model.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(Render);
        device.Session.StatusChanged += OnStatusChanged;

        _ready = true;

        // The radios' own Checked events fired during InitializeComponent, before _ready, and were
        // ignored — so without this the card comes up with every field live regardless of which
        // radio is selected. Found by running it: the tree and the build are both happy either way.
        UpdateFieldEnablement();

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

    /// <summary>§10.7's outer choice: type the delay, or work it out from the cable.</summary>
    private void OnDelaySourceChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _model is not TimingViewModel model)
        {
            return;
        }

        model.UseDirectEntry = EnterDirectlyRadio.IsChecked == true;
        UpdateFieldEnablement();

        // A field that is not in play should not be holding the card's Apply hostage.
        if (model.UseDirectEntry)
        {
            _length.Reset();
            _directDelay?.Revalidate();
        }
        else
        {
            _directDelay?.Reset();
            _length.Revalidate();
        }
    }

    /// <summary>
    /// Enables the fields the two radio pairs between them leave in play.
    /// </summary>
    /// <remarks>
    /// One place rather than one per handler: the choices nest — velocity factor is only live when
    /// the calculator is, which is only when direct entry is not — and two handlers each setting
    /// part of it is how a control ends up enabled under a radio nobody selected.
    /// </remarks>
    private void UpdateFieldEnablement()
    {
        bool direct = EnterDirectlyRadio.IsChecked == true;
        bool custom = UseVelocityRadio.IsChecked == true;

        DirectDelayBox.IsEnabled = direct;

        UsePresetRadio.IsEnabled = !direct;
        UseVelocityRadio.IsEnabled = !direct;
        LengthBox.IsEnabled = !direct;
        CablePicker.IsEnabled = !direct && !custom;
        VelocityFactorBox.IsEnabled = !direct && custom;
    }

    private void OnDirectDelayChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_ready && _model is TimingViewModel model && !double.IsNaN(args.NewValue))
        {
            model.DirectDelayNanoseconds = args.NewValue;
        }
    }

    private void OnCableSourceChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _model is not TimingViewModel model)
        {
            return;
        }

        model.UseVelocityFactor = UseVelocityRadio.IsChecked == true;
        UpdateFieldEnablement();
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

    /// <summary>
    /// Recomputes the §13 P2-3 stability curve (#63).
    /// </summary>
    /// <remarks>
    /// Not called from <c>Render</c>. Render runs on every poll, and this walks the whole persisted
    /// series — at the 24 h window that is tens of thousands of samples across a dozen averaging
    /// times, which is real work to repeat every few seconds for a figure that cannot meaningfully
    /// change in that time. It runs when the page is opened.
    /// </remarks>
    private void RefreshStability()
    {
        if (_stability is not StabilityViewModel stability)
        {
            return;
        }

        stability.Refresh();
        StabilitySummary.Text = stability.Summary;
        StabilityHeader.Visibility = stability.HasCurve ? Visibility.Visible : Visibility.Collapsed;
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

        // A computed delay can leave the receiver's range from two valid inputs, so it gets the
        // same error line a field would - §9.11's rule is about what Apply would send, not only
        // about what was typed.
        ComputedDelayError.Message =
            !model.UseDirectEntry && model.ComputedDelayNanoseconds is not null && !model.IsComputedDelayAcceptable
                ? "This cable run is longer than the receiver can compensate for. It accepts 0 to 999,999 ns."
                : null;

        // The cable path needs its length *and*, when the custom factor is selected, the factor -
        // §9.11 disables Apply while any field in the card is invalid, and the factor was not being
        // counted because the control used to clamp it into range instead of reporting it.
        ApplyDelayButton.IsEnabled =
            !_busy
            && model.CanApplyDelay
            && (model.UseDirectEntry
                ? _directDelay is { IsValid: true }
                : _length.IsValid && (!model.UseVelocityFactor || _velocityFactor.IsValid));

        TimeInterval.Value = model.TimeIntervalNanoseconds;
        RenderDeviation();

        FooterText.Text = model.AgeDescription;
    }

    /// <summary>
    /// §10.7's σ, over the hour the wireframe asks for rather than over whatever is in memory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This read is separate from the charts' and always an hour, whatever range is selected: §10.7
    /// puts σ beside <c>Current</c> as a property of the receiver now, not of the window being
    /// drawn. A σ that changed when the user pressed <c>7 d</c> would be a different statistic
    /// wearing the same label.
    /// </para>
    /// <para>
    /// It falls back to <c>ReceiverStateStore</c>'s 60-sample ring when there is no trend store —
    /// which is the case in a session that has not been given one — so the readout is never blank
    /// for want of persistence that an installation may not have.
    /// </para>
    /// </remarks>
    private void RenderDeviation()
    {
        if (_model is not TimingViewModel model)
        {
            return;
        }

        if (_trends is TrendStore trends && _device is DeviceContext device)
        {
            long now = device.TimeProvider.GetUtcNow().UtcTicks;
            IReadOnlyList<TrendRecord> hour = trends.Read(now - TimeSpan.FromHours(1).Ticks, now);

            List<double> values = [];
            long first = 0;
            long last = 0;

            foreach (TrendRecord record in hour)
            {
                if (record.TimeIntervalNanoseconds is not double value)
                {
                    continue;
                }

                if (values.Count == 0)
                {
                    first = record.Ticks;
                }

                last = record.Ticks;
                values.Add(value);
            }

            if (values.Count >= SampleDeviation.MinimumSamples)
            {
                Deviation.Value = SampleDeviation.Of(values);
                // "from", because Describe already says "over" about the span: "σ from 2,492
                // readings over 59 minutes" rather than "σ over 2,492 readings over 59 minutes".
                DeviationWindowText.Text =
                    $"σ from {SampleDeviation.Describe(values.Count, TimeSpan.FromTicks(last - first))}.";
                return;
            }
        }

        // Nothing persisted yet. The ring buffer is what there is, and the caption says so.
        Deviation.Value = model.TimeIntervalDeviation;
        DeviationWindowText.Text = $"σ from the {model.DeviationWindow}.";
    }

    /// <summary>
    /// §8.3's antenna delay. The receiver takes seconds; §10.7's field, §8.3's confirmation and
    /// §9.11's error sentence are all in nanoseconds, so the scaling happens here, once, on the way
    /// out — and the dialog quotes the number the user actually typed.
    /// </summary>
    private async void OnApplyDelayClicked(object sender, RoutedEventArgs e)
    {
        if (_invoker is not CommandInvoker invoker
            || _model is not TimingViewModel model
            || _device is not DeviceContext device
            || model.DelayToApplyNanoseconds is not double nanoseconds
            || _busy)
        {
            return;
        }

        _busy = true;
        DelayOutcome.Clear();
        Render();

        try
        {
            string display = ReadoutFormatter.Format(nanoseconds, decimalPlaces: 1);

            DelayOutcome.Show(await CommandConfirmation.RunAsync(
                XamlRoot,
                invoker,
                CommandConfirmation.Require(device.Driver, ":GPS:REF:ADELay"),
                argument: (nanoseconds * 1e-9).ToString("0.#########E+00", CultureInfo.InvariantCulture),
                displayValue: display));
        }
        finally
        {
            _busy = false;
            Render();
        }
    }

    /// <inheritdoc />
    public event EventHandler? ExportAvailabilityChanged;

    /// <inheritdoc />
    public bool CanExport => _exportable.Count > 0;

    /// <inheritdoc />
    public string SuggestedFileName =>
        $"receiver-trend-{(_device?.TimeProvider ?? TimeProvider.System).GetLocalNow():yyyy-MM-dd-HHmm}";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The <i>selected range</i>, not the whole store, because §9.7.5 calls the command "export
    /// current view" and the range selector is the view. A user looking at one hour who exports
    /// eight weeks has been surprised.
    /// </para>
    /// <para>
    /// <b>Undecimated.</b> The chart reduces 604 800 samples to a thousand columns because a screen
    /// has a thousand columns; a file does not, and a spreadsheet fed min/max pairs would be
    /// analysing an artefact of the plot width. What decimation exists to protect on screen —
    /// the one-second excursion — is simply present here.
    /// </para>
    /// </remarks>
    public CsvDocument? BuildCsv()
    {
        if (_exportable.Count == 0)
        {
            return null;
        }

        // No CorrectedTimestamp column here, and #132 asked for that to be checked rather than
        // assumed. These ticks come from PollingService's own TimeProvider at the moment of the
        // sweep, not from anything the receiver printed, so §7.4's rollover cannot reach them. The
        // diagnostic log is the opposite case: its dates are the receiver's own, and that export
        // carries both columns.
        CsvDocument document = new("Timestamp", "TimeIntervalNs", "EfcPercent", "SyncState", "TrackedSatellites");

        foreach (TrendRecord record in _exportable)
        {
            document.AddRow(
                CsvDocument.PreciseTimestamp(new DateTime(record.Ticks, DateTimeKind.Utc)),
                CsvDocument.Number(record.TimeIntervalNanoseconds, 1),
                CsvDocument.Number(record.Efc, 2),
                record.SyncState,
                record.TrackedCount?.ToString(CultureInfo.InvariantCulture));
        }

        return document;
    }

    /// <summary>
    /// Runs #137's fit over the window on screen and reports it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Over the same window the chart is showing, so the numbers describe the picture above them.
    /// A fit over a different span from the plot would be two claims about one receiver.
    /// </para>
    /// <para>
    /// Severity comes from the pattern rather than from the slope. A steep drift on a receiver
    /// whose fix has been failing is not an oscillator warning, and §9.4.3 renders it through
    /// <c>SeverityPill</c> — colour, shape and text together — because a coloured dot asserting a
    /// hardware fault is exactly what A11Y-12 forbids.
    /// </para>
    /// </remarks>
    private void RenderDrift(IReadOnlyList<TrendRecord> window)
    {
        List<EfcSample> samples = [];
        foreach (TrendRecord record in window)
        {
            if (record.Efc is not double percent)
            {
                continue;
            }

            ReceiverMode mode = ReceiverModes.FromSyncState(record.SyncState);
            samples.Add(new EfcSample(
                record.Ticks,
                percent,
                IsPowerUp: mode == ReceiverMode.PowerUp,
                IsLocked: mode == ReceiverMode.Locked));
        }

        EfcDriftResult drift = EfcDrift.Analyse(samples);

        DriftPill.Severity = drift.Pattern switch
        {
            DriftPattern.OscillatorNearingRange => Severity.Critical,
            DriftPattern.GpsOrAntennaPath => Severity.Caution,
            DriftPattern.LoopOrReference => Severity.Caution,
            DriftPattern.NothingRemarkable => Severity.Success,
            _ => Severity.Neutral,
        };

        DriftPill.Text = drift.Pattern switch
        {
            DriftPattern.OscillatorNearingRange => "Oscillator near end of range",
            DriftPattern.GpsOrAntennaPath => "Signal path",
            DriftPattern.LoopOrReference => "Loop or reference",
            DriftPattern.NothingRemarkable => "Nothing remarkable",
            _ => "Not enough data",
        };

        DriftAdvisoryText.Text = EfcDrift.Describe(drift.Pattern);

        // The wording lives in DriftAdvisory rather than here, so it can be held against a series
        // whose answer is known. #182 was a sentence, not a fit: the arithmetic agreed with an
        // independent one to five decimal places and the card still printed 0.00 %.
        if (!drift.IsUsable)
        {
            DriftNumbersText.Text = string.Empty;
            DriftEvidenceText.Text = DriftAdvisory.NotEnough(drift);
            return;
        }

        DriftNumbersText.Text = DriftAdvisory.Numbers(
            drift,
            (_device?.TimeProvider ?? TimeProvider.System).GetLocalNow());

        DriftEvidenceText.Text = DriftAdvisory.Evidence(drift);
    }

    /// <summary>
    /// Reads the receiver's own verdict on its EFC range (#137).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Surfaced, never recomputed.</b> Hardware register bits 6 and 7 are the receiver saying
    /// "EFC voltage near full scale" and "at full scale" in its own words, documented in the 58503A
    /// reference and decoded in <c>StatusRegisterMap</c> since #34. A fit that disagreed with them
    /// would be the application arguing with the instrument.
    /// </para>
    /// <para>
    /// The <b>condition</b> field only. Reading <c>:EVENt</c> clears its latches, and doing that
    /// from a page the user came to for a chart would silently destroy history the Status Registers
    /// page exists to show them.
    /// </para>
    /// </remarks>
    private async Task ReadEfcHardwareBitsAsync()
    {
        if (_device is not DeviceContext device)
        {
            return;
        }

        ScpiCommand? command = device.Driver.Find(":STAT:OPER:HARD:COND?");
        if (command is null)
        {
            return;
        }

        try
        {
            Transaction transaction = await device.Session.ExecuteAsync(command);
            if (!transaction.Succeeded || ScalarParsers.ParseInteger(transaction.Text) is not int condition)
            {
                return;
            }

            bool near = (condition & (1 << 6)) != 0;
            bool full = (condition & (1 << 7)) != 0;

            DriftHardwareText.Text = (near, full) switch
            {
                (_, true) => "The receiver reports its EFC voltage at full scale (hardware bit 7). "
                    + "That is the instrument's own end-of-range indication, not an inference.",
                (true, _) => "The receiver reports its EFC voltage near full scale (hardware bit 6). "
                    + "That is the instrument's own warning, not an inference.",
                _ => "The receiver reports its EFC voltage within range: hardware bits 6 and 7 are "
                    + "both clear.",
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or IOException)
        {
            // A page that could not ask says nothing rather than guessing.
            DriftHardwareText.Text = string.Empty;
        }
    }

    /// <summary>Pulls one field out of a window, dropping the samples that have none.</summary>
    private static IReadOnlyList<TrendSample> Project(
        IReadOnlyList<TrendRecord> window,
        Func<TrendRecord, double?> selector)
    {
        List<TrendSample> samples = [];
        foreach (TrendRecord record in window)
        {
            if (selector(record) is double value)
            {
                samples.Add(new TrendSample(record.Ticks, value));
            }
        }

        return samples;
    }

    private void OnRangeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton button && int.TryParse((string?)button.Tag, out int hours))
        {
            _rangeHours = hours;
            RenderTrend();
        }
    }

    /// <summary>
    /// Reads the selected window out of the store and hands it to the chart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The chart decimates, so the cost of this is the query rather than the drawing: a 7-day
    /// window is about 138 000 rows after compaction and collapses to one stroke per pixel column.
    /// </para>
    /// <para>
    /// The summary line says how many samples are behind the plot, because a 7-day range drawn
    /// from four hours of history looks identical to one drawn from seven days — an empty stretch
    /// and a disconnected stretch are the same picture, and only the count tells them apart.
    /// </para>
    /// </remarks>
    private void RenderTrend()
    {
        if (_trends is not TrendStore trends || _device is not DeviceContext device)
        {
            return;
        }

        long now = device.TimeProvider.GetUtcNow().UtcTicks;
        long from = now - TimeSpan.FromHours(_rangeHours).Ticks;

        // One read, reaching EfcDrift.FitMargin further back than the charts draw, and filtered for
        // them (#184). The fit needs a span of a full day before it can separate a diurnal term,
        // and a window of exactly 24 hours holds slightly under 24 hours of span - so the range
        // named for a day could never reach the analysis the card is built around.
        //
        // Read once and narrowed rather than read twice: the 7 d range is 200 000-odd rows and the
        // second query would be almost all of the first.
        IReadOnlyList<TrendRecord> fitWindow = trends.Read(from - EfcDrift.FitMargin.Ticks, now);

        List<TrendRecord> drawn = new(fitWindow.Count);
        foreach (TrendRecord record in fitWindow)
        {
            if (record.Ticks >= from)
            {
                drawn.Add(record);
            }
        }

        IReadOnlyList<TrendRecord> window = drawn;

        // The export is what the user is looking at, not what the fit reached into.
        _exportable = window;

        IReadOnlyList<TrendSample> series = Project(window, record => record.TimeIntervalNanoseconds);
        IReadOnlyList<TrendSample> efc = Project(window, record => record.Efc);

        // The mode as a number, for the background shading. Read once and shared by both charts,
        // so the two cannot disagree about when the receiver was locked.
        IReadOnlyList<TrendSample> states = Project(
            window,
            record => (double)ReceiverModes.FromSyncState(record.SyncState));

        TimeIntervalTrend.FromTicks = from;
        TimeIntervalTrend.ToTicks = now;
        TimeIntervalTrend.States = states;
        TimeIntervalTrend.Samples = series;

        EfcTrend.FromTicks = from;
        EfcTrend.ToTicks = now;
        EfcTrend.States = states;
        EfcTrend.Samples = efc;

        // Names the shading in words as well as colour (§9.4.3, A11Y-12), and reports the count,
        // because a 7-day range drawn from four hours of history looks exactly like one drawn from
        // seven days.
        RenderDrift(fitWindow);
        ExportAvailabilityChanged?.Invoke(this, EventArgs.Empty);

        TrendSummaryText.Text = series.Count switch
        {
            0 => "No readings stored for this range yet. The trend fills as the receiver is polled.",
            1 => "1 reading stored for this range. Shaded stretches are where the receiver was not locked.",
            _ => $"{series.Count:N0} readings stored for this range. Shaded stretches are where the receiver was not locked.",
        };
    }
}
