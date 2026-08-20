using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

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
        if (_services is ServiceProvider services)
        {
            _services = null;
            await services.DisposeAsync();
        }
    }
}
