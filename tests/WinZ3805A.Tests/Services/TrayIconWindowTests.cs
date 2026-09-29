using System.Runtime.InteropServices;

using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// #549 — the tray icon's window must hear a broadcast, or the icon never survives Explorer.
/// </summary>
/// <remarks>
/// <para>
/// <b>These send a real broadcast.</b> Explorer announces a restart by broadcasting
/// <c>TaskbarCreated</c>, and whether a window receives a broadcast is a property of how Windows
/// created it rather than of any code this repository could call instead — so the only honest test
/// is to broadcast. Each test registers a message under a fresh GUID, which no other window on the
/// desktop has registered, so every other window ignores it; <c>SendNotifyMessage</c> does not wait
/// for windows on other threads, so a hung one cannot stall the run.
/// </para>
/// <para>
/// The window was created under <c>HWND_MESSAGE</c> before #549, and moving it back there fails
/// <see cref="ABroadcastReachesTheWindow"/> — checked by doing exactly that.
/// </para>
/// </remarks>
public sealed class TrayIconWindowTests
{
    private const nint HwndBroadcast = 0xFFFF;
    private const uint PmRemove = 0x0001;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;

    /// <summary>
    /// <b>A broadcast reaches the window</b>, which is what lets <c>TaskbarCreated</c> re-add the icon.
    /// </summary>
    [Fact]
    public void ABroadcastReachesTheWindow()
    {
        uint probe = RegisterProbe();
        int received = 0;

        using TrayIconWindow window = new((message, _, _) => received += message == probe ? 1 : 0);

        Assert.NotEqual(0, window.Handle);
        Assert.True(SendNotifyMessage(HwndBroadcast, probe, 0, 0));
        PumpUntil(() => received > 0);

        Assert.Equal(1, received);
    }

    /// <summary>Two windows in one process each reach their own handler.</summary>
    /// <remarks>
    /// The class name was once unique per process only, so a second registration failed and the
    /// second window was created from the first one's class — delivering its messages to the first
    /// window's handler. Nothing makes two today, and this is what keeps that safe for P2-1.
    /// </remarks>
    [Fact]
    public void EachWindowReachesItsOwnHandler()
    {
        uint probe = RegisterProbe();
        int first = 0;
        int second = 0;

        using TrayIconWindow one = new((message, _, _) => first += message == probe ? 1 : 0);
        using TrayIconWindow two = new((message, _, _) => second += message == probe ? 1 : 0);

        Assert.True(SendNotifyMessage(HwndBroadcast, probe, 0, 0));
        PumpUntil(() => first > 0 && second > 0);

        Assert.Equal(1, first);
        Assert.Equal(1, second);
    }

    /// <summary>
    /// The window is never shown, and could not reach the taskbar or Alt+Tab if it were.
    /// </summary>
    /// <remarks>
    /// Being top-level is what #549 needs, and it is also what would let a careless style put an
    /// empty window on the taskbar. A message-only window could not appear anywhere, so this is the
    /// guarantee the change gave up and had to replace.
    /// </remarks>
    [Fact]
    public void TheWindowIsNeverShown()
    {
        using TrayIconWindow window = new((_, _, _) => { });

        Assert.False(IsWindowVisible(window.Handle));
        Assert.Equal(WsExToolWindow, GetWindowLongPtr(window.Handle, GwlExStyle) & WsExToolWindow);
    }

    private static uint RegisterProbe()
    {
        uint probe = RegisterWindowMessage($"WinZ3805A.Tests.TrayBroadcast.{Guid.NewGuid():N}");
        Assert.NotEqual(0u, probe);
        return probe;
    }

    /// <summary>Runs this thread's message loop until a condition holds, for about two seconds.</summary>
    /// <remarks>
    /// The windows belong to the test's own thread, and nothing else pumps it. A broadcast that
    /// arrives is normally delivered within the first pass; the limit is there so a window that can
    /// never receive it fails the assertion rather than hanging the run.
    /// </remarks>
    private static void PumpUntil(Func<bool> done)
    {
        for (int pass = 0; pass < 200 && !done(); pass++)
        {
            while (PeekMessage(out MSG message, 0, 0, 0, PmRemove))
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }

            if (!done())
            {
                Thread.Sleep(10);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int x;
        public int y;
        public uint lPrivate;
    }

    // DllImport for the reason TrayIcon gives: LibraryImport needs AllowUnsafeBlocks project-wide.
#pragma warning disable SYSLIB1054

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SendNotifyMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(
        out MSG message, nint window, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

#pragma warning restore SYSLIB1054
}
