using System.Globalization;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using WinZ3805A.Controls;
using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Views;

/// <summary>
/// The §10.7 Timing &amp; Antenna page.
/// </summary>
public sealed partial class TimingPage : Page
{
    /// <summary>§8.3's antenna delay, the one tier C command on this page.</summary>
    private static readonly ScpiCommand SetDelay = CommandConfirmation.Require(":GPS:REF:ADELay");

    /// <summary>§10.7's field range, taken from the catalog rather than restated here.</summary>
    private static readonly ParameterSpec DelayRange = SetDelay.Parameters[0];

    private TimingViewModel? _model;
    private DeviceContext? _device;
    private TrendStore? _trends;

    /// <summary>The selected range, in hours. §13's four settings are 1, 6, 24 and 168.</summary>
    private int _rangeHours = 1;
    private CommandInvoker? _invoker;
    private readonly NumberFieldValidator _directDelay;
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

        // §9.11's validation model. The delay's bounds come from the catalog entry the command is
        // built from; the cable length's are this page's own, since no command takes a length.
        _directDelay = new NumberFieldValidator(DirectDelayBox, DirectDelayError, DelayRange);
        _length = new NumberFieldValidator(LengthBox, LengthError, 0, 10000, "m");

        // The velocity factor never reaches the receiver - it feeds the local calculation - but it
        // is still a number a user types and gets wrong, so it validates like the other two. It
        // used to rely on the NumberBox clamping to Minimum and Maximum instead, which replaced an
        // out-of-range entry without saying so. A physically impossible factor is worth a sentence,
        // not a silent correction: above 1 means faster than light, and the user has almost
        // certainly read a percentage off a datasheet and not divided it by a hundred.
        _velocityFactor = new NumberFieldValidator(VelocityFactorBox, VelocityFactorError, 0.01, 0.99);

        _directDelay.ValidityChanged += (_, _) => Render();
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
        _trends = App.Services?.GetService<TrendStore>();
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
            _directDelay.Revalidate();
        }
        else
        {
            _directDelay.Reset();
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
                ? _directDelay.IsValid
                : _length.IsValid && (!model.UseVelocityFactor || _velocityFactor.IsValid));

        TimeInterval.Value = model.TimeIntervalNanoseconds;
        Deviation.Value = model.TimeIntervalDeviation;

        DeviationWindowText.Text = $"σ over the {model.DeviationWindow}. "
            + "The specification's one-hour window needs the trend history that arrives with P1 persistence.";

        FooterText.Text = model.AgeDescription;
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
                SetDelay,
                argument: (nanoseconds * 1e-9).ToString("0.#########E+00", CultureInfo.InvariantCulture),
                displayValue: display));
        }
        finally
        {
            _busy = false;
            Render();
        }
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

        IReadOnlyList<TrendSample> series =
            trends.ReadSeries(from, now, record => record.TimeIntervalNanoseconds);

        TimeIntervalTrend.FromTicks = from;
        TimeIntervalTrend.ToTicks = now;
        TimeIntervalTrend.Samples = series;

        TrendSummaryText.Text = series.Count switch
        {
            0 => "No readings stored for this range yet. The trend fills as the receiver is polled.",
            1 => "1 reading stored for this range.",
            _ => $"{series.Count:N0} readings stored for this range.",
        };
    }
}
