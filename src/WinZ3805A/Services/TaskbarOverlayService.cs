using System.ComponentModel;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

using WinZ3805A.Controls;

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

    private ConnectionStatus _connection = ConnectionStatus.Disconnected;
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
    /// Called many times a second while polling. <see cref="TaskbarOverlay.Update"/> discards an
    /// unchanged mode, so this is a dispatcher hop and a comparison rather than a rasterise — cheap
    /// enough not to need a throttle of its own, and one fewer piece of state to be wrong.
    /// </remarks>
    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        ReceiverMode mode = ShellMode.For(_store, _connection);

        if (_dispatcher.HasThreadAccess)
        {
            _overlay.Update(mode);
            return;
        }

        _dispatcher.TryEnqueue(() =>
        {
            if (!_disposed)
            {
                _overlay.Update(mode);
            }
        });
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
