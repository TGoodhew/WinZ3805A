using System.ComponentModel;
using System.IO.Ports;
using System.Runtime.CompilerServices;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;

namespace WinZ3805A.ViewModels;

/// <summary>
/// The §10.12 connection dialog: which port, on what settings, and what to remember about it.
/// </summary>
/// <remarks>
/// <para>
/// Everything the dialog decides lives here rather than in its code-behind, because all of it is
/// worth testing and none of it needs a window: which port comes back selected after a refresh,
/// whether Connect is offerable, what the progress line says on the fifth of eight probes, and
/// which of §9.11's error rows a failure belongs to.
/// </para>
/// <para>
/// Plain <see cref="INotifyPropertyChanged"/> for the same reason as
/// <see cref="MainViewModel"/> — no dependency the headless test project cannot take.
/// </para>
/// </remarks>
public sealed class ConnectionViewModel : INotifyPropertyChanged
{
    private readonly DeviceSessionService _session;
    private readonly ISerialPortSource _ports;
    private readonly IConnectionPreferenceStore _preferences;

    /// <summary>The attempt in flight, or null between attempts.</summary>
    /// <remarks>
    /// Nulled when the attempt ends because <see cref="Cancel"/> reaches through it, and the source
    /// is disposed by then — cancelling a disposed one throws. Identity for the progress callback is
    /// <see cref="_generation"/> instead, which is a different question and needs a different answer.
    /// </remarks>
    private volatile CancellationTokenSource? _attempt;

    /// <summary>Which attempt is the current one. Incremented when a new attempt starts, never reset.</summary>
    /// <remarks>
    /// <para>
    /// <b>The progress callback asks "is this still my attempt", not "is my attempt still running"</b>
    /// — and the difference is #213. <c>Progress&lt;T&gt;</c> delivery is not ordered against the
    /// task <see cref="ConnectAsync"/> awaits, so the last candidate's line routinely arrives after
    /// the walk has finished. Declining it, as the first version of #198's guard did, meant the
    /// eighth of eight was simply never painted.
    /// </para>
    /// <para>
    /// Painting it late is harmless: <c>ConnectionDialog</c> collapses the whole progress area when
    /// <see cref="IsBusy"/> goes false, so nothing is on screen to be wrong. What is <i>not</i>
    /// harmless is a line from a previous attempt landing during the next one, which is what this
    /// still declines.
    /// </para>
    /// <para>
    /// Not reset to zero, so a generation number is never reused and a callback held up across two
    /// attempts cannot find its own number again.
    /// </para>
    /// </remarks>
    private volatile int _generation;

    private IReadOnlyList<SerialPortInfo> _available = [];
    private SerialPortInfo? _selectedPort;
    private string? _portsMessage;
    private bool _isAutoDetect;
    private int _baudRate;
    private int _dataBits;
    private Parity _parity;
    private int _stopBitCount;
    private bool _reconnectAutomatically;
    private bool _connectOnLaunch;
    private bool _isBusy;
    private string? _progressText;
    private string? _errorMessage;

    /// <summary>Creates a view model over a session, a port source and a preference store.</summary>
    public ConnectionViewModel(
        DeviceSessionService session,
        ISerialPortSource ports,
        IConnectionPreferenceStore preferences)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(preferences);

        _session = session;
        _ports = ports;
        _preferences = preferences;

        Apply(preferences.Load());
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The baud rates §7.1 permits.</summary>
    public IReadOnlyList<int> BaudRateOptions => SerialSettings.SupportedBaudRates;

    /// <summary>The data-bit counts §7.1 permits.</summary>
    public IReadOnlyList<int> DataBitOptions => SerialSettings.SupportedDataBits;

    /// <summary>
    /// The parities §7.1 permits.
    /// </summary>
    /// <remarks>
    /// Three of the five <see cref="Parity"/> members, deliberately. Mark and Space are valid
    /// RS-232 and no SmartClock unit uses them, so offering them would only be a way to fail to
    /// connect.
    /// </remarks>
    public IReadOnlyList<Parity> ParityOptions { get; } = [Parity.None, Parity.Even, Parity.Odd];

    /// <summary>
    /// The stop-bit counts §7.1 permits, as the numbers the §10.12 wireframe shows.
    /// </summary>
    /// <remarks>
    /// Counts rather than <see cref="StopBits"/> members because the picker renders what it is given
    /// and <c>StopBits.One</c> renders as "One". The wireframe says "1", instrument documentation
    /// says 1, and the receiver's own front panel says 1.
    /// </remarks>
    public IReadOnlyList<int> StopBitOptions { get; } = [1, 2];

    /// <summary>The ports currently on offer.</summary>
    public IReadOnlyList<SerialPortInfo> AvailablePorts
    {
        get => _available;
        private set => Set(ref _available, value);
    }

    /// <summary>The port the user has picked.</summary>
    public SerialPortInfo? SelectedPort
    {
        get => _selectedPort;
        set
        {
            if (Set(ref _selectedPort, value))
            {
                OnPropertyChanged(nameof(CanConnect));
            }
        }
    }

    /// <summary>Why the port list is empty, or <see langword="null"/> when it is not (§9.11, §6.1).</summary>
    public string? PortsMessage
    {
        get => _portsMessage;
        private set => Set(ref _portsMessage, value);
    }

    /// <summary>Whether the dialog is on Auto-detect rather than Manual.</summary>
    public bool IsAutoDetect
    {
        get => _isAutoDetect;
        set
        {
            if (Set(ref _isAutoDetect, value))
            {
                OnPropertyChanged(nameof(IsManual));
                OnPropertyChanged(nameof(CanEditSettings));
            }
        }
    }

    /// <summary>The other half of the §10.12 radio pair.</summary>
    public bool IsManual
    {
        get => !_isAutoDetect;
        set => IsAutoDetect = !value;
    }

    /// <summary>Baud rate for a manual connection.</summary>
    public int BaudRate
    {
        get => _baudRate;
        set => Set(ref _baudRate, value);
    }

    /// <summary>Data bits for a manual connection.</summary>
    public int DataBits
    {
        get => _dataBits;
        set => Set(ref _dataBits, value);
    }

    /// <summary>Parity for a manual connection.</summary>
    public Parity Parity
    {
        get => _parity;
        set => Set(ref _parity, value);
    }

    /// <summary>Stop bits for a manual connection, as a count of 1 or 2.</summary>
    public int StopBitCount
    {
        get => _stopBitCount;
        set => Set(ref _stopBitCount, value);
    }

    /// <summary>"Reconnect automatically" (§10.12), which drives the §7.2 retry policy.</summary>
    public bool ReconnectAutomatically
    {
        get => _reconnectAutomatically;
        set => Set(ref _reconnectAutomatically, value);
    }

    /// <summary>"Connect to this device on launch" (§10.12).</summary>
    public bool ConnectOnLaunch
    {
        get => _connectOnLaunch;
        set => Set(ref _connectOnLaunch, value);
    }

    /// <summary>Whether an attempt is running, which is when Cancel means "stop" rather than "close".</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanConnect));
                OnPropertyChanged(nameof(CanEditSettings));
                OnPropertyChanged(nameof(CanChoosePort));
            }
        }
    }

    /// <summary>The §10.12 progress line, or <see langword="null"/> when nothing is in flight.</summary>
    public string? ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    /// <summary>What went wrong, in §9.11's words, or <see langword="null"/>.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => Set(ref _errorMessage, value);
    }

    /// <summary>Whether Connect is offerable.</summary>
    public bool CanConnect => SelectedPort is not null && !IsBusy;

    /// <summary>Whether the manual line settings are editable.</summary>
    public bool CanEditSettings => IsManual && !IsBusy;

    /// <summary>Whether the port picker and its Refresh are usable.</summary>
    public bool CanChoosePort => !IsBusy;

    /// <summary>The line parameters a manual connection would use.</summary>
    public SerialSettings ManualSettings => new()
    {
        BaudRate = BaudRate,
        DataBits = DataBits,
        Parity = Parity,
        StopBits = StopBitCount == 2 ? StopBits.Two : StopBits.One,
    };

    /// <summary>
    /// Re-reads the port list, keeping the user's choice if the port is still there.
    /// </summary>
    /// <remarks>
    /// Selection falls back in the order that costs the user least: the port they had chosen, then
    /// the port they last connected to, then the first in the list. Clearing the selection on every
    /// refresh would punish the user for plugging something in.
    /// </remarks>
    public async Task RefreshPortsAsync(CancellationToken cancellationToken = default)
    {
        string? wanted = SelectedPort?.PortName ?? _preferences.Load().PortName;

        IReadOnlyList<SerialPortInfo> found = await _ports.ListAsync(cancellationToken).ConfigureAwait(false);

        AvailablePorts = found;
        PortsMessage = found.Count == 0 ? _ports.EmptyMessage : null;
        SelectedPort = found.FirstOrDefault(port =>
            string.Equals(port.PortName, wanted, StringComparison.OrdinalIgnoreCase))
            ?? found.FirstOrDefault();
    }

    /// <summary>
    /// Connects, by whichever of the two routes the dialog is set to.
    /// </summary>
    /// <returns>True when the receiver answered.</returns>
    /// <remarks>
    /// The attempt is cancellable throughout, as §10.12 requires: auto-detect's worst case is eight
    /// probes at two seconds each, and a user who has realised they picked the wrong port should not
    /// have to wait out the other seven.
    /// </remarks>
    public async Task<bool> ConnectAsync()
    {
        if (SelectedPort is not SerialPortInfo port || IsBusy)
        {
            return false;
        }

        using CancellationTokenSource attempt = new();
        _attempt = attempt;

        // Cleared before the area becomes visible, not after the last attempt ended. That is the
        // symptom #198 was actually reaching for: a late line can still land after a walk finishes,
        // and without this it would be sitting there when the progress area next opens (#213).
        //
        // Deliberately untested, and worth saying so rather than leaving the next reader to find it
        // uncovered and delete it. Reaching the state it clears needs a Progress<T> callback that
        // arrives after its own walk returned, and nothing in the fixture can force that: the
        // session is real, only the transports are faked, so the delivery timing is the runtime's.
        // A test written for it passes whether or not this line is here, which is worse than none.
        ProgressText = null;

        IsBusy = true;
        ErrorMessage = null;
        _session.StayConnected = ReconnectAutomatically;

        int generation = ++_generation;

        try
        {
            bool connected = IsAutoDetect
                ? await AutoDetectAsync(port.PortName, generation, attempt.Token).ConfigureAwait(false)
                : await ManualConnectAsync(port.PortName, attempt.Token).ConfigureAwait(false);

            if (connected)
            {
                Remember(port.PortName);
            }
            else
            {
                ErrorMessage = FailureMessage(
                    _session.LastFault,
                    port.PortName,
                    IsAutoDetect,
                    IsAutoDetect ? null : ManualSettings);
            }

            return connected;
        }
        catch (OperationCanceledException)
        {
            // Cancelling is a decision, not a failure: §9.11 has no error copy for it because the
            // user already knows what happened.
            return false;
        }
        finally
        {
            _attempt = null;
            ProgressText = null;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Connects without showing the dialog, when §10.12's "Connect to this device on launch" was
    /// ticked for a port that is still present.
    /// </summary>
    /// <returns>True when the receiver answered.</returns>
    /// <remarks>
    /// The remembered port and no other. <see cref="RefreshPortsAsync"/> falls back to the first
    /// port in the list, which is right for a user looking at the picker and wrong here — silently
    /// opening whatever else happens to be plugged in is not what the checkbox says, and on a bench
    /// the other port is as likely to be a synthesiser as a receiver.
    /// </remarks>
    public async Task<bool> ConnectOnLaunchAsync(CancellationToken cancellationToken = default)
    {
        ConnectionPreferences saved = _preferences.Load();
        if (!saved.ConnectOnLaunch || string.IsNullOrWhiteSpace(saved.PortName))
        {
            return false;
        }

        await RefreshPortsAsync(cancellationToken).ConfigureAwait(false);

        if (SelectedPort is null
            || !string.Equals(SelectedPort.PortName, saved.PortName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return await ConnectAsync().ConfigureAwait(false);
    }

    /// <summary>Stops an attempt in flight (§10.12).</summary>
    public void Cancel() => _attempt?.Cancel();

    /// <summary>Loads the given preferences into the dialog's fields.</summary>
    public void Apply(ConnectionPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        IsAutoDetect = preferences.AutoDetect;
        BaudRate = preferences.BaudRate;
        DataBits = preferences.DataBits;
        Parity = preferences.Parity;
        StopBitCount = preferences.StopBits == StopBits.Two ? 2 : 1;
        ReconnectAutomatically = preferences.ReconnectAutomatically;
        ConnectOnLaunch = preferences.ConnectOnLaunch;
    }

    /// <summary>
    /// The §9.11 copy for a connection that did not happen.
    /// </summary>
    /// <param name="fault">How the port itself failed, if it did.</param>
    /// <param name="portName">The port that was tried.</param>
    /// <param name="autoDetect">Whether the eight-combination walk was used.</param>
    /// <param name="settings">The settings tried, for the manual case.</param>
    /// <remarks>
    /// Static and fully parameterised so every row can be asserted without a serial port in the
    /// machine. The copy follows §9.11's rules literally: what happened, then what to do next, with
    /// no apology and no "something went wrong". The "No permission" wording is quoted from the
    /// state matrix rather than paraphrased — it is specified text, not a suggestion.
    /// </remarks>
    public static string FailureMessage(
        TransportFault fault,
        string portName,
        bool autoDetect,
        SerialSettings? settings) => fault switch
    {
        TransportFault.AccessDenied =>
            $"Windows wouldn't let the app open {portName}. Another program may have it open. "
            + "Close it, then try again.",

        // This took the machine's architecture and named a missing ARM64 driver here. §6.1 no
        // longer describes ARM64 as a target (amended 29 Aug 2026), so there is one message, and
        // reconnecting the adapter is the right first step whatever the machine.
        TransportFault.PortNotFound =>
            $"Windows no longer reports {portName}. Reconnect the adapter, then choose Refresh.",

        TransportFault.DeviceRemoved =>
            $"{portName} disappeared while it was being opened. Reconnect the adapter, then choose Refresh.",

        _ when autoDetect =>
            $"No receiver answered on {portName} at any supported setting. "
            + "Check that the receiver is powered on and that the cable is a null-modem type.",

        _ =>
            $"No receiver answered on {portName} at {settings}. Check the settings against the "
            + "receiver's front panel, or use Auto-detect.",
    };

    /// <remarks>
    /// <para>
    /// <b>The callback checks that the attempt it belongs to is still the current one</b> (#198,
    /// #213). <c>Progress&lt;T&gt;</c> posts to the captured synchronization context, or to the
    /// thread pool when there is none, and either way it is <i>not</i> ordered against the task
    /// <see cref="ConnectAsync"/> is awaiting — so the last candidate's line routinely arrives after
    /// the walk has already finished.
    /// </para>
    /// <para>
    /// <b>That late line is allowed, and the first version of this guard was wrong to refuse it.</b>
    /// <c>ConnectionDialog</c> collapses the whole progress area when <see cref="IsBusy"/> goes
    /// false, so a line painted after the walk is not on screen to be wrong — while refusing it meant
    /// the eighth of eight was never painted at all, which is a real gap in §10.12's count.
    /// </para>
    /// <para>
    /// What must not paint is a line from a <i>previous</i> attempt landing during the next one, and
    /// that is what the generation check declines. The stale-text symptom the original guard was
    /// reaching for is handled where it actually shows: <see cref="ConnectAsync"/> clears the line
    /// when an attempt starts, before the area becomes visible again.
    /// </para>
    /// </remarks>
    private async Task<bool> AutoDetectAsync(
        string portName,
        int generation,
        CancellationToken cancellationToken)
    {
        int attempt = 0;
        // The session's plan, not the transport's static: with more than one driver registered the
        // walk is their union, and a count read from one family's list would finish at "8 of 8"
        // with probes still running (#287).
        int total = _session.AutoDetectPlan.Count;

        Progress<SerialSettings> progress = new(candidate =>
        {
            attempt++;

            // Its own attempt, however late — see _generation. A line from a *previous* attempt is
            // what must not paint, and that is what this declines.
            if (_generation == generation)
            {
                ProgressText = $"Trying {candidate} — {attempt} of {total}";
            }
        });

        return await _session.AutoDetectAsync(portName, progress, cancellationToken).ConfigureAwait(false)
            is not null;
    }

    private async Task<bool> ManualConnectAsync(string portName, CancellationToken cancellationToken)
    {
        SerialSettings settings = ManualSettings;
        ProgressText = $"Connecting to {portName} at {settings}";

        return await _session.ConnectAsync(portName, settings, cancellationToken).ConfigureAwait(false);
    }

    /// <remarks>
    /// The settings stored are the ones that <em>worked</em>, not the ones that were asked for. After
    /// an auto-detect those differ, and writing back what the walk found is what lets a later manual
    /// connection to the same receiver open on the right parameters.
    /// </remarks>
    private void Remember(string portName) => _preferences.Save(new ConnectionPreferences
    {
        PortName = portName,
        AutoDetect = IsAutoDetect,
        ReconnectAutomatically = ReconnectAutomatically,
        ConnectOnLaunch = ConnectOnLaunch,
    }.WithSettings(_session.Settings));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
