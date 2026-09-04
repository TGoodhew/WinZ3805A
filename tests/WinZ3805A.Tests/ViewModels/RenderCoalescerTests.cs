using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// The race #399's render coalescing depends on, asserted rather than argued.
/// </summary>
/// <remarks>
/// The fix that started #399 was reasoned about and wrong, and a soak caught it. This is the one
/// piece of that work that was still only an argument: that collapsing a burst of notifications
/// into one render cannot lose an update. It differs from a version that does lose them by the
/// position of a single line.
/// </remarks>
public sealed class RenderCoalescerTests
{
    [Fact]
    public void ABurstCostsOneRender()
    {
        int scheduled = 0;
        RenderCoalescer coalescer = new(() => { scheduled++; return true; });

        // Seven notifications for one sweep, which is what the store actually raises.
        for (int i = 0; i < 7; i++)
        {
            coalescer.Request();
        }

        Assert.Equal(1, scheduled);
    }

    [Fact]
    public void ANotificationDuringTheRenderIsNotLost()
    {
        // THE ONE THAT MATTERS. Begin() runs at the top of the render; a notification arriving
        // after it - while the render is still reading state - must schedule another render, or
        // the reading it carried is never drawn.
        int scheduled = 0;
        RenderCoalescer coalescer = new(() => { scheduled++; return true; });

        coalescer.Request();
        Assert.Equal(1, scheduled);

        coalescer.Begin();          // the render starts
        coalescer.Request();        // a reading arrives while it is still running

        Assert.Equal(2, scheduled);
    }

    [Fact]
    public void ClearingAfterTheRenderWouldHaveLostIt()
    {
        // The same sequence with Begin() called last - the ordering this class exists to prevent.
        // Written out so the failure mode is visible rather than described: the second Request is
        // swallowed, and whatever it carried is never drawn.
        int scheduled = 0;
        RenderCoalescer coalescer = new(() => { scheduled++; return true; });

        coalescer.Request();
        coalescer.Request();        // arrives during the render, gate still shut
        coalescer.Begin();          // render finishes and only then reopens

        Assert.Equal(1, scheduled);
    }

    [Fact]
    public void ARefusedEnqueueLeavesTheGateOpen()
    {
        // A dispatcher shutting down refuses the callback. Nothing will run to reopen the gate, so
        // if Request did not reopen it the view would never render again.
        bool accept = false;
        int scheduled = 0;
        RenderCoalescer coalescer = new(() => { if (!accept) { return false; } scheduled++; return true; });

        Assert.False(coalescer.Request());
        Assert.Equal(0, scheduled);

        accept = true;
        Assert.True(coalescer.Request());
        Assert.Equal(1, scheduled);
    }

    [Fact]
    public void ConcurrentNotificationsScheduleExactlyOne()
    {
        // Notifications arrive on the poll thread, and nothing stops two threads racing here.
        int scheduled = 0;
        RenderCoalescer coalescer = new(() => { Interlocked.Increment(ref scheduled); return true; });

        Parallel.For(0, 512, _ => coalescer.Request());

        Assert.Equal(1, scheduled);
    }

    [Fact]
    public void EveryRenderCycleSchedulesOnce()
    {
        // A steady stream: each render opens the gate and the next burst closes it again.
        int scheduled = 0;
        RenderCoalescer coalescer = new(() => { scheduled++; return true; });

        for (int sweep = 0; sweep < 10; sweep++)
        {
            for (int notification = 0; notification < 7; notification++)
            {
                coalescer.Request();
            }

            coalescer.Begin();
        }

        Assert.Equal(10, scheduled);
    }

    [Fact]
    public void TheDispatcherIsRequired() =>
        Assert.Throws<ArgumentNullException>(() => new RenderCoalescer(null!));
}
