using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.Win32;

using WinZ3805A.Controls;

namespace WinZ3805A.Services;

/// <summary>
/// P1-10's tray icon: the receiver's mode, on the taskbar, for a user who is not looking.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is P/Invoke and not a package.</b> The Windows App SDK has no tray API — the shell
/// wants <c>Shell_NotifyIcon</c> and an <c>HICON</c>, and neither has a WinUI equivalent. The
/// alternative was a third-party wrapper, which would have meant a dependency for about a hundred
/// lines of interop, on a project §6.4 keeps deliberately thin.
/// </para>
/// <para>
/// <b>The window is message-only.</b> <c>Shell_NotifyIcon</c> needs an <c>HWND</c> to send clicks
/// to. Using the main window's would mean subclassing a live WinUI window's <c>WndProc</c>, which is
/// a good way to break XAML input handling in ways that appear months later. A window created under
/// <c>HWND_MESSAGE</c> is never shown, never activated, and owns nothing but this.
/// </para>
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    /// <summary>Our callback message, which must be in the <c>WM_APP</c> range.</summary>
    private const uint CallbackMessage = 0x0400 + 1;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint WmLeftButtonUp = 0x0202;
    private const int SmallIconMetric = 49;
    private const int ColorWindowText = 8;
    private const uint SpiGetHighContrast = 0x0042;
    private const uint HighContrastOn = 0x00000001;

    private readonly WndProc _wndProc;
    private readonly nint _window;
    private readonly uint _taskbarCreated;
    private readonly string _displayName;
    private readonly ILogger _logger;

    private nint _icon;
    private bool _added;
    private bool _disposed;
    private ReceiverMode _mode = ReceiverMode.Disconnected;

    /// <summary>Creates the icon and adds it to the notification area.</summary>
    /// <param name="displayName">
    /// The application's name for the tooltip. §6.3 forbids hard-coding it, so it arrives from
    /// <c>Package.Current.DisplayName</c> at the call site.
    /// </param>
    /// <param name="logger">Where a shell refusal is recorded.</param>
    public TrayIcon(string displayName, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(displayName);

        _logger = logger ?? NullLogger.Instance;

        _displayName = displayName;
        _wndProc = HandleMessage;

        // Registered by name so that a second instance of this class - or a second receiver, under
        // P2-1 - gets its own class rather than failing to register an existing one.
        WNDCLASSEX klass = new()
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = $"WinZ3805A.TrayIcon.{Environment.ProcessId}",
        };

        RegisterClassEx(ref klass);

        _window = CreateWindowEx(
            0, klass.lpszClassName, string.Empty, 0, 0, 0, 0, 0,
            -3 /* HWND_MESSAGE */, 0, klass.hInstance, 0);

        // Explorer can restart. When it does every tray icon is gone and the shell broadcasts this
        // to say so; without it the icon simply never comes back and the user assumes the app died.
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        Update(ReceiverMode.Disconnected);
    }

    /// <summary>Raised when the user clicks the icon.</summary>
    public event EventHandler? Activated;

    /// <summary>
    /// Redraws the icon and retitles it for a mode.
    /// </summary>
    /// <remarks>
    /// Cheap to call with an unchanged mode, and callers do: the poller reports state every second
    /// and has no idea what the tray last drew. Rebuilding the <c>HICON</c> a second time per second
    /// would be pointless work and a steady GDI-handle churn, so the comparison lives here.
    /// </remarks>
    public void Update(ReceiverMode mode)
    {
        if (_disposed || (_added && mode == _mode))
        {
            return;
        }

        _mode = mode;

        TrayIconState state = TrayIconStates.For(mode, _displayName);
        int size = Math.Max(16, GetSystemMetrics(SmallIconMetric));

        nint replacing = _icon;
        _icon = CreateIcon(TrayIconRaster.Render(state.Severity, size, ColourFor(state.Severity)), size);

        NOTIFYICONDATA data = new()
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _window,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = state.Tooltip,
        };

        bool adding = !_added;
        bool ok = Shell_NotifyIcon(adding ? NimAdd : NimModify, ref data);

        if (ok)
        {
            _added = true;
        }
        else if (adding)
        {
            // Worth a warning rather than silence. The shell refuses an add for reasons the user
            // can act on - notification-area policy, an icon limit - and an icon that simply never
            // appears is indistinguishable from one this code forgot to create.
            _logger.LogWarning(
                "The shell refused the tray icon (error {Error}).",
                Marshal.GetLastWin32Error());
        }

        // Freed only after the shell has been handed the replacement. Destroying it first leaves a
        // window in which the notification area is drawing a handle that no longer exists.
        if (replacing != 0)
        {
            DestroyIcon(replacing);
        }
    }

    /// <summary>
    /// The §9.4.3 colour for a severity, in the taskbar's theme rather than the app's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SystemUsesLightTheme</c>, not <c>AppsUseLightTheme</c>. Windows lets these differ, and a
    /// light taskbar under a dark app is a common pairing — reading the app's theme would put the
    /// dark palette's pale amber on a white strip, where it nearly vanishes.
    /// </para>
    /// <para>
    /// If the shape is doing its job this choice is cosmetic, which is the point: the icon stays
    /// readable when the colour is wrong, not merely when it is right.
    /// </para>
    /// </remarks>
    private static Rgb ColourFor(Severity severity)
    {
        // High contrast first: §9.4.3's HighContrast dictionary sends every severity to
        // SystemColorWindowTextColor, and the tray is not the place to make an exception. A user
        // who has asked Windows for two colours is not asking this application for five, and the
        // shape is already carrying the whole message.
        if (IsHighContrast())
        {
            return SystemWindowText();
        }

        bool light = true;

        try
        {
            light = (int?)Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme",
                1) != 0;
        }
        catch (Exception)
        {
            // An unreadable theme preference is not worth failing over; light is the default.
        }

        // Straight out of the token dictionary, so the taskbar cannot drift from the pages.
        // If the token does not resolve - it never should for Light or Dark, both of which give
        // every severity a literal - the system window text colour is a legible last resort on
        // whatever the taskbar is, which is the property that actually matters here.
        return ThemePalette.Colour(light ? ThemePalette.Light : ThemePalette.Dark,
            ThemePalette.BrushKey(severity)) ?? SystemWindowText();
    }

    /// <summary>Builds an <c>HICON</c> from a premultiplied BGRA buffer.</summary>
    /// <remarks>
    /// A top-down DIB — the negative height — because the raster produces the top row first, and a
    /// bottom-up section would show every shape mirrored. The mask bitmap is required by
    /// <c>ICONINFO</c> and ignored for a 32-bit colour bitmap, so it is left blank.
    /// </remarks>
    private static nint CreateIcon(uint[] pixels, int size)
    {
        BITMAPINFO info = new()
        {
            biSize = Marshal.SizeOf<BITMAPINFO>(),
            biWidth = size,
            biHeight = -size,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };

        nint colour = CreateDIBSection(0, ref info, 0, out nint bits, 0, 0);

        if (colour == 0)
        {
            return 0;
        }

        Marshal.Copy(Array.ConvertAll(pixels, unchecked(p => (int)p)), 0, bits, pixels.Length);

        nint mask = CreateBitmap(size, size, 1, 1, 0);

        ICONINFO icon = new() { fIcon = true, hbmMask = mask, hbmColor = colour };
        nint handle = CreateIconIndirect(ref icon);

        DeleteObject(colour);
        DeleteObject(mask);

        return handle;
    }

    private nint HandleMessage(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == CallbackMessage && (uint)lParam == WmLeftButtonUp)
        {
            Activated?.Invoke(this, EventArgs.Empty);
        }
        else if (message == _taskbarCreated)
        {
            // Explorer restarted, so the icon we added is gone with it. Re-adding means starting
            // from "not added" - a modify would be addressed to an icon the shell has forgotten.
            _added = false;
            ReceiverMode mode = _mode;
            _mode = ReceiverMode.Disconnected;
            Update(mode);
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    /// <summary>Removes the icon and releases the window.</summary>
    /// <remarks>
    /// Worth getting right: an icon whose owner exited without this stays on the taskbar as a ghost
    /// until the user waves the pointer over it.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_added)
        {
            NOTIFYICONDATA data = new()
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _window,
                uID = 1,
            };

            Shell_NotifyIcon(NimDelete, ref data);
        }

        if (_icon != 0)
        {
            DestroyIcon(_icon);
            _icon = 0;
        }

        if (_window != 0)
        {
            DestroyWindow(_window);
        }
    }

    /// <summary>The system window text colour, which is legible on whatever the taskbar is.</summary>
    private static Rgb SystemWindowText()
    {
        uint colour = GetSysColor(ColorWindowText);

        // COLORREF is 0x00BBGGRR, not RGB. Getting this backwards is invisible for a grey and
        // obvious for anything else, which is a poor way to find out.
        return new Rgb((byte)colour, (byte)(colour >> 8), (byte)(colour >> 16));
    }

    /// <summary>Whether Windows is in a high-contrast theme.</summary>
    /// <remarks>
    /// <c>SystemParametersInfo</c> rather than <c>AccessibilitySettings</c>: the WinRT type wants a
    /// thread with a <c>CoreWindow</c>-ish context and this is called from wherever the poll
    /// happens to have marshalled to. The Win32 call has no such requirement.
    /// </remarks>
    private static bool IsHighContrast()
    {
        HIGHCONTRAST info = new() { cbSize = Marshal.SizeOf<HIGHCONTRAST>() };

        return SystemParametersInfo(SpiGetHighContrast, (uint)info.cbSize, ref info, 0)
            && (info.dwFlags & HighContrastOn) != 0;
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct HIGHCONTRAST
    {
        public int cbSize;
        public uint dwFlags;
        public nint lpszDefaultScheme;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    // DllImport rather than LibraryImport. The source generator cannot marshal NOTIFYICONDATA -
    // its szTip, szInfo and szInfoTitle are fixed-length inline buffers, which it has no support
    // for - and it also requires AllowUnsafeBlocks across the whole project. Turning unsafe code on
    // application-wide to save the interop marshaller a few nanoseconds on a call made once a
    // second is a bad trade, so this uses the runtime marshaller, which handles ByValTStr natively.
#pragma warning disable SYSLIB1054

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX klass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint exStyle, string klass, string name, uint style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action, uint param, ref HIGHCONTRAST info, uint update);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateIconIndirect(ref ICONINFO icon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? name);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateDIBSection(
        nint dc, ref BITMAPINFO info, uint usage, out nint bits, nint section, int offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateBitmap(int width, int height, uint planes, uint bits, nint data);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);

#pragma warning restore SYSLIB1054
}
