using System.IO.Ports;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Time.Testing;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;
using WinZ3805A.Tests.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// The §10.12 connection dialog's decisions (P0-1): which port comes back selected, what the
/// progress line says, which of §9.11's rows a failure belongs to, and what is remembered.
/// </summary>
/// <remarks>
/// Driven through <see cref="ControllableTransport"/> for the reason its own remarks give — the
/// connect path reads before it writes, so a transport that only speaks when spoken to leaves it
/// waiting on a timeout that a pinned clock never delivers. A port at the wrong line settings is
/// modelled as one that answers with rubbish rather than one that says nothing, which is both what
/// the hardware does and what keeps an eight-combination walk instant in a test.
/// </remarks>
public sealed class ConnectionViewModelTests
{
    private const string Identity = "SYMMETRICOM,Z3805A,3625A02931,1.01.03-A";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static ControllableTransport Receiver() =>
        new(command => command.StartsWith("*IDN", StringComparison.OrdinalIgnoreCase) ? Identity : "LOCK")
        {
            Banner = Identity,
        };

    private static ControllableTransport WrongSettings() => new(_ => "ÿþ garbage");

    private static SerialPortInfo Port(string name, string? description = null) =>
        new() { PortName = name, Description = description };

    // -------------------------------------------------------------------------------------
    // The port list
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task RefreshingSelectsTheRememberedPort()
    {
        using Fixture fixture = new(
            ports: [Port("COM1"), Port("COM3", "USB Serial Port")],
            stored: new ConnectionPreferences { PortName = "COM3" });

        await fixture.Model.RefreshPortsAsync();

        Assert.Equal(2, fixture.Model.AvailablePorts.Count);
        Assert.Equal("COM3", fixture.Model.SelectedPort?.PortName);
        Assert.True(fixture.Model.CanConnect);
        Assert.Null(fixture.Model.PortsMessage);
    }

    [Fact]
    public async Task RefreshingFallsBackToTheFirstPortWhenTheRememberedOneIsGone()
    {
        using Fixture fixture = new(
            ports: [Port("COM1"), Port("COM4")],
            stored: new ConnectionPreferences { PortName = "COM3" });

        await fixture.Model.RefreshPortsAsync();

        Assert.Equal("COM1", fixture.Model.SelectedPort?.PortName);
    }

    /// <remarks>
    /// Refresh exists so the user can plug something in without reopening the dialog. Clearing what
    /// they had chosen would make it punish the thing it is for.
    /// </remarks>
    [Fact]
    public async Task RefreshingKeepsTheUsersChoiceOverTheRememberedOne()
    {
        using Fixture fixture = new(
            ports: [Port("COM1"), Port("COM3")],
            stored: new ConnectionPreferences { PortName = "COM3" });

        await fixture.Model.RefreshPortsAsync();
        fixture.Model.SelectedPort = fixture.Model.AvailablePorts[0];
        fixture.Ports.Available = [Port("COM1"), Port("COM3"), Port("COM7")];

        await fixture.Model.RefreshPortsAsync();

        Assert.Equal("COM1", fixture.Model.SelectedPort?.PortName);
    }

    [Fact]
    public async Task AnEmptyListExplainsItselfAndOffersNoConnection()
    {
        using Fixture fixture = new(ports: []);

        await fixture.Model.RefreshPortsAsync();

        Assert.Null(fixture.Model.SelectedPort);
        Assert.False(fixture.Model.CanConnect);
        Assert.Equal(FakePortSource.Empty, fixture.Model.PortsMessage);
    }

    // -------------------------------------------------------------------------------------
    // Connecting
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task AutoDetectConnectsAndRemembersTheSettingsThatWorked()
    {
        using Fixture fixture = new(
            ports: [Port("COM3")],
            stored: new ConnectionPreferences { AutoDetect = true, BaudRate = 1200 });
        fixture.Transports.Enqueue(Receiver());

        await fixture.Model.RefreshPortsAsync();
        bool connected = await fixture.Model.ConnectAsync().WaitAsync(TestTimeout);

        Assert.True(connected);

        // What is stored is what answered, not what the dialog was showing beforehand: a later
        // manual connection to the same receiver has to open on parameters that work.
        ConnectionPreferences saved = fixture.Store.Load();
        Assert.Equal("COM3", saved.PortName);
        Assert.True(saved.AutoDetect);
        Assert.Equal(9600, saved.BaudRate);
        Assert.Equal(8, saved.DataBits);
        Assert.Equal(Parity.None, saved.Parity);
        Assert.Equal(StopBits.One, saved.StopBits);
    }

    [Fact]
    public async Task ManualConnectUsesTheChosenLineSettings()
    {
        using Fixture fixture = new(ports: [Port("COM3")]);
        fixture.Transports.Enqueue(Receiver());

        await fixture.Model.RefreshPortsAsync();
        fixture.Model.IsManual = true;
        fixture.Model.BaudRate = 19200;
        fixture.Model.DataBits = 7;
        fixture.Model.Parity = Parity.Even;
        fixture.Model.StopBitCount = 2;

        Assert.True(await fixture.Model.ConnectAsync().WaitAsync(TestTimeout));

        SerialSettings used = Assert.Single(fixture.Requested).Settings;
        Assert.Equal(19200, used.BaudRate);
        Assert.Equal(7, used.DataBits);
        Assert.Equal(Parity.Even, used.Parity);
        Assert.Equal(StopBits.Two, used.StopBits);
    }

    /// <remarks>
    /// §10.12 requires progress for the eight-combination walk, and the count is the part that makes
    /// it progress rather than decoration — "trying 9600-8-N-1" alone never says how much is left.
    /// </remarks>
    [Fact]
    public async Task AutoDetectReportsEachCombinationAsItIsTried()
    {
        using Fixture fixture = new(ports: [Port("COM3")]);
        for (int probe = 0; probe < SerialSettings.AutoDetectSequence.Count; probe++)
        {
            fixture.Transports.Enqueue(WrongSettings());
        }

        List<string> progress = [];
        fixture.Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionViewModel.ProgressText)
                && fixture.Model.ProgressText is string line)
            {
                progress.Add(line);
            }
        };

        await fixture.Model.RefreshPortsAsync();
        Assert.False(await fixture.Model.ConnectAsync().WaitAsync(TestTimeout));

        // Waited for, not assumed (#213). Progress<T> posts its callbacks and does not order them
        // against the task ConnectAsync awaits, so the last candidate's line routinely arrives just
        // after the walk finishes. Reading the list the instant the walk returns is asking whether
        // the eighth line happened to have been delivered *yet* — which it had, about 24 times in
        // 25. This waits for the mechanism instead of racing it, and still asserts all eight.
        using (CancellationTokenSource settle = new(TestTimeout))
        {
            while (progress.Count < 8 && !settle.IsCancellationRequested)
            {
                await Task.Delay(5, CancellationToken.None);
            }
        }

        // Second is the Z3801A's DOCUMENTED factory default, odd parity - it was even here, and
        // eighth, until 28 Aug 2026. Reading the guide moved it (#64).
        Assert.Equal(8, progress.Count);
        Assert.Equal("Trying 9600-8-N-1 — 1 of 8", progress[0]);
        Assert.Equal("Trying 19200-7-O-1 — 2 of 8", progress[1]);
        Assert.Equal("Trying 9600-7-O-1 — 8 of 8", progress[7]);
    }

    /// <summary>
    /// The last candidate is still reported when its callback is delivered after the walk (#213).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the race, forced rather than waited for.</b> #213 is a flake because
    /// <c>Progress&lt;T&gt;</c> delivery is not ordered against the task <c>ConnectAsync</c> awaits,
    /// and on a fast machine the eighth callback happens to land in time — 60 consecutive runs of
    /// this suite reproduced it zero times either before or after the fix, so a repeat-until-red
    /// loop is not evidence of anything here.
    /// </para>
    /// <para>
    /// <c>Progress&lt;T&gt;</c> captures <see cref="SynchronizationContext.Current"/> when it is
    /// constructed and posts every report there, so a context that holds its posts decides the
    /// question outright: the walk finishes with nothing delivered, and the reports are released
    /// afterwards. That is the late delivery that only sometimes occurs by itself.
    /// </para>
    /// <para>
    /// Under the guard this replaces — which compared against the live attempt, nulled by the
    /// <c>finally</c> — every one of the eight is declined and this reports nothing at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ACandidateReportedAfterTheWalkEndsIsStillItsOwnAttempt()
    {
        SynchronizationContext? original = SynchronizationContext.Current;
        HoldingContext held = new();
        SynchronizationContext.SetSynchronizationContext(held);

        try
        {
            using Fixture fixture = new(ports: [Port("COM3")]);
            for (int probe = 0; probe < SerialSettings.AutoDetectSequence.Count; probe++)
            {
                fixture.Transports.Enqueue(WrongSettings());
            }

            List<string?> progress = [];
            fixture.Model.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ConnectionViewModel.ProgressText)
                    && fixture.Model.ProgressText is string line)
                {
                    progress.Add(line);
                }
            };

            await fixture.Model.RefreshPortsAsync();
            Assert.False(await fixture.Model.ConnectAsync().WaitAsync(TestTimeout));

            // The walk is over and the finally has run, and not one report has been delivered.
            Assert.Empty(progress);

            held.Release();

            Assert.Equal(SerialSettings.AutoDetectSequence.Count, progress.Count);
            Assert.Equal("Trying 9600-8-N-1 — 1 of 8", progress[0]);
            Assert.EndsWith("8 of 8", progress[^1], StringComparison.Ordinal);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }
    }

    /// <summary>
    /// A context that queues everything posted to it until it is told to let go.
    /// </summary>
    /// <remarks>
    /// Only <see cref="Post"/> is held. <c>Send</c> is synchronous by contract and holding it
    /// would deadlock its caller; nothing in this path uses it. Awaits inside the code under test
    /// resume through the default scheduler because the production path is
    /// <c>ConfigureAwait(false)</c> throughout, which is why holding every post does not stall the
    /// walk itself — only the progress reports, which is the point.
    /// </remarks>
    private sealed class HoldingContext : SynchronizationContext
    {
        private readonly List<(SendOrPostCallback Callback, object? State)> _held = [];

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_held)
            {
                _held.Add((d, state));
            }
        }

        public void Release()
        {
            (SendOrPostCallback Callback, object? State)[] pending;
            lock (_held)
            {
                pending = [.. _held];
                _held.Clear();
            }

            foreach ((SendOrPostCallback callback, object? state) in pending)
            {
                callback(state);
            }
        }
    }

    [Fact]
    public async Task TheProgressLineAndTheBusyFlagAreClearedWhenTheAttemptEnds()
    {
        using Fixture fixture = new(ports: [Port("COM3")]);
        fixture.Transports.Enqueue(Receiver());

        await fixture.Model.RefreshPortsAsync();
        await fixture.Model.ConnectAsync().WaitAsync(TestTimeout);

        Assert.False(fixture.Model.IsBusy);
        Assert.Null(fixture.Model.ProgressText);
        Assert.True(fixture.Model.CanConnect);
    }

    [Fact]
    public async Task ConnectingDoesNothingWithoutAPort()
    {
        using Fixture fixture = new(ports: []);

        Assert.False(await fixture.Model.ConnectAsync().WaitAsync(TestTimeout));
        Assert.Empty(fixture.Requested);
    }

    [Fact]
    public async Task TheReconnectCheckboxDrivesTheRetryPolicy()
    {
        using Fixture fixture = new(ports: [Port("COM3")]);
        fixture.Transports.Enqueue(Receiver());

        await fixture.Model.RefreshPortsAsync();
        fixture.Model.ReconnectAutomatically = false;
        await fixture.Model.ConnectAsync().WaitAsync(TestTimeout);

        Assert.False(fixture.Session.StayConnected);
    }

    // -------------------------------------------------------------------------------------
    // Failures, and the §9.11 copy for them
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task APortNobodyAnsweredOnSaysSoRatherThanNothing()
    {
        using Fixture fixture = new(ports: [Port("COM3")]);
        for (int probe = 0; probe < SerialSettings.AutoDetectSequence.Count; probe++)
        {
            fixture.Transports.Enqueue(WrongSettings());
        }

        await fixture.Model.RefreshPortsAsync();
        Assert.False(await fixture.Model.ConnectAsync().WaitAsync(TestTimeout));

        Assert.Contains("No receiver answered on COM3", fixture.Model.ErrorMessage!, StringComparison.Ordinal);
    }

    /// <remarks>
    /// A port Windows will not open fails identically at all eight settings, so walking the rest
    /// only delays a message that has nothing to do with baud rates.
    /// </remarks>
    [Fact]
    public async Task APortHeldByAnotherProgramIsReportedAtOnceInTheWordsOf911()
    {
        using Fixture fixture = new(ports: [Port("COM3")]);
        fixture.OnOpen = () => throw new UnauthorizedAccessException("Access to the port is denied.");

        await fixture.Model.RefreshPortsAsync();
        Assert.False(await fixture.Model.ConnectAsync().WaitAsync(TestTimeout));

        Assert.Equal(
            "Windows wouldn't let the app open COM3. Another program may have it open. "
            + "Close it, then try again.",
            fixture.Model.ErrorMessage);
        Assert.Single(fixture.Requested);
    }

    [Theory]
    [InlineData(TransportFault.AccessDenied, "Another program may have it open")]
    [InlineData(TransportFault.PortNotFound, "Reconnect the adapter")]
    [InlineData(TransportFault.DeviceRemoved, "disappeared")]
    [InlineData(TransportFault.None, "eight standard settings")]
    public void EachFailureGetsItsOwnRowOfTheStateMatrix(TransportFault fault, string expected)
    {
        string message = ConnectionViewModel.FailureMessage(
            fault, "COM3", autoDetect: true, settings: null, Architecture.X64);

        Assert.Contains("COM3", message, StringComparison.Ordinal);
        Assert.Contains(expected, message, StringComparison.Ordinal);
    }

    [Fact]
    public void AVanishedPortOnArm64PointsAtTheDriver()
    {
        string message = ConnectionViewModel.FailureMessage(
            TransportFault.PortNotFound, "COM3", autoDetect: true, settings: null, Architecture.Arm64);

        Assert.Contains("ARM64", message, StringComparison.Ordinal);
        Assert.Contains("Device Manager", message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The manual case names the settings that were tried. Without them the message cannot be acted
    /// on — the whole point of choosing manually is that the user has a hypothesis about them.
    /// </remarks>
    [Fact]
    public void AManualFailureNamesTheSettingsItTried()
    {
        SerialSettings tried = new() { BaudRate = 19200, DataBits = 7, Parity = Parity.Even };

        string message = ConnectionViewModel.FailureMessage(
            TransportFault.None, "COM3", autoDetect: false, tried, Architecture.X64);

        Assert.Contains("19200-7-E-1", message, StringComparison.Ordinal);
        Assert.Contains("Auto-detect", message, StringComparison.Ordinal);
    }

    /// <remarks>§9.11's copy rules: no apology, no first person, and an instruction to follow.</remarks>
    [Theory]
    [InlineData(TransportFault.AccessDenied)]
    [InlineData(TransportFault.PortNotFound)]
    [InlineData(TransportFault.DeviceRemoved)]
    [InlineData(TransportFault.None)]
    public void NoFailureMessageApologises(TransportFault fault)
    {
        string message = ConnectionViewModel.FailureMessage(
            fault, "COM3", autoDetect: true, settings: null, Architecture.X64);

        Assert.DoesNotContain("Sorry", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Oops", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Something went wrong", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" we ", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// Cancelling is a decision, not a fault. §9.11 has no error row for it because the user already
    /// knows what happened, and showing one would read as the app having failed to obey.
    /// </remarks>
    [Fact]
    public async Task CancellingLeavesNoErrorBehind()
    {
        using Fixture fixture = new(ports: [Port("COM3")]);
        fixture.OnOpen = fixture.Model.Cancel;

        await fixture.Model.RefreshPortsAsync();
        Assert.False(await fixture.Model.ConnectAsync().WaitAsync(TestTimeout));

        Assert.Null(fixture.Model.ErrorMessage);
        Assert.False(fixture.Model.IsBusy);
    }

    // -------------------------------------------------------------------------------------
    // Connect on launch
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task ConnectOnLaunchOpensTheRememberedPort()
    {
        using Fixture fixture = new(
            ports: [Port("COM1"), Port("COM3")],
            stored: new ConnectionPreferences { PortName = "COM3", ConnectOnLaunch = true });
        fixture.Transports.Enqueue(Receiver());

        Assert.True(await fixture.Model.ConnectOnLaunchAsync().WaitAsync(TestTimeout));
        Assert.Equal("COM3", Assert.Single(fixture.Requested).Port);
    }

    [Fact]
    public async Task ConnectOnLaunchDoesNothingWhenTheBoxIsClear()
    {
        using Fixture fixture = new(
            ports: [Port("COM3")],
            stored: new ConnectionPreferences { PortName = "COM3", ConnectOnLaunch = false });

        Assert.False(await fixture.Model.ConnectOnLaunchAsync().WaitAsync(TestTimeout));
        Assert.Empty(fixture.Requested);
    }

    [Fact]
    public async Task ConnectOnLaunchDoesNothingBeforeThereHasEverBeenAConnection()
    {
        using Fixture fixture = new(
            ports: [Port("COM3")],
            stored: ConnectionPreferences.Default);

        Assert.False(await fixture.Model.ConnectOnLaunchAsync().WaitAsync(TestTimeout));
        Assert.Empty(fixture.Requested);
    }

    /// <remarks>
    /// The remembered port and no other. On a bench the port that took its place is as likely to be
    /// a synthesiser as a receiver, and the checkbox says "this device".
    /// </remarks>
    [Fact]
    public async Task ConnectOnLaunchRefusesToSubstituteAnotherPort()
    {
        using Fixture fixture = new(
            ports: [Port("COM1")],
            stored: new ConnectionPreferences { PortName = "COM3", ConnectOnLaunch = true });

        Assert.False(await fixture.Model.ConnectOnLaunchAsync().WaitAsync(TestTimeout));
        Assert.Empty(fixture.Requested);
    }

    // -------------------------------------------------------------------------------------
    // Fields
    // -------------------------------------------------------------------------------------

    [Fact]
    public void TheRadioPairIsOneChoice()
    {
        using Fixture fixture = new(ports: []);

        fixture.Model.IsAutoDetect = true;
        Assert.False(fixture.Model.IsManual);
        Assert.False(fixture.Model.CanEditSettings);

        fixture.Model.IsManual = true;
        Assert.False(fixture.Model.IsAutoDetect);
        Assert.True(fixture.Model.CanEditSettings);
    }

    [Fact]
    public void TheDialogOpensOnWhatWasStored()
    {
        using Fixture fixture = new(
            ports: [],
            stored: new ConnectionPreferences
            {
                AutoDetect = false,
                BaudRate = 19200,
                DataBits = 7,
                Parity = Parity.Even,
                StopBits = StopBits.Two,
                ReconnectAutomatically = false,
                ConnectOnLaunch = false,
            });

        Assert.True(fixture.Model.IsManual);
        Assert.Equal(19200, fixture.Model.BaudRate);
        Assert.Equal(7, fixture.Model.DataBits);
        Assert.Equal(Parity.Even, fixture.Model.Parity);
        Assert.Equal(2, fixture.Model.StopBitCount);
        Assert.False(fixture.Model.ReconnectAutomatically);
        Assert.False(fixture.Model.ConnectOnLaunch);
        Assert.Equal("19200-7-E-2", fixture.Model.ManualSettings.ToString());
    }

    /// <remarks>
    /// §7.1 permits None, Even and Odd. Mark and Space are valid RS-232 that no SmartClock unit
    /// uses, so offering them would only be a way to fail to connect.
    /// </remarks>
    [Fact]
    public void OnlyTheParametersSection71PermitsAreOffered()
    {
        using Fixture fixture = new(ports: []);

        Assert.Equal([1200, 2400, 9600, 19200], fixture.Model.BaudRateOptions);
        Assert.Equal([7, 8], fixture.Model.DataBitOptions);
        Assert.Equal([Parity.None, Parity.Even, Parity.Odd], fixture.Model.ParityOptions);
        Assert.Equal([1, 2], fixture.Model.StopBitOptions);
    }

    // -------------------------------------------------------------------------------------

    /// <summary>A view model over a session whose transports and preferences the test controls.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly List<ControllableTransport> _made = [];

        public Fixture(IReadOnlyList<SerialPortInfo> ports, ConnectionPreferences? stored = null)
        {
            Ports = new FakePortSource { Available = ports };
            Store = new FakeStore(stored ?? ConnectionPreferences.Default);

            Session = new DeviceSessionService(
                (port, settings) =>
                {
                    Requested.Add((port, settings));
                    OnOpen?.Invoke();

                    ControllableTransport transport = Transports.Count > 0
                        ? Transports.Dequeue()
                        : new ControllableTransport(_ => "ÿþ garbage");

                    _made.Add(transport);
                    return transport;
                },
                new FakeTimeProvider());

            Model = new ConnectionViewModel(Session, Ports, Store);
        }

        public FakePortSource Ports { get; }

        public FakeStore Store { get; }

        public DeviceSessionService Session { get; }

        public ConnectionViewModel Model { get; }

        /// <summary>Transports handed out in order, one per open attempt.</summary>
        public Queue<ControllableTransport> Transports { get; } = new();

        /// <summary>Every open attempt, in order.</summary>
        public List<(string Port, SerialSettings Settings)> Requested { get; } = [];

        /// <summary>Runs on each open attempt, for tests that need to fail or cancel one.</summary>
        public Action? OnOpen { get; set; }

        public void Dispose()
        {
            Session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            foreach (ControllableTransport transport in _made)
            {
                transport.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    private sealed class FakePortSource : ISerialPortSource
    {
        public const string Empty = "No ports, for the reason the real enumerator would give.";

        public IReadOnlyList<SerialPortInfo> Available { get; set; } = [];

        public string EmptyMessage => Empty;

        public Task<IReadOnlyList<SerialPortInfo>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Available);
    }

    private sealed class FakeStore(ConnectionPreferences initial) : IConnectionPreferenceStore
    {
        private ConnectionPreferences _preferences = initial;

        public ConnectionPreferences Load() => _preferences;

        public void Save(ConnectionPreferences preferences) => _preferences = preferences;
    }
}
