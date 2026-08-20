using System.ComponentModel;

using WinZ3805A.Device.Models;

namespace WinZ3805A.Services;

/// <summary>
/// One receiver's session, its state store, and the poller that connects them.
/// </summary>
/// <remarks>
/// <para>
/// §12 requires <see cref="DeviceSessionService"/> to be instantiable per device and resolved from
/// a keyed DI registration, even though v1 creates exactly one. The three services are bundled
/// rather than keyed individually because they are only ever meaningful together: a store filled by
/// one receiver's poller and read beside another receiver's session would be wrong in a way nothing
/// would report. Resolving the bundle makes that pairing unrepresentable.
/// </para>
/// <para>
/// Disposal order is the poller first, then the session. The poller issues commands through the
/// session, and tearing the session down underneath an in-flight sweep is exactly the race the
/// §7.2 reconnect policy was written to avoid.
/// </para>
/// </remarks>
public sealed class DeviceContext : IAsyncDisposable
{
    /// <summary>Creates a context over an already-constructed set of services.</summary>
    public DeviceContext(
        string key,
        DeviceSessionService session,
        ReceiverStateStore store,
        PollingService poller,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(poller);
        ArgumentNullException.ThrowIfNull(timeProvider);

        Key = key;
        Session = session;
        Store = store;
        Poller = poller;
        TimeProvider = timeProvider;
        PowerUp = new PowerUpGuard(timeProvider);

        // The guard is fed here rather than by the page that reads it, because §10.8's figure is
        // accumulated over the whole session and a page that only started watching when the user
        // navigated to it would report a lower bound of a few seconds after a week of uptime.
        Store.PropertyChanged += OnStoreChanged;
        Session.StatusChanged += OnStatusChanged;

        // Subscribed here rather than by the console page, for the reason above applied to a
        // different figure: a transcript that started recording when the page was opened would
        // hold nothing about the transaction the user came to look at (§10.11).
        Session.TransactionCompleted += OnTransactionCompleted;
    }

    /// <summary>Which device this is. v1 uses <see cref="DeviceKeys.Primary"/> and only that.</summary>
    public string Key { get; }

    /// <summary>The clock everything under this device reads.</summary>
    /// <remarks>
    /// Already a constructor parameter and previously consumed only by <see cref="PowerUp"/>.
    /// Exposed so a page that needs the time — the CSV export stamps its suggested file name with
    /// it — takes the same injected clock as everything else rather than reaching for
    /// <c>DateTime.Now</c>, which §12 forbids and which a test could not pin.
    /// </remarks>
    public TimeProvider TimeProvider { get; }

    /// <summary>The transport and command channel.</summary>
    public DeviceSessionService Session { get; }

    /// <summary>Last-known state, which the view models bind to.</summary>
    public ReceiverStateStore Store { get; }

    /// <summary>The two §7.3 cadences. View models never touch this.</summary>
    public PollingService Poller { get; }

    /// <summary>§10.8's manual-holdover guard, accumulated across the whole session.</summary>
    public PowerUpGuard PowerUp { get; }

    /// <summary>§10.11's record of everything this device has been sent, always recording.</summary>
    public CommandTranscript Transcript { get; } = new();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Store.PropertyChanged -= OnStoreChanged;
        Session.StatusChanged -= OnStatusChanged;
        Session.TransactionCompleted -= OnTransactionCompleted;

        await Poller.DisposeAsync().ConfigureAwait(false);
        await Session.DisposeAsync().ConfigureAwait(false);
    }

    private void OnTransactionCompleted(object? sender, TranscriptEntry entry) => Transcript.Add(entry);

    private void OnStoreChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Store.Status?.Mode is SmartClockMode mode)
        {
            PowerUp.Observe(mode);
        }
    }

    private void OnStatusChanged(object? sender, ConnectionStatusChanged e)
    {
        // Anything short of connected is a gap in observation, and a gap could hide a power cycle.
        if (e.Status != ConnectionStatus.Connected)
        {
            PowerUp.ObservationBroken();
        }
    }
}

/// <summary>The device keys this version registers.</summary>
/// <remarks>
/// A class of constants rather than an enum: the key reaches DI as an object, and §12's
/// multi-device future wants keys derived from device identity — a serial number or a port — which
/// an enum cannot express.
/// </remarks>
public static class DeviceKeys
{
    /// <summary>The single receiver v1 talks to.</summary>
    public const string Primary = "primary";
}
