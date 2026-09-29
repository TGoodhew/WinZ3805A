using System.Runtime.InteropServices;

namespace WinZ3805A.Services;

/// <summary>
/// The window the tray icon's clicks, menu commands and shell broadcasts are delivered to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not the main window.</b> <c>Shell_NotifyIcon</c> needs an <c>HWND</c> to send clicks to.
/// Using the main window's would mean subclassing a live WinUI window's <c>WndProc</c>, which is a
/// good way to break XAML input handling in ways that appear months later. So the tray owns a window
/// of its own, which is never shown, never activated, and owns nothing but the icon.
/// </para>
/// <para>
/// <b>A hidden top-level window, not a message-only one — and that is the fix for #549, not a
/// style choice.</b> Until then this window was created under <c>HWND_MESSAGE</c>, which reads as
/// the tidier form and is wrong: a message-only window receives no broadcast messages, and Explorer
/// announces a restart by broadcasting <c>TaskbarCreated</c>. The handler that re-adds the icon
/// therefore never ran, and after any Explorer restart the icon stayed gone until the application
/// was restarted too. <c>TrayIconWindowTests</c> sends this window a broadcast; moving it back
/// under <c>HWND_MESSAGE</c> fails that test, which was checked by doing so.
/// </para>
/// <para>
/// <c>WS_POPUP</c> with no <c>WS_VISIBLE</c>, and nothing ever calls <c>ShowWindow</c> on it, so it
/// is never drawn. <c>WS_EX_TOOLWINDOW</c> as well, so that even if something did show it, it could
/// not reach the taskbar or Alt+Tab.
/// </para>
/// <para>
/// Separate from <c>TrayIcon</c> so the tests can build one without putting an icon in the
/// notification area of whoever runs them.
/// </para>
/// </remarks>
internal sealed class TrayIconWindow : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;

    private readonly WndProc _wndProc;
    private readonly Action<uint, nint, nint> _handler;
    private readonly string _className;
    private readonly nint _instance;
    private bool _disposed;

    /// <summary>Registers a window class and creates the window.</summary>
    /// <param name="handler">
    /// Called with every message the window receives, before the default processing, which always
    /// runs afterwards.
    /// </param>
    public TrayIconWindow(Action<uint, nint, nint> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _handler = handler;

        // Held in a field: the class points at this delegate, and a delegate the collector has
        // taken is a crash on the next message rather than an exception anywhere useful.
        _wndProc = HandleMessage;
        _instance = GetModuleHandle(null);

        // Unique per window, not per process. A second registration under a name already taken
        // fails, and CreateWindowEx then quietly uses the first class - so the second window's
        // messages would go to the first window's handler. There is one tray icon today; P2-1 and
        // the tests are why this is not left to that.
        _className = $"WinZ3805A.TrayIcon.{Guid.NewGuid():N}";

        WNDCLASSEX klass = new()
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _instance,
            lpszClassName = _className,
        };

        RegisterClassEx(ref klass);

        Handle = CreateWindowEx(
            WsExToolWindow, _className, string.Empty, WsPopup, 0, 0, 0, 0,
            0 /* top-level: see the remarks */, 0, _instance, 0);
    }

    /// <summary>The window's handle, or zero if Windows refused to create it.</summary>
    public nint Handle { get; }

    private nint HandleMessage(nint window, uint message, nint wParam, nint lParam)
    {
        _handler(message, wParam, lParam);
        return DefWindowProc(window, message, wParam, lParam);
    }

    /// <summary>Destroys the window and unregisters its class.</summary>
    /// <remarks>Must be called on the thread that created the window, as Win32 requires.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (Handle != 0)
        {
            DestroyWindow(Handle);
        }

        UnregisterClass(_className, _instance);
    }

    // ------------------------------------------------------------------------------- interop

    private delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    // DllImport rather than LibraryImport, for the reason TrayIcon gives: the source generator
    // needs AllowUnsafeBlocks project-wide, and WNDCLASSEX carries strings it will not marshal.
#pragma warning disable SYSLIB1054

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX klass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string klass, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint exStyle, string klass, string name, uint style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? name);

#pragma warning restore SYSLIB1054
}
