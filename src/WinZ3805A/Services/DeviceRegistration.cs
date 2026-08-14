using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using WinZ3805A.Device.Transport;

namespace WinZ3805A.Services;

/// <summary>
/// Registers one receiver's services against a key, per §12.
/// </summary>
/// <remarks>
/// Only the device half of the composition root lives here, and deliberately: it is free of
/// Windows-only and UI types, so it is compiled into the test project by link and the registration
/// shape — one instance per key, never shared across keys, no static state — is asserted rather
/// than assumed. The rest of the container (the port enumerator's registry walk, the window
/// placement stores) is assembled in <c>App</c>, which is the only place that can be.
/// </remarks>
public static class DeviceRegistration
{
    /// <summary>
    /// Adds the session, state store and poller for one device.
    /// </summary>
    /// <param name="services">The collection to add to.</param>
    /// <param name="key">Which device. v1 passes <see cref="DeviceKeys.Primary"/>.</param>
    /// <param name="transportFactory">
    /// Opens the link. Injected rather than constructed here so a test — and, later, a simulated
    /// device — can supply one without a serial port existing.
    /// </param>
    public static IServiceCollection AddDevice(
        this IServiceCollection services,
        string key,
        Func<string, SerialSettings, ITransport> transportFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(transportFactory);

        // Keyed on the device, so a second receiver is a second AddDevice call and nothing else.
        // Singleton within the key: the session owns the port, and two of them would fight over it.
        services.AddKeyedSingleton(key, (provider, resolvedKey) =>
        {
            TimeProvider time = provider.GetRequiredService<TimeProvider>();
            ILoggerFactory? loggers = provider.GetService<ILoggerFactory>();

            DeviceSessionService session = new(
                transportFactory,
                time,
                loggers?.CreateLogger<DeviceSessionService>());

            ReceiverStateStore store = new(time);

            PollingService poller = new(
                session,
                store,
                time,
                loggers?.CreateLogger<PollingService>());

            return new DeviceContext((string)resolvedKey!, session, store, poller, time);
        });

        return services;
    }
}
