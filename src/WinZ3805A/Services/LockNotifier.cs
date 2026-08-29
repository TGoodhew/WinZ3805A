using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

using WinZ3805A.Device.Models;

namespace WinZ3805A.Services;

/// <summary>Raises a Windows notification, or does nothing if it cannot.</summary>
/// <remarks>
/// An interface so the decision logic can be tested against a recorder rather than against the
/// shell, and so a build with no package identity has somewhere to fall back to.
/// </remarks>
public interface IToastSink
{
    /// <summary>Shows one notification.</summary>
    void Show(string title, string body);

    /// <summary>Why a notification would or would not appear, for the Overview page's test button.</summary>
    ToastStatus Status { get; }
}

/// <summary>
/// What the shell has said about this application's notifications.
/// </summary>
/// <remarks>
/// <para>
/// Exists because "no notification appeared" has at least four causes that look identical from the
/// outside: the app never registered, the shell has them switched off for this app, the user has
/// them switched off entirely, or the notification was raised and the shell dropped it. On a clean
/// machine that ran the sideload package, the first three are all plausible and none of them is
/// visible without asking.
/// </para>
/// <para>
/// <b>Every field is a fact the shell reported, not an inference.</b> The button that displays this
/// composes the sentence; this record does not, so the same values can be logged and tested.
/// </para>
/// </remarks>
/// <param name="Registered">Whether <c>AppNotificationManager.Register</c> returned without throwing.</param>
/// <param name="RegistrationError">
/// The registration failure, or null. Kept verbatim: this is the line that explains a feature which
/// has now silently done nothing across three releases.
/// </param>
/// <param name="ShellSetting">
/// What the shell reports about this app's notifications - <c>Enabled</c>, or one of the several
/// ways of being disabled. <c>unavailable</c> if the query itself failed, which is itself the
/// answer for a build with no package identity.
/// </param>
public sealed record ToastStatus(bool Registered, string? RegistrationError, string ShellSetting);

/// <summary>
/// P1-9's Windows notifications for losing and regaining GPS lock (§10.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>This class does no deciding.</b> <see cref="LockWatch"/> holds the whole of the policy — when
/// a loss has lasted long enough to be worth saying, and whether a recovery is owed — and it does so
/// without a toast, a timer or a receiver anywhere near it. What is left here is the plumbing: watch
/// the store, ask the policy, and hand the answer to the shell.
/// </para>
/// <para>
/// <b>Nothing it does may cost the application anything.</b> A notification that fails is a
/// notification not shown; the poll loop and the window carry on. This project has twice been killed
/// outright by a WinRT call that looked harmless — <c>ApplicationData.Current</c> and
/// <c>DisplayArea.FindAll</c> — so registration and every send are guarded, and a sink that throws
/// on construction leaves the feature off rather than the process dead.
/// </para>
/// </remarks>
public sealed class LockNotifier : IDisposable
{
    private readonly ReceiverStateStore _store;
    private readonly DeviceSessionService _session;
    private readonly LockWatch _watch;
    private readonly IToastSink _sink;
    private readonly ILogger<LockNotifier> _logger;

    private bool _enabled;
    private bool _disposed;

    /// <summary>Creates a notifier over a device's state.</summary>
    public LockNotifier(
        ReceiverStateStore store,
        DeviceSessionService session,
        TimeProvider timeProvider,
        IToastSink sink,
        ILogger<LockNotifier>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(sink);

        _store = store;
        _session = session;
        _watch = new LockWatch(timeProvider);
        _sink = sink;
        _logger = logger ?? NullLogger<LockNotifier>.Instance;

        _store.PropertyChanged += OnStoreChanged;
        _session.StatusChanged += OnSessionChanged;
    }

    /// <summary>Whether notifications are switched on (Settings → Advanced).</summary>
    /// <remarks>
    /// Switching off resets the policy as well as silencing it, so turning it back on does not
    /// immediately announce an outage that began while nobody was listening.
    /// </remarks>
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            _watch.Reset();
        }
    }

    /// <summary>How many notifications have been raised, which the tests count.</summary>
    public int Raised { get; private set; }

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
    }

    private void OnSessionChanged(object? sender, ConnectionStatusChanged e)
    {
        // A disconnect is not an outage: the receiver may be perfectly happy and the cable may be
        // out. Dropping the policy's state stops a pending loss maturing into an alert about a
        // receiver nobody is talking to.
        if (e.Status != ConnectionStatus.Connected)
        {
            _watch.Reset();
        }
    }

    private void OnStoreChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_enabled || _store.Status?.Mode is not SmartClockMode mode)
        {
            return;
        }

        LockAlert alert = _watch.Observe(mode);
        if (LockWatch.Describe(alert, mode) is not (string title, string body))
        {
            return;
        }

        Raised++;

        try
        {
            _sink.Show(title, body);

            // Logged at Information, not Debug, and on the success path rather than only the
            // failure one. The application log is "what this application saw" — the port opening,
            // every connection change, the receiver's mode whenever it moves — and a notification
            // raised belongs in that account: it is the record a user has when they want to know
            // whether they were told, and the only one, since a toast the shell has retired leaves
            // no trace anywhere else.
            _logger.LogInformation("Notified: {Title}", title);
        }
        catch (Exception exception)
        {
            // Never fatal. See the class remarks: a notification that fails is a notification not
            // shown, and the window and the poll loop are not the shell's to take down. At warning
            // rather than debug, because the file log keeps Information and above — a failure
            // recorded below the level anyone keeps is a failure nobody can find.
            _logger.LogWarning(exception, "Raising the lock notification failed.");
        }
    }
}

/// <summary>
/// The real sink: an <c>AppNotification</c> through the Windows App SDK.
/// </summary>
/// <remarks>
/// <para>
/// Registration is attempted once and its failure is survivable. An unpackaged run has no identity
/// to register under, and a machine with notifications disabled by policy will refuse — neither is
/// a reason for the application not to start, and neither is something the user can act on from
/// here.
/// </para>
/// <para>
/// <b>Toasts carry no buttons and no arguments.</b> A notification that could act on the receiver
/// would be a command path outside §8's tiers, reachable from the lock screen. These say what
/// happened; the window is where anything is done.
/// </para>
/// </remarks>
public sealed class AppNotificationSink : IToastSink
{
    private readonly bool _registered;
    private readonly string? _registrationError;
    private readonly ILogger<AppNotificationSink> _logger;

    /// <summary>Registers with the shell, or records that it could not.</summary>
    public AppNotificationSink(ILogger<AppNotificationSink>? logger = null)
    {
        _logger = logger ?? NullLogger<AppNotificationSink>.Instance;

        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception exception)
        {
            // Kept, not merely logged. The log is the right place for it and was not enough: this
            // failure is what a user experiences as "no notifications", and they cannot be asked to
            // find a log line to learn that a feature switched itself off.
            _registrationError = $"{exception.GetType().Name}: {exception.Message}";
            // WARNING, not Debug. This is a shipped feature turning itself off, and it stayed
            // broken from the first release to 28 Aug 2026 precisely because the one line that
            // explained it sat below the captured level. A feature that silently does nothing must
            // say so loudly enough to be read.
            _logger.LogWarning(exception, "Notifications are unavailable; P1-9 is off for this session.");
        }
    }

    /// <summary>Whether the shell accepted the registration.</summary>
    public bool IsAvailable => _registered;

    /// <inheritdoc />
    /// <remarks>
    /// The shell setting is read on every access rather than cached, because the user can change it
    /// in Windows Settings while the application is running - which is precisely the case the test
    /// button exists to catch, and a cached "Enabled" from launch would report the opposite.
    /// </remarks>
    public ToastStatus Status => new(_registered, _registrationError, ReadShellSetting());

    /// <summary>Asks the shell what it will do with this app's notifications.</summary>
    /// <remarks>
    /// Guarded like everything else here. An unpackaged run has no identity to have a setting for,
    /// and the query throwing is a usable answer rather than a reason to fail.
    /// </remarks>
    private static string ReadShellSetting()
    {
        try
        {
            return AppNotificationManager.Default.Setting.ToString();
        }
        catch (Exception exception)
        {
            return $"unavailable ({exception.GetType().Name})";
        }
    }

    /// <inheritdoc />
    public void Show(string title, string body)
    {
        if (!_registered)
        {
            return;
        }

        AppNotification notification = new AppNotificationBuilder()
            .AddText(title)
            .AddText(body)
            .BuildNotification();

        AppNotificationManager.Default.Show(notification);
    }
}
