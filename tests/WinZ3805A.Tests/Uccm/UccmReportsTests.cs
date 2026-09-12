using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Drivers.Uccm;

namespace WinZ3805A.Tests.Uccm;

/// <summary>
/// The reading a UCCM looks unable to report and has not been shown unable to report (#483).
/// </summary>
/// <remarks>
/// <c>ReceiverReadingTests</c> pins the whole switch, absences and all. This one exists because
/// <see cref="ReceiverReading.Holdover"/> is the entry a future reader is most likely to "finish"
/// by adding, and the reason not to is not visible from the switch.
/// </remarks>
public sealed class UccmReportsTests
{
    [Fact]
    public void HoldoverIsNotDeclaredAbsentBecauseThatIsNotYetKnown()
    {
        // THE ENTRY THAT LOOKS LIKE THE OTHERS AND IS NOT. Every field of the Holdover page reads
        // as absent on the bench module, which is exactly how StatusRegisters and HealthMonitor
        // looked before they were declared - so the obvious next move is to add this one too.
        //
        // The difference is in the reply. `:ROSC:HOLD:DUR?` answers `Command error`, where the two
        // survey queries answer `Undefined header` - the same reply a deliberately nonsensical
        // header gets, which is the control that makes THAT conclusive. So this node EXISTS and is
        // refused for some other reason, and state is the obvious candidate: the unit has been
        // locked throughout all five sittings and has never been asked while actually in holdover.
        //
        // Declaring it false would be a hypothesis wearing the costume of a measurement, which is
        // the one thing #416 asks this driver not to do.
        IReceiverDriver driver = new UccmDriver(new FakeTimeProvider());

        Assert.True(driver.Reports(ReceiverReading.Holdover));
    }
}
