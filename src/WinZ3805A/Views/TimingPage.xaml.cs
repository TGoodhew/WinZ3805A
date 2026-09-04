using System.ComponentModel;
using System.Globalization;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using WinZ3805A.Controls;
using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Drivers;
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

    /// <summary>Whether the connected receiver's driver offers the antenna delay (#304).</summary>
    private bool _canSetDelay;
    private readonly NumberFieldValidator _length;
    private readonly NumberFieldValidator _velocityFactor;
    private bool _busy;
    /// <summary>UTC ticks of the last trend redraw, or 0 for never (#389).</summary>
    private long _trendRenderedTicks;

    /// <summary>
    /// The fit window, kept between redraws and extended rather than read again (#389).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The store is append-only</b>, which is what makes this sound: rows arrive with increasing
    /// ticks and nothing rewrites history, so a redraw needs the records since the last one plus
    /// whatever has aged out of the front - never the whole window again.
    /// </para>
    /// <para>
    /// <b>Why it is worth caching rather than re-reading.</b> A 1 h window is about 3,600 records,
    /// which is 200 KB - over the 85 KB large-object threshold, so every re-read put another large
    /// object on a heap that is not compacted, thirteen times a minute at the throttled rate. The
    /// increment between two redraws is five seconds of data: under 300 bytes, and nowhere near the
    /// LOH. Measured before this: 2.67 MB/min of working set with the throttle already in.
    /// </para>
    /// </remarks>
    private readonly List<TrendRecord> _window = [];

    /// <summary>The newest tick in <see cref="_window"/>, or 0 when it is empty (#389).</summary>
    /// <remarks>
    /// The newest RECORD's tick rather than the time of the last read. A record can be appended
    /// with a tick a little behind the moment the read happened, and starting the next read from
    /// "now" would step over it - a dropped sample that would never come back, because the next
    /// read starts later still.
    /// </remarks>
    private long _windowToTicks;

    /// <summary>Which range <see cref="_window"/> was built for, so a change to it re-reads (#389).</summary>
    private int _windowRangeHours;

    /// <summary>
    /// The lists a redraw fills, kept and refilled rather than rebuilt (#389).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Clear()</c> keeps a list's capacity, so after the first redraw at a given range these
    /// allocate nothing at all. Rebuilt each time, they were four large objects per redraw - the
    /// drawn window at about 200 KB on the 1 h range and three projections at 58 KB each - every
    /// one of them over the 85 KB large-object threshold, thirteen times a minute.
    /// </para>
    /// <para>
    /// <b>Handing the chart a list this page goes on to refill is safe because both happen on the
    /// UI thread</b>, and a redraw clears and refills within one synchronous block: there is no
    /// moment at which a layout pass can observe a half-filled list.
    /// </para>
    /// </remarks>
    private readonly List<TrendRecord> _drawn = [];
    private readonly List<TrendSample> _series = [];
    private readonly List<TrendSample> _efc = [];
    private readonly List<TrendSample> _states = [];
    private readonly List<EfcSample> _drift = [];

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

    /// <summary>
    /// The one handler this page hands the dispatcher, reused for every notification (#399).
    /// </summary>
    /// <remarks>
    /// A field rather than a lambda or a method group so the hop allocates nothing. See
    /// <see cref="MainPage"/> for why that is hygiene and not the fix, and for what the leak
    /// in #399 actually turned out to be.
    /// </remarks>
    private readonly DispatcherQueueHandler _render;

    /// <summary>1 while a render is already queued, so a burst costs one (#399).</summary>
    private int _renderQueued;

    /// <summary>Creates the page.</summary>
    public TimingPage()
    {
        InitializeComponent();

        _render = () =>
        {
            Interlocked.Exchange(ref _renderQueued, 0);
            Render();
        };

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
        //
        // THAT REASONING WAS RIGHT AND THE TICK WAS STILL TOO OFTEN (#389). A second of new data
        // cannot move an hour-wide plot, and neither can four: the chart decimates to one column
        // per pixel, so nothing changes until a whole column of time has passed - 4.6 s on the 1 h
        // range, two minutes on 7 d. Measured with the ticker unthrottled and this page on screen,
        // the working set still climbed 7.5 MB a minute after #387 and #388 had fixed everything
        // else (#385).
        _stalenessTicker.Tick += (_, _) =>
        {
            _model?.RaiseAll();
            RenderTrendIfItWouldShowAnything();
        };
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

        // The cached window goes with it (#389). A page that has been away would otherwise hold a
        // 7 d window - megabytes - for as long as it is not being looked at, which is the thing
        // #388 was about; and its next redraw would extend a window with a hole in the middle.
        _window.Clear();
        _window.TrimExcess();
        _drawn.Clear();
        _drawn.TrimExcess();
        _series.Clear();
        _series.TrimExcess();
        _efc.Clear();
        _efc.TrimExcess();
        _states.Clear();
        _states.TrimExcess();
        _drift.Clear();
        _drift.TrimExcess();
        _windowToTicks = 0;
        _windowRangeHours = 0;

        if (_device is DeviceContext device)
        {
            device.Session.StatusChanged -= OnStatusChanged;
        }

        if (_model is TimingViewModel model)
        {
            model.PropertyChanged -= OnModelChanged;
            model.Dispose();
            _model = null;
        }
    }

    /// <summary>Renders on a model notification. Named so <see cref="Detach"/> can remove it (#388).</summary>
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // One hop and one render per burst (#399). The store raises about seven notifications per
        // sweep and Render rewrites everything, so six of them repaint what the seventh is about
        // to - and each repaint marshals boxed values into WinRT, minting a COM wrapper the
        // runtime appends to a list that never shrinks.
        if (Interlocked.Exchange(ref _renderQueued, 1) == 1)
        {
            return;
        }

        if (!DispatcherQueue.TryEnqueue(_render))
        {
            Interlocked.Exchange(ref _renderQueued, 0);
        }
    }

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

        // §8.3's antenna delay is the one tier C command on this page; §10.7's field range is its
        // catalog entry's, taken from the driver rather than restated here.
        // A talker has no antenna-delay command (#304), so the spec is looked up rather than
        // required and the field is disabled below instead of the navigation throwing.
        _directDelay = new NumberFieldValidator(
            DirectDelayBox, DirectDelayError, minimum: null, maximum: null);
        _directDelay.ValidityChanged += (_, _) => Render();

        BindDriver();

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
        _model.PropertyChanged += OnModelChanged;
        device.Session.StatusChanged += OnStatusChanged;

        _ready = true;

        // The radios' own Checked events fired during InitializeComponent, before _ready, and were
        // ignored — so without this the card comes up with every field live regardless of which
        // radio is selected. Found by running it: the tree and the build are both happy either way.
        UpdateFieldEnablement();

        _stalenessTicker.Start();
        Render();
    }

    /// <summary>
    /// A stored sync token, read by the driver that is on the port now (#304).
    /// </summary>
    /// <remarks>
    /// The honest limit of colouring history by mode: <c>trend.db</c> records the receiver's own
    /// token, and which mode that means is a fact about the family. A window spanning a swap to a
    /// different family would read the older half in the newer family's vocabulary — which is why
    /// the token and not the mode is what gets stored, so the reading can be redone rather than
    /// having been baked in wrong.
    /// </remarks>
    private ReceiverMode InterpretSyncState(string? syncState) =>
        _device?.Driver.InterpretSyncState(syncState) ?? ReceiverMode.Disconnected;

    private void OnStatusChanged(object? sender, ConnectionStatusChanged e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_model is TimingViewModel model)
            {
                model.Connection = e.Status;
            }

            if (e?.Status == ConnectionStatus.Connected)
            {
                // The receiver on the port can have been swapped while the link was down, so the
                // session re-selects a driver on every connect (#287) and this page's answer to
                // "what may I offer" has to be asked again rather than kept from navigation (#304).
                BindDriver();
                Render();
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

        _canSetDelay = Capability.Offers(driver, ":GPS:REF:ADELay");
        _directDelay?.Rebind(Capability.SpecFor(driver, ":GPS:REF:ADELay"));
    }

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
        // Capability first, then state (#304).
        DelayUnsupportedText.Text = _canSetDelay
            ? string.Empty
            : Capability.NotOffered(_device?.Driver, "an antenna delay");
        DelayUnsupportedText.Visibility = _canSetDelay ? Visibility.Collapsed : Visibility.Visible;

        ApplyDelayButton.IsEnabled =
            _canSetDelay
            && !_busy
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
        // Refilled rather than rebuilt, like the three projections above (#389): this walks the FIT
        // window, which is the widest of the lot.
        _drift.Clear();
        List<EfcSample> samples = _drift;
        foreach (TrendRecord record in window)
        {
            if (record.Efc is not double percent)
            {
                continue;
            }

            ReceiverMode mode = InterpretSyncState(record.SyncState);
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

    /// <summary>
    /// Brings <see cref="_window"/> up to date, reading only what is new (#389).
    /// </summary>
    /// <param name="trends">The store.</param>
    /// <param name="fitFrom">The oldest tick the window should hold.</param>
    /// <param name="now">The newest.</param>
    /// <remarks>
    /// <para>
    /// Falls back to reading the whole window in the two cases where an increment would be wrong:
    /// the range changed, so the window is a different window; or the cache is empty or stale
    /// enough that its newest record is older than the new front, which is the shape of a page that
    /// has been away — there would be a hole in the middle otherwise, and a hole in a trend is
    /// indistinguishable from a receiver that stopped answering.
    /// </para>
    /// <para>
    /// <b>What this does not handle, deliberately.</b> Compaction thins rows older than 24 h, and a
    /// cached window keeps the detail it read before that happened; it draws more than the store
    /// still holds, which is harmless. And an <c>Append</c> that updates a tick already cached is
    /// not seen — the store upserts, but the poll loop writes each instant once, so that is a
    /// theoretical case rather than one to complicate this for.
    /// </para>
    /// </remarks>
    private IReadOnlyList<TrendRecord> ExtendWindow(TrendStore trends, long fitFrom, long now)
    {
        bool sameRange = _windowRangeHours == _rangeHours;
        bool contiguous = _window.Count > 0 && _windowToTicks >= fitFrom;

        if (!sameRange || !contiguous)
        {
            _window.Clear();
            _window.AddRange(trends.Read(fitFrom, now));
            _windowRangeHours = _rangeHours;
        }
        else
        {
            // Strictly after the newest record we hold: the store's key is the tick, so a record
            // at that tick is already here and one after it is not.
            foreach (TrendRecord record in trends.Read(_windowToTicks + 1, now))
            {
                _window.Add(record);
            }

            // And drop what has fallen off the back. RemoveRange shifts what remains rather than
            // allocating, which is the point — this list is the thing being kept.
            int aged = 0;
            while (aged < _window.Count && _window[aged].Ticks < fitFrom)
            {
                aged++;
            }

            if (aged > 0)
            {
                _window.RemoveRange(0, aged);
            }
        }

        _windowToTicks = _window.Count > 0 ? _window[^1].Ticks : 0;
        return _window;
    }

    /// <summary>
    /// Pulls one field out of a window into a buffer the caller keeps, dropping the samples that
    /// have none (#389).
    /// </summary>
    /// <remarks>
    /// Fills rather than returns: a fresh list here was a large object three times per redraw. The
    /// buffer's capacity survives <c>Clear()</c>, so this settles at one allocation per range.
    /// </remarks>
    private static IReadOnlyList<TrendSample> Project(
        List<TrendSample> buffer,
        IReadOnlyList<TrendRecord> window,
        Func<TrendRecord, double?> selector)
    {
        buffer.Clear();
        foreach (TrendRecord record in window)
        {
            if (selector(record) is double value)
            {
                buffer.Add(new TrendSample(record.Ticks, value));
            }
        }

        return buffer;
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
    /// <summary>
    /// Renders the trend only if a whole pixel column of new data has arrived since the last one
    /// (#389).
    /// </summary>
    /// <remarks>
    /// Everything that wants the trend brought up to date on a schedule comes through here. The two
    /// callers that must draw regardless - the first render, and a range change, which moves the
    /// window rather than extending it - call <see cref="RenderTrend"/> directly.
    /// </remarks>
    private void RenderTrendIfItWouldShowAnything()
    {
        if (_device is not DeviceContext device)
        {
            return;
        }

        if (!TrendRefreshPolicy.ShouldRedraw(
                _trendRenderedTicks,
                device.TimeProvider.GetUtcNow().UtcTicks,
                TimeSpan.FromHours(_rangeHours),
                TimeIntervalTrend.ActualWidth))
        {
            return;
        }

        RenderTrend();
    }

    private void RenderTrend()
    {
        if (_trends is not TrendStore trends || _device is not DeviceContext device)
        {
            return;
        }

        long now = device.TimeProvider.GetUtcNow().UtcTicks;
        _trendRenderedTicks = now;
        long from = now - TimeSpan.FromHours(_rangeHours).Ticks;

        // One window, reaching EfcDrift.FitMargin further back than the charts draw, and filtered
        // for them (#184). The fit needs a span of a full day before it can separate a diurnal
        // term, and a window of exactly 24 hours holds slightly under 24 hours of span - so the
        // range named for a day could never reach the analysis the card is built around.
        //
        // Extended rather than re-read (#389). See _window.
        IReadOnlyList<TrendRecord> fitWindow = ExtendWindow(trends, from - EfcDrift.FitMargin.Ticks, now);

        _drawn.Clear();
        foreach (TrendRecord record in fitWindow)
        {
            if (record.Ticks >= from)
            {
                _drawn.Add(record);
            }
        }

        IReadOnlyList<TrendRecord> window = _drawn;

        // The export is what the user is looking at, not what the fit reached into.
        _exportable = window;

        IReadOnlyList<TrendSample> series = Project(_series, window, record => record.TimeIntervalNanoseconds);
        IReadOnlyList<TrendSample> efc = Project(_efc, window, record => record.Efc);

        // The mode as a number, for the background shading. Read once and shared by both charts,
        // so the two cannot disagree about when the receiver was locked.
        IReadOnlyList<TrendSample> states = Project(
            _states,
            window,
            record => (double)InterpretSyncState(record.SyncState));

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
