using System.Globalization;

using System.ComponentModel;

using Microsoft.UI.Dispatching;
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
/// The Time page — the §10.3 clock with its workings shown.
/// </summary>
public sealed partial class TimePage : Page
{
    private TimeViewModel? _model;
    private DeviceContext? _device;
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _ready;

    /// <summary>§10.14's leap-second detail, read on arrival and on reconnect.</summary>
    private LeapSecondReading _leap = LeapSecondReading.Unknown;

    /// <summary>§10.14's time code format, read on arrival and on reconnect.</summary>
    private TimeCodeReading _timeCode = TimeCodeReading.Unknown;

    /// <summary>Cancels a read in flight when the page is left.</summary>
    private CancellationTokenSource? _reading;

    /// <summary>
    /// The one handler this page hands the dispatcher, reused for every notification (#399).
    /// </summary>
    /// <remarks>
    /// A field rather than a lambda or a method group because each of those allocates a fresh
    /// delegate, and a fresh delegate is a fresh COM wrapper the runtime can never reuse. See
    /// <see cref="MainPage"/> for the measurement.
    /// </remarks>
    private readonly DispatcherQueueHandler _render;

    /// <summary>Creates the page.</summary>
    public TimePage()
    {
        InitializeComponent();

        _render = Render;

        _ticker.Tick += (_, _) => _model?.RaiseAll();
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
        if (_device is DeviceContext device)
        {
            device.Session.StatusChanged -= OnStatusChanged;
        }

        if (_model is TimeViewModel model)
        {
            model.PropertyChanged -= OnModelChanged;
            model.Dispose();
            _model = null;
        }
    }

    /// <summary>Renders on a model notification. Named so <see cref="Detach"/> can remove it (#388).</summary>
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(_render);

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
        _model = new TimeViewModel(device.Store) { Connection = device.Session.Status };
        _model.PropertyChanged += OnModelChanged;
        device.Session.StatusChanged += OnStatusChanged;

        LoadZones();

        _ready = true;
        _ticker.Start();
        Render();

        // Read once on arrival rather than on a timer. The accumulated offset changes when a leap
        // second is applied, which is at most twice a year, and §7.3 gives the poller sole ownership
        // of anything that repeats. It is re-read on reconnect, because a different receiver may
        // have been plugged in.
        _ = ReadOnceAsync();
    }

    /// <summary>
    /// Reads the §10.14 leap-second detail, asking only what the receiver will answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The order is the point.</b> <c>ACC?</c> and <c>STAT?</c> are always safe to ask;
    /// <c>DATE?</c> and <c>DUR?</c> answer only while an announcement stands and are rejected with
    /// <c>E-230</c> otherwise. Asking all four on arrival would put two errors in the receiver's
    /// error queue every time this page was opened, and they would then surface on the Diagnostics
    /// page as if something had gone wrong. See <see cref="LeapSecondQueries"/>.
    /// </para>
    /// <para>
    /// Nothing here is a setter and nothing here is tier C. The whole card is four queries.
    /// </para>
    /// </remarks>
    private async Task ReadOnceAsync()
    {
        if (_device is not DeviceContext)
        {
            return;
        }

        _reading?.Cancel();
        _reading?.Dispose();
        _reading = new CancellationTokenSource();
        CancellationToken token = _reading.Token;

        try
        {
            await ReadLeapAsync(token).ConfigureAwait(true);
            await ReadTimeCodeAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_ready)
        {
            Render();
        }
    }

    /// <summary>Reads the leap-second detail, asking only what the receiver will answer.</summary>
    private async Task ReadLeapAsync(CancellationToken token)
    {
        try
        {
            int? accumulated = await AskAsync(LeapSecondQueries.Accumulated, token).ConfigureAwait(true);
            int? status = await AskAsync(LeapSecondQueries.Status, token).ConfigureAwait(true);

            int? direction = null;
            DateOnly? announced = null;

            if (LeapSecondQueries.NeedsAnnouncementDetail(LeapSecondQueries.Decode(status, null)))
            {
                direction = await AskAsync(LeapSecondQueries.Direction, token).ConfigureAwait(true);
                announced = LeapSecondQueries.ParseDate(
                    await AskLinesAsync(LeapSecondQueries.Date, token).ConfigureAwait(true));
            }

            _leap = new LeapSecondReading(
                accumulated,
                LeapSecondQueries.Decode(status, direction),
                announced,
                accumulated is null && status is null
                    ? "The receiver did not answer the leap-second queries."
                    : null);
        }
        catch (Exception exception) when (exception is InvalidOperationException or TransportException)
        {
            _leap = LeapSecondReading.Unknown with { Error = exception.Message };
        }
    }

    /// <summary>
    /// Reads which time code format the receiver emits (§10.14).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One query, and deliberately not the time code itself.</b> <c>:PTIM:TCOD?</c> does not
    /// answer when asked — it answers on the receiver's own 1 Hz cadence, roughly 509 ms before the
    /// 1 PPS it names, so a request lands in the next emission slot and the transaction blocks for
    /// up to a second. That is a poll budget this page has no use for, and #37 records the
    /// measurement. The format is the part that does not change and is the part a reader needs.
    /// </para>
    /// <para>
    /// Read on arrival rather than on a timer, for the same reason as the leap-second card: it is a
    /// fact about the receiver, and §7.3 gives the poller sole ownership of anything that repeats.
    /// </para>
    /// </remarks>
    private async Task ReadTimeCodeAsync(CancellationToken token)
    {
        try
        {
            IReadOnlyList<string>? lines = await AskLinesAsync(TimeCodeFormats.Query, token)
                .ConfigureAwait(true);

            _timeCode = lines is [string first, ..]
                ? new TimeCodeReading(TimeCodeFormats.Parse(first), null)
                : new TimeCodeReading(
                    TimeCodeFormat.Unknown,
                    "The receiver did not answer the time code format query.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or TransportException)
        {
            _timeCode = TimeCodeReading.Unknown with { Error = exception.Message };
        }
    }

    /// <summary>Asks one catalogued query and reads a single integer from the answer.</summary>
    private async Task<int?> AskAsync(string mnemonic, CancellationToken token) =>
        ScalarParsers.ParseInteger(
            (await AskLinesAsync(mnemonic, token).ConfigureAwait(true)) is [string first, ..] ? first : null);

    /// <summary>
    /// Asks one catalogued query and returns its lines, or null if it did not answer.
    /// </summary>
    /// <remarks>
    /// Resolved through the driver's catalog rather than sent as text: §8.1 makes the catalog an
    /// allowlist and <c>ExecuteAsync</c> takes an <see cref="ScpiCommand"/> precisely so nothing
    /// routes around it.
    /// </remarks>
    private async Task<IReadOnlyList<string>?> AskLinesAsync(string mnemonic, CancellationToken token)
    {
        if (_device is not DeviceContext device || device.Driver.Find(mnemonic) is not ScpiCommand command)
        {
            return null;
        }

        Transaction transaction = await device.Session
            .ExecuteAsync(command, cancellationToken: token)
            .ConfigureAwait(true);

        return transaction.Succeeded && transaction.Lines.Count > 0 ? transaction.Lines : null;
    }

    /// <remarks>
    /// Populated on arrival rather than in the constructor: enumerating every system zone costs
    /// more than a page nobody has opened should pay.
    /// </remarks>
    private void LoadZones()
    {
        if (ZonePicker.Items.Count > 0 || _model is null)
        {
            return;
        }

        foreach (TimeZoneInfo zone in TimeZoneInfo.GetSystemTimeZones())
        {
            ZonePicker.Items.Add(zone);
        }

        ZonePicker.DisplayMemberPath = nameof(TimeZoneInfo.DisplayName);
        ZonePicker.SelectedItem = TimeZoneInfo.GetSystemTimeZones()
            .FirstOrDefault(zone => zone.Id == _model.DisplayZone.Id);
    }

    private void OnStatusChanged(object? sender, ConnectionStatusChanged e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_model is TimeViewModel model)
            {
                model.Connection = e.Status;
            }

            // Re-read on a fresh connection: the accumulated offset is a fact about the receiver,
            // and a reconnect may be to a different one. Nothing is read while disconnected, which
            // leaves the last-known values on screen with the footer saying how old they are —
            // §9.11's rule that stale is dimmed and timestamped, never blanked.
            if (e.Status == ConnectionStatus.Connected)
            {
                _ = ReadOnceAsync();
            }
        });

    private void OnZoneSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && _model is TimeViewModel model && ZonePicker.SelectedItem is TimeZoneInfo zone)
        {
            model.DisplayZone = zone;
        }
    }

    private void Render()
    {
        if (_model is not TimeViewModel model)
        {
            return;
        }

        ShownTimeText.Text = model.ShownTimeText;
        TimeScaleText.Text = model.TimeScaleText;
        DeviceTimeText.Text = model.DeviceTimeText;

        TimeScaleNoteText.Text = model.TimeScaleNote ?? string.Empty;
        TimeScaleNoteText.Visibility = model.TimeScaleNote is null
            ? Visibility.Collapsed
            : Visibility.Visible;

        RolloverPill.Severity = model.RolloverSeverity;
        RolloverPill.Text = model.IsDateCorrected ? "Date corrected" : "No correction";
        RolloverText.Text = model.RolloverText;

        // #245. Shown only when the receiver actually marked the reading, and carrying the caveat
        // as words rather than as a colour - §9.4.3 requires severity to be colour plus shape plus
        // text, and a pill alone would not say what is provisional about it.
        ProvisionalCard.Visibility = model.IsTimeProvisional ? Visibility.Visible : Visibility.Collapsed;
        ProvisionalPill.Severity = Severity.Caution;
        ProvisionalPill.Text = "Not yet corrected from GPS";
        ProvisionalText.Text = model.ProvisionalText ?? string.Empty;

        // The pill follows the direct query where there is one, and the status screen otherwise.
        // They agree in every case seen so far; where they could not both be read, the screen is
        // the one that arrives without asking.
        LeapSecondPending pending = _leap.AccumulatedSeconds is null && _leap.Error is null
            ? model.LeapPending
            : _leap.Pending;

        LeapPill.Severity = pending == LeapSecondPending.None ? Severity.Neutral : Severity.Caution;
        LeapPill.Text = pending switch
        {
            LeapSecondPending.Plus => "A second will be inserted",
            LeapSecondPending.Minus => "A second will be removed",
            _ => "None announced",
        };

        AccumulatedText.Text = _leap.AccumulatedSeconds is int seconds
            ? $"{seconds.ToString("+0;\u22120;0", CultureInfo.CurrentCulture)}{ReadoutFormatter.HairSpace}s"
            : ReadoutFormatter.NoValue;

        bool hasDate = _leap.AnnouncedDate is DateOnly;
        AnnouncedLabel.Visibility = hasDate ? Visibility.Visible : Visibility.Collapsed;
        AnnouncedDateText.Visibility = hasDate ? Visibility.Visible : Visibility.Collapsed;
        AnnouncedDateText.Text = _leap.AnnouncedDate?.ToString("d MMM yyyy", CultureInfo.CurrentCulture)
            ?? string.Empty;

        LeapErrorText.Text = _leap.Error ?? string.Empty;
        LeapErrorText.Visibility = _leap.Error is null ? Visibility.Collapsed : Visibility.Visible;

        TimeCodeFormatText.Text = _timeCode.FormatText;

        TimeCodeContentText.Text = _timeCode.ContentText ?? string.Empty;
        TimeCodeContentText.Visibility = _timeCode.ContentText is null
            ? Visibility.Collapsed
            : Visibility.Visible;

        TimeCodeErrorText.Text = _timeCode.Error ?? string.Empty;
        TimeCodeErrorText.Visibility = _timeCode.Error is null ? Visibility.Collapsed : Visibility.Visible;

        FooterText.Text = model.AgeDescription;
    }
}
