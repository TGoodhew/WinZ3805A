using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Drivers.Uccm;

namespace WinZ3805A.Tests.Uccm;

/// <summary>
/// The driver's frame grammar reaches the transport through the interface (#481).
/// </summary>
/// <remarks>
/// <para>
/// <b>Written while diagnosing why the fix was not working on hardware</b>, to rule out the
/// possibility that <see cref="IReceiverDriver.BinaryFrames"/>'s default implementation was being
/// taken in preference to the driver's own property. It was not — the session was reading the
/// grammar correctly all along, and the real fault was elsewhere. The test is kept because that
/// question is worth being able to answer in a second rather than by reasoning about default
/// interface members, and because a driver that silently declared nothing would show no symptom
/// beyond the corruption it exists to prevent.
/// </para>
/// </remarks>
public class BinaryFrameWiringTests
{
    /// <summary>Asked as <see cref="IReceiverDriver"/>, which is how the session asks.</summary>
    [Fact]
    public void TheUccmDriverDeclaresItsFramesThroughTheInterface()
    {
        IReceiverDriver driver = new UccmDriver(TimeProvider.System);

        Assert.Equal(44, driver.BinaryFrames.Length);
        Assert.Equal(0xC5, driver.BinaryFrames.Marker);
        Assert.Equal(0xCA, driver.BinaryFrames.Terminator);
        Assert.False(driver.BinaryFrames.IsNone);
    }

    /// <summary>
    /// A family that broadcasts nothing declares nothing, and is not scanned for a marker.
    /// </summary>
    /// <remarks>
    /// The guard on the default. <c>IsNone</c> is what makes the transport skip the byte pass
    /// entirely, so a SmartClock keeps the path it had rather than one that merely finds nothing.
    /// </remarks>
    [Fact]
    public void TheSmartClockDeclaresNoFrames()
    {
        IReceiverDriver driver = new SmartClockDriver(TimeProvider.System);

        Assert.True(driver.BinaryFrames.IsNone);
    }
}
