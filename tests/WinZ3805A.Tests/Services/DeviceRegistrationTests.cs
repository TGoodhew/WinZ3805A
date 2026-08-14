using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Transport;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// The registration shape §12 constrains: one context per device key, nothing shared between keys,
/// and no static state anywhere in it.
/// </summary>
/// <remarks>
/// v1 creates exactly one receiver, so none of this is exercised by running the application — which
/// is the reason to assert it now rather than when a second device makes it load-bearing. The
/// transport factory is fake throughout: nothing here opens a port.
/// </remarks>
public sealed class DeviceRegistrationTests
{
    private const string Second = "secondary";

    private static ServiceProvider Compose(params string[] keys)
    {
        ServiceCollection services = new();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());

        foreach (string key in keys)
        {
            services.AddDevice(key, (_, _) => new FakeTransport());
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task TheContextIsResolvedByDeviceKey()
    {
        await using ServiceProvider provider = Compose(DeviceKeys.Primary);

        DeviceContext context = provider.GetRequiredKeyedService<DeviceContext>(DeviceKeys.Primary);

        Assert.Equal(DeviceKeys.Primary, context.Key);
    }

    /// <remarks>
    /// The session owns the port; two of them for one device would fight over it, and the poller
    /// would be writing into a store nobody was reading.
    /// </remarks>
    [Fact]
    public async Task OneKeyResolvesToOneContext()
    {
        await using ServiceProvider provider = Compose(DeviceKeys.Primary);

        Assert.Same(
            provider.GetRequiredKeyedService<DeviceContext>(DeviceKeys.Primary),
            provider.GetRequiredKeyedService<DeviceContext>(DeviceKeys.Primary));
    }

    /// <remarks>
    /// §12's multi-device readiness in one assertion. A second receiver is a second
    /// <c>AddDevice</c> call and nothing else — and it must share none of the first's state.
    /// </remarks>
    [Fact]
    public async Task TwoKeysShareNothing()
    {
        await using ServiceProvider provider = Compose(DeviceKeys.Primary, Second);

        DeviceContext first = provider.GetRequiredKeyedService<DeviceContext>(DeviceKeys.Primary);
        DeviceContext second = provider.GetRequiredKeyedService<DeviceContext>(Second);

        Assert.NotSame(first, second);
        Assert.NotSame(first.Session, second.Session);
        Assert.NotSame(first.Store, second.Store);
        Assert.NotSame(first.Poller, second.Poller);
        Assert.Equal(Second, second.Key);
    }

    /// <remarks>
    /// Unkeyed resolution has to fail. It is the shape a later refactor slips back into — one
    /// <c>AddSingleton&lt;DeviceSessionService&gt;</c> is all it takes — and it is exactly the
    /// static-by-another-name that §12 forbids.
    /// </remarks>
    [Fact]
    public async Task ThereIsNoUnkeyedContext()
    {
        await using ServiceProvider provider = Compose(DeviceKeys.Primary);

        Assert.Null(provider.GetService<DeviceContext>());
    }

    /// <summary>
    /// The container must be disposed <i>asynchronously</i>, and says so by throwing.
    /// </summary>
    /// <remarks>
    /// <see cref="DeviceContext"/> implements only <see cref="IAsyncDisposable"/>, and a synchronous
    /// <c>Dispose</c> on a provider holding one throws <see cref="InvalidOperationException"/> —
    /// from inside the shutdown path, where an exception is least welcome and least visible. Pinned
    /// as a test because the failure is a one-word edit away in <c>App</c> and would surface as the
    /// application refusing to close cleanly rather than as anything that names the cause.
    /// </remarks>
    [Fact]
    public void TheProviderCannotBeDisposedSynchronously()
    {
        ServiceProvider provider = Compose(DeviceKeys.Primary);
        _ = provider.GetRequiredKeyedService<DeviceContext>(DeviceKeys.Primary);

        Assert.Throws<InvalidOperationException>(provider.Dispose);
    }

    /// <remarks>
    /// The container disposes what it built, and the context tears the poller down before the
    /// session it issues commands through.
    /// </remarks>
    [Fact]
    public async Task DisposingTheProviderDisposesTheDevice()
    {
        ServiceProvider provider = Compose(DeviceKeys.Primary);
        DeviceContext context = provider.GetRequiredKeyedService<DeviceContext>(DeviceKeys.Primary);

        await provider.DisposeAsync();

        Assert.False(context.Poller.IsRunning);
        Assert.Equal(ConnectionStatus.Disconnected, context.Session.Status);
    }

    [Fact]
    public void ADeviceNeedsAKeyAndAFactory()
    {
        ServiceCollection services = new();

        Assert.Throws<ArgumentException>(() => services.AddDevice(" ", (_, _) => new FakeTransport()));
        Assert.Throws<ArgumentNullException>(() => services.AddDevice(DeviceKeys.Primary, null!));
    }
}
