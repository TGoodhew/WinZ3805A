using Microsoft.Extensions.DependencyInjection;
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
        services.AddDevice(DeviceKeys.Primary, (port, settings) => new SerialTransport(port, settings));

        services.AddSingleton<IConnectionPreferenceStore, LocalConnectionPreferenceStore>();
        services.AddSingleton<IWindowPlacementStore, LocalWindowPlacementStore>();
        services.AddSingleton<SerialPortEnumerator>();

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
