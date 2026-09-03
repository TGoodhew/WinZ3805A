using System.Globalization;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using WinZ3805A.Controls;
using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Views;

/// <summary>
/// The §10.4 Overview page.
/// </summary>
/// <remarks>
/// Rendered imperatively for the same reason <c>MainPage</c> is: several of these drive severity
/// pills and visual states rather than plain properties, and splitting the mechanisms across one
/// surface is harder to follow than doing all of it in one place.
/// </remarks>
public sealed partial class OverviewPage : Page
{
    private OverviewViewModel? _model;
    private DeviceContext? _device;
    private CommandInvoker? _invoker;
    private readonly DispatcherTimer _stalenessTicker = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>#285's trend store, or null when it could not be resolved.</summary>
    /// <remarks>
    /// Optional on purpose. The card's readout is useful without a history behind it, so a store
    /// that will not resolve costs the plot and nothing else - the same judgement the Timing page
    /// makes about the same dependency.
    /// </remarks>
    private TrendStore? _trends;

    /// <summary>The selected span in hours. Six by default, which is §10.4's.</summary>
    /// <remarks>
    /// Deliberately not shared with the Timing page's selection. §10.4 names six hours here and
    /// Timing defaults to one, so a single shared setting would have to discard one of the two
    /// specified defaults every time a user moved between the pages.
    /// </remarks>
    private int _rangeHours = 6;

    /// <summary>True while the self-test is running, so a second click cannot queue a second one.</summary>
    private bool _testRunning;

    /// <summary>UTC ticks of the last trend redraw, or 0 for never (#387).</summary>
    private long _trendRenderedTicks;

    /// <summary>Creates the page.</summary>
    public OverviewPage()
    {
        InitializeComponent();

        // The footer counts up while nothing arrives, which is exactly when its number matters.
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
        _model = new OverviewViewModel(device.Store, device.Driver)
        {
            Connection = device.Session.Status,

            // P0-1's identity comes from the session rather than the store: it belongs to the link,
            // not to the status screen, and is read once at connect.
            Identity = device.Session.ParsedIdentity,
            RawIdentity = device.Session.Identity,
        };
        // Render on every notification; the TREND only when a redraw could show something (#387).
        //
        // Both used to run here, and the trend is the expensive one by a wide margin: it reads the
        // whole selected window out of SQLite and decimates it, where Render() rewrites a few
        // readouts. Notifications arrive at least once a second - the ticker below raises them so
        // the staleness footer counts up - and each one was a full 6 h read. Measured consequence
        // in #385: 36 MB/s allocated, 1.1 GB of large object heap, a working set climbing 8.9 MB a
        // minute for ten hours with no ceiling.
        _model.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            Render();
            RenderTrendIfItWouldShowAnything();
        });
        device.Session.StatusChanged += OnStatusChanged;

        _trends = App.Services?.GetService<TrendStore>();

        // Checked here rather than bound in XAML: setting IsChecked in markup would raise Checked
        // during InitializeComponent, before _device exists, and RenderTrend would return having
        // done nothing - leaving the default range selected but never drawn.
        OverviewRange6h.IsChecked = true;

        _stalenessTicker.Start();
        Render();
        RenderTrend();
    }

    private void OnStatusChanged(object? sender, ConnectionStatusChanged e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_model is OverviewViewModel model)
            {
                model.Connection = e.Status;

                // Re-set on every connect, not captured once (#287, #304).
                if (_device is DeviceContext current)
                {
                    model.Driver = current.Driver;
                }

                // Re-read on every status change, not only on the first: a reconnect can find a
                // different receiver on the port, and the session re-selects its driver for exactly
                // that reason (§12). An identity card that kept the first unit's serial number
                // would be wrong in the one situation where it matters.
                if (_device is DeviceContext device)
                {
                    model.Identity = device.Session.ParsedIdentity;
                    model.RawIdentity = device.Session.Identity;
                }
            }
        });

    private void Render()
    {
        if (_model is not OverviewViewModel model)
        {
            return;
        }

        Medallion.Mode = model.Mode;
        Medallion.Samples = model.TimeIntervalSamples;
        Medallion.SatelliteCount = model.SatelliteCount;
        Medallion.TimeIntervalNanoseconds = model.TimeIntervalNanoseconds;
        Medallion.ModeDetail = model.ModeDetail;

        ModeText.Text = model.ModeText;
        ModeDetailText.Text = model.ModeDetail ?? string.Empty;
        ModeDetailText.Visibility = string.IsNullOrEmpty(model.ModeDetail)
            ? Visibility.Collapsed
            : Visibility.Visible;

        CoastingPill.Visibility = model.IsCoasting ? Visibility.Visible : Visibility.Collapsed;

        OutputsPill.Severity = model.OutputsSeverity;
        OutputsPill.Text = model.OutputsText;

        RenderMerit(TfomPill, TfomDetailText, "TFOM", model.Tfom, model.TfomDetail);
        RenderMerit(FfomPill, FfomDetailText, "FFOM", model.Ffom, model.FfomDetail);
        ToolTipService.SetToolTip(FfomPill, model.FfomTooltip);

        TimeInterval.Value = model.TimeIntervalNanoseconds;

        HoldoverPredictedText.Text = WithUnit(model.HoldoverPredicted);
        HoldoverThresholdText.Text = WithUnit(model.HoldoverThreshold);
        HoldoverDurationText.Text = model.HoldoverDuration;

        HealthSummaryText.Text = model.HealthSummary;
        HealthItems.ItemsSource = model.Health.Select(BuildHealthPill).ToList();

        OscillatorControl.Value = model.OscillatorControl;

        // P0-1's identity card.
        IdentityModelText.Text = model.IdentityModel;
        IdentityManufacturerText.Text = model.IdentityManufacturer;
        IdentitySerialText.Text = model.IdentitySerialNumber;
        IdentityFirmwareText.Text = model.IdentityFirmware;

        IdentityRawText.Text = model.UnparsedIdentity ?? string.Empty;
        IdentityRawText.Visibility = model.UnparsedIdentity is null ? Visibility.Collapsed : Visibility.Visible;

        FooterText.Text = model.AgeDescription;

        // Capability first, then state (#304).
        bool canSelfTest = Capability.Offers(_device?.Driver, "*TST?");

        RunTestButton.IsEnabled =
            canSelfTest && !_testRunning && model.Connection == ConnectionStatus.Connected;

        SelfTestUnsupportedText.Text = canSelfTest
            ? string.Empty
            : Capability.NotOffered(_device?.Driver, "a self test");
        SelfTestUnsupportedText.Visibility = canSelfTest ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// §8.3's self-test. Everything about what the user is told, and whether it runs at all, is
    /// <see cref="CommandConfirmation"/>'s; this only knows how to read the number that comes back.
    /// </summary>
    private async void OnRunTestClicked(object sender, RoutedEventArgs e)
    {
        if (_invoker is not CommandInvoker invoker || _device is not DeviceContext device || _testRunning)
        {
            return;
        }

        _testRunning = true;
        RunTestButton.IsEnabled = false;
        SelfTestOutcome.Clear();

        // The one tier C command on this page (§8.3), resolved through the driver (#287).
        // A talker has no self-test (#304). The button is gated in Render, so reaching here with the
        // command absent would be a gating bug - but the handler is async void and an exception in
        // one has nowhere to go, so it returns instead of asserting.
        if (device.Driver.Find("*TST?") is not ScpiCommand selfTest)
        {
            return;
        }

        try
        {
            CommandOutcome? outcome = await CommandConfirmation.RunAsync(XamlRoot, invoker, selfTest);
            SelfTestOutcome.Show(outcome, DescribeSelfTest(outcome));
        }
        finally
        {
            _testRunning = false;
            Render();
        }
    }

    /// <summary>
    /// Reads <c>*TST?</c>'s answer. IEEE 488.2 defines zero as "no fault found" and leaves every
    /// other value to the instrument, so a non-zero result is reported as the number it was rather
    /// than translated into a fault this application cannot name.
    /// </summary>
    private static string? DescribeSelfTest(CommandOutcome? outcome)
    {
        if (outcome is not { Succeeded: true } result || result.Lines.Count == 0)
        {
            return null;
        }

        string answer = result.Lines[0].Trim();
        return int.TryParse(answer, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code)
            ? code == 0 ? "It reported no faults." : $"It reported result {code}, which is a fault."
            : $"It answered \"{answer}\".";
    }

    /// <remarks>
    /// The pill carries the number; the caption underneath carries what the number means. §9.4.3
    /// needs the severity in text as well as colour, and "TFOM 3" is that text — the range below it
    /// is the part a user could not have worked out.
    /// </remarks>
    private static void RenderMerit(SeverityPill pill, TextBlock caption, string label, int? value, string detail)
    {
        pill.Text = value is int merit ? $"{label} {merit}" : $"{label} {ReadoutFormatter.NoValue}";
        pill.Severity = OverviewViewModel.SeverityOfMerit(value);
        caption.Text = detail;
    }

    /// <remarks>
    /// A pill per item, built here rather than through a DataTemplate because <c>SeverityPill</c>
    /// takes an enum and never a brush (P0-19) and a template binding would need a converter that
    /// could be handed anything.
    /// </remarks>
    private static SeverityPill BuildHealthPill(HealthItem item)
    {
        SeverityPill pill = new() { Severity = item.Severity, Text = item.Name };
        AutomationProperties.SetName(pill, $"{item.Name}: {(item.IsOk ? "passing" : "failing")}");
        return pill;
    }

    private static string WithUnit((string Value, string Unit) reading) =>
        reading.Unit.Length == 0
            ? reading.Value
            : $"{reading.Value}{ReadoutFormatter.HairSpace}{reading.Unit}";

    private void OnRangeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton button && int.TryParse((string?)button.Tag, out int hours))
        {
            _rangeHours = hours;
            RenderTrend();
        }
    }

    /// <summary>
    /// Reads the selected window out of the store and hands it to the EFC chart (#285).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The chart decimates by min/max per pixel column rather than by sampling, so a 7-day range
    /// keeps a one-second excursion and the cost of this is the query rather than the drawing.
    /// </para>
    /// <para>
    /// Narrower than the Timing page's equivalent by design: no drift fit, so no reading further
    /// back than the chart draws, and one series rather than two.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Renders the trend only if a whole pixel column of new data has arrived since the last one
    /// (#387).
    /// </summary>
    /// <remarks>
    /// The chart decimates to one column per pixel (§9.10.2), so on this page's default 6 h range
    /// over a 700 px chart nothing on it can change more than once every 31 seconds. Everything that
    /// wants the trend brought up to date goes through here; the two callers that must draw
    /// regardless — the first render and a range change — call <see cref="RenderTrend"/> directly.
    /// </remarks>
    private void RenderTrendIfItWouldShowAnything()
    {
        if (_device is not DeviceContext device)
        {
            return;
        }

        long now = device.TimeProvider.GetUtcNow().UtcTicks;

        if (!TrendRefreshPolicy.ShouldRedraw(
                _trendRenderedTicks, now, TimeSpan.FromHours(_rangeHours), EfcTrend.ActualWidth))
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

        IReadOnlyList<TrendRecord> window = trends.Read(from, now);

        List<TrendSample> efc = new(window.Count);
        List<TrendSample> states = new(window.Count);
        foreach (TrendRecord record in window)
        {
            if (record.Efc is double value)
            {
                efc.Add(new TrendSample(record.Ticks, value));
            }

            // The stored token read by the driver that is on the port now (#304). History from a
            // different family would be read in the wrong vocabulary, which is the honest limit of
            // colouring a chart by mode: the token is what was recorded, not the mode.
            states.Add(new TrendSample(
                record.Ticks,
                (double)(_device?.Driver.InterpretSyncState(record.SyncState)
                    ?? ReceiverMode.Disconnected)));
        }

        EfcTrend.FromTicks = from;
        EfcTrend.ToTicks = now;
        EfcTrend.States = states;
        EfcTrend.Samples = efc;

        TrendSummaryText.Text = efc.Count switch
        {
            0 => "No readings stored for this range yet. The trend fills as the receiver is polled.",
            1 => "1 reading stored for this range. Shaded stretches are where the receiver was not locked.",
            _ => $"{efc.Count:N0} readings stored for this range. Shaded stretches are where the receiver was not locked.",
        };
    }
}
