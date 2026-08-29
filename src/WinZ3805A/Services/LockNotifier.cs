using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
}

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
/// The real sink: a packaged-app toast through the shell.
/// </summary>
/// <remarks>
/// <para>
/// <b>This deliberately does not use <c>AppNotificationManager</c>.</b> The Windows App SDK's
/// notification API was tried first and its <c>Register()</c> failed with <c>0x80070490</c> on every
/// machine it was run on, including the one this was written on, with a manifest declaring
/// everything the documentation asks for. P1-9 was therefore switched off for every user from the
/// first release until 29 Aug 2026.
/// </para>
/// <para>
/// <b>It is not needed, and that is a consequence of §8 rather than a workaround.</b> These toasts
/// carry no buttons and no arguments, so that a notification can never become a command path
/// outside §8's tiers, reachable from the lock screen. Activation is the only thing
/// <c>AppNotificationManager</c>, its COM server and its CLSID uniquely provide — and a
/// notification nobody can click needs no activation. Since §8's rule is permanent, the modern API
/// has nothing to offer this application, now or later.
/// </para>
/// <para>
/// <b>So the failing path was removed rather than kept as a preferred-but-broken first choice.</b>
/// A branch that has never succeeded anywhere is not a fallback arrangement, it is dead code that
/// fails, logs and is stepped over on every launch — and it made the manifest carry a COM server
/// and a CLSID that did nothing. <c>ToastNotificationManager</c> predates the Windows App SDK,
/// needs no manifest declaration, no CLSID and no registration, and needs the one thing this
/// application has: package identity.
/// </para>
/// <para>
/// <b>Nothing it does may cost the application anything.</b> A notification that fails is a
/// notification not shown; the poll loop and the window carry on. This project has twice been
/// killed outright by a WinRT call that looked harmless — <c>ApplicationData.Current</c> and
/// <c>DisplayArea.FindAll</c> — so every send is guarded.
/// </para>
/// </remarks>
public sealed class AppNotificationSink : IToastSink
{
    private readonly ILogger<AppNotificationSink> _logger;

    /// <summary>Creates the sink. There is nothing to register.</summary>
    /// <remarks>
    /// Doing no work here is the point. The constructor previously performed a COM registration
    /// that could not succeed, and the whole feature hung off its result.
    /// </remarks>
    public AppNotificationSink(ILogger<AppNotificationSink>? logger = null)
        => _logger = logger ?? NullLogger<AppNotificationSink>.Instance;

    /// <inheritdoc />
    /// <remarks>
    /// <c>ToastText02</c> is one bold heading over one wrapping body, which is the shape §10.3 asks
    /// for. The API takes XML because it predates the builder pattern; that is the whole of the
    /// cost of not using the newer one.
    /// </remarks>
    public void Show(string title, string body)
    {
        Windows.Data.Xml.Dom.XmlDocument xml =
            Windows.UI.Notifications.ToastNotificationManager.GetTemplateContent(
                Windows.UI.Notifications.ToastTemplateType.ToastText02);

        Windows.Data.Xml.Dom.XmlNodeList texts = xml.GetElementsByTagName("text");
        texts[0].AppendChild(xml.CreateTextNode(title));
        texts[1].AppendChild(xml.CreateTextNode(body));

        Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier()
            .Show(new Windows.UI.Notifications.ToastNotification(xml));

        // Recorded on the success path, not only the failure one. The application log is "what this
        // application saw", and a notification raised belongs in that account - it is the only
        // record a user has of whether they were told, since a toast the shell has retired leaves
        // no trace anywhere else.
        _logger.LogInformation("Notification delivered through the shell.");
    }
}
