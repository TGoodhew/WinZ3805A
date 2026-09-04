using System.ComponentModel;

using Microsoft.Extensions.Logging;

using Microsoft.UI.Dispatching;

using WinZ3805A.Device.Models;

namespace WinZ3805A.Services;

/// <summary>
/// Keeps the P1-10 tray icon showing the receiver's current mode.
/// </summary>
/// <remarks>
/// <para>
/// The split mirrors <see cref="LockNotifier"/>: this class does no deciding. It watches the same
/// two sources the main window does and derives the mode the same way, so the taskbar and the window
/// cannot disagree — which they would within a week if the mapping were restated here.
/// </para>
/// <para>
/// <b>Everything is marshalled to the dispatcher.</b> The message-only window is created on the UI
/// thread, so its <c>WndProc</c> is pumped by the UI message loop; calling
/// <c>Shell_NotifyIcon</c> for it from the poll thread would be sending to a window whose thread
/// affinity we had just violated.
/// </para>
/// </remarks>
public sealed class TrayIconService : IDisposable
{
    private readonly ReceiverStateStore _store;
    private readonly DeviceSessionService _session;
    private readonly DispatcherQueue _dispatcher;
    private readonly TrayIcon _icon;

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

    /// <summary>Creates the service and shows the icon.</summary>
    /// <param name="store">The receiver's state.</param>
    /// <param name="session">The connection, because a mode outlives no link (§9.11).</param>
    /// <param name="dispatcher">The UI thread's queue.</param>
    /// <param name="displayName">
    /// The application's name, read from <c>Package.Current.DisplayName</c> by the caller — §6.3
    /// forbids hard-coding it, and this class has no business reading package identity.
    /// </param>
    /// <param name="logger">Where a refusal by the shell is recorded.</param>
    public TrayIconService(
        ReceiverStateStore store,
        DeviceSessionService session,
        DispatcherQueue dispatcher,
        string displayName,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _store = store;
        _session = session;
        _dispatcher = dispatcher;
        _icon = new TrayIcon(displayName, logger);
        _push = Push;

        _store.PropertyChanged += OnStoreChanged;
        _session.StatusChanged += OnSessionChanged;

        Refresh();
    }

    /// <summary>Raised when the user clicks the icon, for the window to bring itself forward.</summary>
    public event EventHandler? Activated
    {
        add => _icon.Activated += value;
        remove => _icon.Activated -= value;
    }

    /// <summary>Raised when Exit is chosen from the tray menu (#280).</summary>
    public event EventHandler? ExitRequested
    {
        add => _icon.ExitRequested += value;
        remove => _icon.ExitRequested -= value;
    }

    /// <summary>
    /// The mode the tray should be showing.
    /// </summary>
    /// <remarks>
    /// Identical to <c>MainViewModel.Mode</c>, and deliberately so: a mode is a claim about now, and
    /// a link that has dropped no longer justifies the last one the store happened to hold. Moved
    /// into <see cref="ShellMode"/> for #274, when the taskbar badge became a third surface that has
    /// to agree with this one.
    /// </remarks>
    private ReceiverMode Mode => ShellMode.For(_session.Driver, _store, _connection);

    private void OnSessionChanged(object? sender, ConnectionStatusChanged e)
    {
        _connection = e.Status;
        Refresh();
    }

    private void OnStoreChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    /// <summary>
    /// Pushes the current mode to the icon, on the UI thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called on every property change, which is many times a second while polling — and the mode
    /// changes perhaps a handful of times a day. So the unchanged mode is discarded <i>here</i>,
    /// before the hop, and not on the far side of it (#399). The icon still makes the same check
    /// itself, which is what keeps this one a hint rather than the authority: a queued handler that
    /// never ran cannot leave the icon showing something stale.
    /// </para>
    /// <para>
    /// The handler is a field for the same issue: a fresh lambda per hop is a fresh COM wrapper.
    /// It reads the mode again rather than closing over this one, because by the time the UI thread
    /// runs it a newer reading may have arrived, and the newest is the one worth drawing.
    /// </para>
    /// </remarks>
    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        ReceiverMode mode = Mode;

        if (_dispatcher.HasThreadAccess)
        {
            _pushed = mode;
            _icon.Update(mode);
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
            _icon.Update(Mode);
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
        _icon.Dispose();
    }
}
