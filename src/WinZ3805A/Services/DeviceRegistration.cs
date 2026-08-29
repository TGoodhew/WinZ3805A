using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using WinZ3805A.Device.Drivers;
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

            // Every registered driver, in registration order — which the session treats as
            // priority order, so the first is the fallback for an identity nothing claims (#287).
            // Adding a receiver family is one AddSingleton<IReceiverDriver> in the composition
            // root; nothing here changes. An empty list falls through to the session's own
            // default, so a test composition that registers no driver keeps working.
            IReadOnlyList<IReceiverDriver> drivers = [.. provider.GetServices<IReceiverDriver>()];

            DeviceSessionService session = new(
                transportFactory,
                time,
                loggers?.CreateLogger<DeviceSessionService>(),
                drivers.Count > 0 ? drivers : null);

            ReceiverStateStore store = new(time);

            // P1-2's trend store, resolved rather than constructed: it is application-scoped, not
            // device-scoped, and optional so a headless registration test does not need a file.
            PollingService poller = new(
                session,
                store,
                time,
                loggers?.CreateLogger<PollingService>(),
                provider.GetService<TrendStore>());

            return new DeviceContext((string)resolvedKey!, session, store, poller, time);
        });

        return services;
    }
}
