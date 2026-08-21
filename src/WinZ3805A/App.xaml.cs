using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

using Windows.ApplicationModel;

using WinZ3805A.Device.Transport;
using WinZ3805A.Services;
using WinZ3805A.Views;

namespace WinZ3805A;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _services;
    private Window? _window;

    /// <summary>Whether <see cref="ApplyAccent"/> has already subscribed to theme changes.</summary>
    private bool _accentFollowsTheme;

    /// <summary>P1-10's tray icon, while the window is open.</summary>
    private TrayIconService? _tray;

    /// <summary>
    /// The §12 composition root, for the few things a page cannot be handed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pages are constructed by <c>Frame.Navigate</c> and cannot take constructor arguments, which
    /// is why <see cref="DeviceContext"/> already arrives as a navigation parameter. That works
    /// because the context is device-scoped and every page wants it; the log provider is
    /// application-scoped and one page wants it, so widening the navigation parameter for it would
    /// touch eight pages to serve one.
    /// </para>
    /// <para>
    /// <b>Not a way around §12's keyed registration.</b> Anything device-scoped still comes through
    /// <see cref="DeviceContext"/> — resolving a session or a store from here would reintroduce
    /// exactly the shared state that §12 forbids, and multi-device readiness is the reason it does.
    /// </para>
    /// </remarks>
    public static IServiceProvider? Services => (Current as App)?._services;

    /// <summary>
    /// Initializes the singleton application object. This is the first line of authored
    /// code executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _services = Compose();

        _window = new MainWindow(_services);
        _window.Closed += OnMainWindowClosed;
        _window.Activate();

        // Started here rather than by a page: P1-9's whole point is telling a user who is not
        // looking, and a notifier that only ran while some page was open would be off exactly when
        // it is wanted. Resolving it subscribes it to the store.
        StartLockNotifications();

        ApplyAccent();
        StartTrayIcon();
    }

    /// <summary>
    /// Shows P1-10's tray icon and points it at the primary receiver.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Constructed here rather than registered in the container because it needs the window's
    /// dispatcher, which does not exist until <c>OnLaunched</c> has made one. It is disposed with
    /// the window: an icon whose process exits without removing it stays on the taskbar as a ghost
    /// until the user happens to wave the pointer across it.
    /// </para>
    /// <para>
    /// Guarded whole, like the notifier. A tray icon is a convenience, and no failure to draw one
    /// is a reason for the application not to start.
    /// </para>
    /// </remarks>
    private void StartTrayIcon()
    {
        try
        {
            if (_services is null || _window is null)
            {
                return;
            }

            DeviceContext device = _services.GetRequiredKeyedService<DeviceContext>(DeviceKeys.Primary);

            _tray = new TrayIconService(
                device.Store,
                device.Session,
                _window.DispatcherQueue,
                Package.Current.DisplayName,
                _services.GetService<ILoggerFactory>()?.CreateLogger("Tray"));

            // The one thing a user expects of a tray icon. Not a menu: there is nothing to put on
            // one that the window does not already do, and every command worth reaching goes
            // through §8's tiers rather than a shell context menu.
            _tray.Activated += (_, _) => _window?.Activate();

            _services.GetService<ILoggerFactory>()?.CreateLogger("Tray")
                .LogInformation("Tray icon started.");
        }
        catch (Exception exception)
        {
            // Broad, but not silent. Swallowing this without a word made "the shell refused the
            // icon" and "this never ran at all" look identical from the log, which cost an hour
            // of staring at an empty notification area. The feature stays non-fatal; the reason
            // it did not happen is recorded.
            _services?.GetService<ILoggerFactory>()?.CreateLogger("Tray")
                .LogWarning(exception, "The tray icon could not be started.");
        }
    }

    /// <summary>
    /// Applies §9.4.2's accent choice, and keeps applying it when the theme changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The re-application is not belt and braces. A theme change swaps in the other theme
    /// dictionary's brush instances, and those have never been touched — so without the
    /// subscription, a user who had opted in would watch the accent revert to the brand ramp the
    /// moment Windows went dark at sunset, with no setting having changed.
    /// </para>
    /// <para>
    /// Subscribed once, on the main window's content, because the brushes live in the
    /// application's resources rather than a window's. Both windows draw from the same instances,
    /// so one subscription updates both.
    /// </para>
    /// </remarks>
    public static void ApplyAccent()
    {
        try
        {
            if (Current is not App app
                || app._window?.Content is not FrameworkElement root
                || Services?.GetService<IAppearancePreferenceStore>() is not { } store)
            {
                return;
            }

            if (!app._accentFollowsTheme)
            {
                root.ActualThemeChanged += (_, _) => ApplyAccent();
                app._accentFollowsTheme = true;
            }

            AppearancePreferences preferences = store.Load();
            AppliedCount applied = AccentPalette.Apply(root, preferences);

            // Logged because the failure this catches is invisible: a brush key renamed in
            // Colors.xaml and not in AccentRamp leaves one control on the old accent, which reads
            // as a rendering quirk rather than as the wiring fault it is.
            ILogger? log = Services?.GetService<ILoggerFactory>()?.CreateLogger("Accent");

            if (applied.IsComplete)
            {
                log?.LogInformation(
                    "Accent applied: {Base}, {Applied} brushes, source {Source}, theme {Theme}.",
                    applied.Base,
                    applied.Applied,
                    preferences.UseSystemAccent ? "Windows" : "built-in",
                    root.ActualTheme);
            }
            else
            {
                log?.LogWarning(
                    "Accent applied to only {Applied} of {Expected} brushes - a key in AccentRamp "
                    + "does not exist in Colors.xaml.",
                    applied.Applied,
                    applied.Expected);
            }
        }
        catch (Exception)
        {
            // As with the notifier: an accent is decoration, and decoration must not be able to
            // stop the application starting. The brand ramp is already in the dictionary, so the
            // failure mode here is "looks like it always did".
        }
    }

    /// <summary>
    /// Switches P1-9's notifications on or off to match Settings → Advanced.
    /// </summary>
    /// <remarks>
    /// Guarded whole. The feature is a convenience; resolving it must not be able to stop the
    /// application launching, and an unpackaged run has no identity to register a notification
    /// under at all.
    /// </remarks>
    public static void StartLockNotifications()
    {
        try
        {
            if (Services?.GetService<LockNotifier>() is not LockNotifier notifier)
            {
                return;
            }

            notifier.IsEnabled = Services.GetService<IAdvancedPreferenceStore>()
                ?.Load().AreLockNotificationsEnabled ?? false;
        }
        catch (Exception)
        {
            // Deliberately broad and deliberately silent. See the remarks.
        }
    }

    /// <summary>
    /// The composition root §12 asks for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It arrives with the second window rather than with the first, as <c>MainPage</c> said it
    /// would: a container holding exactly one object is ceremony, but two windows sharing one
    /// receiver is the thing a container is for. The session, store and poller can no longer belong
    /// to whichever page happened to be shown first.
    /// </para>
    /// <para>
    /// The device half is registered by <see cref="DeviceRegistration.AddDevice"/>, which is free of
    /// Windows-only types and is therefore tested. What is added here is what cannot be: the port
    /// enumerator walks the registry, and the placement store writes to the user's profile.
    /// </para>
    /// </remarks>
    private static ServiceProvider Compose()
    {
        ServiceCollection services = new();

        services.AddSingleton(TimeProvider.System);

        // #127. ILogger has been injected into the transport, the session and the poller since
        // §15 step 1, and nothing has ever registered a provider - so ILoggerFactory resolved to
        // null and every line went to NullLogger. The instrumentation was real and the log was
        // thrown away.
        //
        // Information by default. Debug logs a line per command, and §7.3 polls once a second on a
        // receiver §1 expects to be left running for weeks; that is a gigabyte of "-> :PTIM:TINT?"
        // and nothing anyone would read. What Information gives is the shape of a session - the
        // port opening, auto-detect settling, every connection change, and the receiver's mode and
        // satellite count as they move - which is what an intermittent antenna fault looks like
        // written down.
        services.AddSingleton(_ => new FileLogWriter(FileLoggerProvider.DefaultPath()));
        services.AddSingleton<FileLoggerProvider>();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.Services.AddSingleton<ILoggerProvider>(
                provider => provider.GetRequiredService<FileLoggerProvider>());
        });
        services.AddDevice(DeviceKeys.Primary, (port, settings) => new SerialTransport(port, settings));

        // §9.8's reduced-motion rule. A singleton because the setting is the user's, not a
        // window's, and because the UISettings instance behind it has to outlive the call that
        // subscribed or it stops reporting changes. Resolved on demand, so the WinRT object is
        // built when the first window that can animate opens rather than on the launch path.
        services.AddSingleton<IMotionService, WzMotionService>();

        // P1-2 (#50). Application-scoped rather than per device: v1 has one receiver, and a second
        // would want its own file rather than a shared table with a device column - which is a
        // decision for #61, not something to prejudge with a schema now.
        services.AddSingleton(_ => new TrendStore(TrendStore.DefaultPath()));

        services.AddSingleton<IConnectionPreferenceStore, LocalConnectionPreferenceStore>();
        services.AddSingleton<IDetailsViewPreferenceStore, LocalDetailsViewPreferenceStore>();
        services.AddSingleton<ISatellitesViewPreferenceStore, LocalSatellitesViewPreferenceStore>();
        services.AddSingleton<IAdvancedPreferenceStore, LocalAdvancedPreferenceStore>();
        services.AddSingleton<IAppearancePreferenceStore, LocalAppearancePreferenceStore>();

        // P1-9 (#57). The sink is registered rather than newed at the call site so a test - or a
        // build with no package identity to register under - can substitute a recorder. Resolved
        // lazily, because registering with the shell is a WinRT call and the launch path is where
        // this application has twice been killed by one.
        services.AddSingleton<IToastSink>(provider =>
            new AppNotificationSink(provider.GetService<ILogger<AppNotificationSink>>()));

        services.AddSingleton(provider => new LockNotifier(
            provider.GetRequiredKeyedService<DeviceContext>(DeviceKeys.Primary).Store,
            provider.GetRequiredKeyedService<DeviceContext>(DeviceKeys.Primary).Session,
            provider.GetRequiredKeyedService<DeviceContext>(DeviceKeys.Primary).TimeProvider,
            provider.GetRequiredService<IToastSink>(),
            provider.GetService<ILogger<LockNotifier>>()));
        services.AddSingleton<SerialPortEnumerator>();

        // Keyed by window: each keeps its own file, because the two are different sizes on
        // different parts of the desktop and are moved and closed independently. The main window's
        // key is its existing file name, so an upgrade does not lose a remembered placement.
        foreach (string window in new[] { MainWindow.PlacementKey, DetailsWindow.PlacementKey })
        {
            services.AddKeyedSingleton<IWindowPlacementStore>(
                window,
                (_, key) => new LocalWindowPlacementStore(LocalWindowPlacementStore.PathFor((string)key!)));
        }

        return services.BuildServiceProvider();
    }

    /// <remarks>
    /// The receiver is let go when the window that opened it closes. <c>Unloaded</c> on the page was
    /// doing this before and is not raised on window close at all, so the port was in practice being
    /// released by process exit. <b>When the §10.4 Details window lands this needs a window count</b>
    /// — closing Main while Details is open must not take the session with it.
    /// </remarks>
    private async void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        _tray?.Dispose();
        _tray = null;

        if (_services is ServiceProvider services)
        {
            _services = null;
            await services.DisposeAsync();
        }
    }
}
