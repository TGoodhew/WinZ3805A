using System.ComponentModel;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

using WinZ3805A.Device.Models;

namespace WinZ3805A.Services;

/// <summary>
/// Keeps the taskbar overlay badge showing the receiver's current mode (#274).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the same shape as <see cref="TrayIconService"/>, watching the same two sources and
/// deriving the mode through the same <see cref="ShellMode.For"/> — so the badge and the
/// notification area cannot disagree. The window does not call it: <c>MainViewModel.Mode</c>
/// restates the same expression, a known duplication (#316), and a third restatement here is how
/// the three would drift apart within a week.
/// </para>
/// <para>
/// <b>Everything is marshalled to the dispatcher.</b> <c>ITaskbarList3</c> is apartment-threaded and
/// was created on the UI thread; calling it from the poll thread would be using a proxy across an
/// apartment it was never marshalled into.
/// </para>
/// </remarks>
public sealed class TaskbarOverlayService : IDisposable
{
    private readonly ReceiverStateStore _store;
    private readonly DeviceSessionService _session;
    private readonly DispatcherQueue _dispatcher;
    private readonly TaskbarOverlay _overlay;

    /// <summary>
    /// The one handler this service hands the dispatcher, reused for every notification (#399).
    /// </summary>
    /// <remarks>
    /// A fresh lambda per hop is a fresh COM callable wrapper the runtime's table can never reuse.
    /// See <see cref="WinZ3805A.Views.MainPage"/> for the measurement.
    /// </remarks>
    private readonly DispatcherQueueHandler _push;

    private ConnectionStatus _connection = ConnectionStatus.Disconnected;

    /// <summary>The mode last handed to the UI thread, or null before the first (#399).</summary>
    private ReceiverMode? _pushed;

    private bool _disposed;

    /// <summary>Creates the service and applies the current state.</summary>
    /// <param name="store">The receiver's state.</param>
    /// <param name="session">The connection, because a mode outlives no link (§9.11).</param>
    /// <param name="dispatcher">The UI thread's queue.</param>
    /// <param name="window">The window handle whose taskbar button carries the badge.</param>
    /// <param name="logger">Where a refusal by the shell is recorded.</param>
    public TaskbarOverlayService(
        ReceiverStateStore store,
        DeviceSessionService session,
        DispatcherQueue dispatcher,
        nint window,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _store = store;
        _session = session;
        _dispatcher = dispatcher;
        _overlay = new TaskbarOverlay(window, logger);
        _push = Push;

        _store.PropertyChanged += OnStoreChanged;
        _session.StatusChanged += OnSessionChanged;

        Refresh();
    }

    private void OnSessionChanged(object? sender, ConnectionStatusChanged e)
    {
        _connection = e.Status;
        Refresh();
    }

    private void OnStoreChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    /// <summary>
    /// Pushes the current mode to the badge, on the UI thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called many times a second while polling, for a mode that changes perhaps a handful of times
    /// a day. <see cref="TaskbarOverlay.Update"/> still discards an unchanged mode, but that check
    /// used to sit on the far side of the hop, so a rasterise was avoided and a dispatcher hop was
    /// not — and each hop minted a COM wrapper the runtime could never reuse (#399). The comparison
    /// happens here now; the overlay's own remains the authority.
    /// </para>
    /// <para>
    /// The handler is a field for the same issue, and reads the mode again rather than closing over
    /// this one: by the time the UI thread runs it a newer reading may have arrived.
    /// </para>
    /// </remarks>
    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        ReceiverMode mode = ShellMode.For(_session.Driver, _store, _connection);

        if (_dispatcher.HasThreadAccess)
        {
            _pushed = mode;
            _overlay.Update(mode);
            return;
        }

        if (mode == _pushed)
        {
            return;
        }

        _pushed = mode;
        _dispatcher.TryEnqueue(_push);
    }

    /// <summary>Shows whatever the mode is now, on the UI thread.</summary>
    private void Push()
    {
        if (!_disposed)
        {
            _overlay.Update(ShellMode.For(_session.Driver, _store, _connection));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _store.PropertyChanged -= OnStoreChanged;
        _session.StatusChanged -= OnSessionChanged;
        _overlay.Dispose();
    }
}
