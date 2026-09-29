using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.Win32;

using WinZ3805A.Controls;
using WinZ3805A.Device.Models;

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
/// <b>The icon has a hidden window of its own</b>, <see cref="TrayIconWindow"/>, which says why it
/// is not the main window's and why it must not be message-only (#549).
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
    private const uint WmRightButtonUp = 0x0205;
    private const uint WmCommand = 0x0111;

    // Menu item ids. Arbitrary, but must not be 0 - TrackPopupMenu returns 0 for "dismissed".
    private const uint MenuOpen = 1;
    private const uint MenuExit = 2;
    private const uint MenuKeepAbove = 3;
    private const int SmallIconMetric = 49;
    private const int ColorWindowText = 8;

    private readonly TrayIconWindow _window;
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

        // Explorer can restart. When it does every tray icon is gone and the shell broadcasts this
        // to say so; without it the icon simply never comes back and the user assumes the app died.
        // Only a window that can receive a broadcast hears it, which is #549 and TrayIconWindow's
        // whole reason for being top-level. Registered before the window exists, so no message can
        // arrive while this still reads as zero.
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        _window = new TrayIconWindow(HandleMessage);

        Update(ReceiverMode.Disconnected);
    }

    /// <summary>Raised when Exit is chosen from the menu (#280).</summary>
    /// <remarks>
    /// One of the two ways out of the application once the close button no longer exits — §10.3.1
    /// requires an Exit under Settings → Advanced as well, because Windows 11 puts a new icon in
    /// the hidden overflow. Raised rather than acted on here: this class knows about an icon, not
    /// about a service provider or a receiver that may be mid-transaction.
    /// </remarks>
    public event EventHandler? ExitRequested;

    /// <summary>Raised when the user clicks the icon.</summary>
    public event EventHandler? Activated;

    /// <summary>Raised when <i>Keep above other windows</i> is chosen from the menu (#568).</summary>
    public event EventHandler? KeepAboveToggled;

    /// <summary>
    /// Asked, as the menu opens, whether the window is pinned, so the item can carry a check mark.
    /// </summary>
    /// <remarks>
    /// A question rather than a stored flag, for the reason the window's own right-click menu reads
    /// its state at opening: the pin changes from four places, and a copy here would be a fifth
    /// that could disagree with them. Unset, the item shows unchecked.
    /// </remarks>
    public Func<bool>? IsKeptAbove { get; set; }

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
            hWnd = _window.Handle,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = state.Tooltip,
        };

        bool adding = !_added;
        bool ok = Shell_NotifyIcon(adding ? NimAdd : NimModify, ref data);

        if (!ok && !adding)
        {
            // #549's second half. A refused modify usually means the shell has forgotten the icon -
            // Explorer restarted and the broadcast saying so did not arrive - and a modify addressed
            // to a forgotten icon fails every time, so without this the icon stays gone. The
            // broadcast is the main route back; this catches whatever it does not reach, on the
            // next change of mode.
            //
            // If the add is refused as well, the icon most likely still exists and the modify merely
            // timed out, which Shell_NotifyIcon does while Explorer is busy. _added is left true
            // then, so the next change tries a modify again rather than an add that cannot succeed.
            ok = Shell_NotifyIcon(NimAdd, ref data);

            if (ok)
            {
                _logger.LogInformation("The shell had lost the tray icon; it has been added again.");
            }
        }

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
        //
        // No logger: this is static and runs on every severity change, so an indeterminate reading
        // would repeat rather than inform. AccentPalette logs it once at startup, which is where a
        // reader would look, and both now get the same answer from the same place (#189).
        if (HighContrast.IsEnabled())
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
    /// <para>
    /// A top-down DIB — the negative height — because the raster produces the top row first, and a
    /// bottom-up section would show every shape mirrored. The mask bitmap is required by
    /// <c>ICONINFO</c> and ignored for a 32-bit colour bitmap, so it is left blank.
    /// </para>
    /// <para>
    /// <b>Internal rather than private since #274.</b> The taskbar overlay builds an HICON from the
    /// same raster, and two copies of this would be two chances to get the top-down DIB wrong in
    /// different ways.
    /// </para>
    /// </remarks>
    internal static nint CreateIcon(uint[] pixels, int size)
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

    /// <summary>
    /// The right-click menu: Open, Keep above other windows, and Exit (#280, #568).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This class had no menu until close-to-tray landed, and the reason it had none was
    /// good</b> - "there is nothing to put on one that the window does not already do". That stops
    /// being true the moment the close button no longer exits, because an application with no
    /// window and no menu cannot be quit from the notification area at all (§10.3.1's other exit,
    /// the button on Settings → Advanced, needs the window back first). The menu exists to carry
    /// <i>Exit</i>; <i>Open</i> is there because one item is a worse affordance than two, and it
    /// duplicates the left-click that already works.
    /// </para>
    /// <para>
    /// <b>Nothing that touches the receiver goes here.</b> Every command reaches the device through
    /// §8's tiers with §8.3's consequence text, and a shell context menu is not a place any of that
    /// can be shown.
    /// </para>
    /// <para>
    /// <b>The pin is here because it touches only the window (#568).</b> It is the one window
    /// setting that is worth reaching while the window is hidden or compact: compact mode collapses
    /// the footer pin, and pinning a window before bringing it back is a reasonable thing to want.
    /// </para>
    /// <para>
    /// <b>SetForegroundWindow and the trailing PostMessage are required, not decorative.</b>
    /// Without them a popup menu shown from a notification icon does not dismiss when the user
    /// clicks away - a documented Win32 quirk, and the reason both calls are here.
    /// </para>
    /// </remarks>
    private void ShowMenu()
    {
        nint menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MfString, MenuOpen, "&Open");
            AppendMenu(
                menu,
                MfString | (IsKeptAbove?.Invoke() == true ? MfChecked : 0),
                MenuKeepAbove,
                "&Keep above other windows");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, MenuExit, "E&xit");

            if (!GetCursorPos(out POINT cursor))
            {
                return;
            }

            SetForegroundWindow(_window.Handle);
            TrackPopupMenu(
                menu, TpmRightButton | TpmBottomAlign, cursor.X, cursor.Y, 0, _window.Handle, 0);
            PostMessage(_window.Handle, 0, 0, 0);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void HandleMessage(uint message, nint wParam, nint lParam)
    {
        if (message == CallbackMessage && (uint)lParam == WmLeftButtonUp)
        {
            Activated?.Invoke(this, EventArgs.Empty);
        }
        else if (message == CallbackMessage && (uint)lParam == WmRightButtonUp)
        {
            ShowMenu();
        }
        else if (message == WmCommand)
        {
            switch ((uint)wParam & 0xFFFF)
            {
                case MenuOpen:
                    Activated?.Invoke(this, EventArgs.Empty);
                    break;

                case MenuExit:
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    break;

                case MenuKeepAbove:
                    KeepAboveToggled?.Invoke(this, EventArgs.Empty);
                    break;

                default:
                    break;
            }
        }
        else if (message == _taskbarCreated)
        {
            // Explorer restarted, so the icon we added is gone with it. Re-adding means starting
            // from "not added" - a modify would be addressed to an icon the shell has forgotten.
            // Logged because this branch never ran before #549, and nothing on screen says whether
            // it has: an icon that came back and one that was never lost look the same.
            _logger.LogInformation("Explorer restarted; adding the tray icon again.");
            _added = false;
            ReceiverMode mode = _mode;
            _mode = ReceiverMode.Disconnected;
            Update(mode);
        }
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
                hWnd = _window.Handle,
                uID = 1,
            };

            Shell_NotifyIcon(NimDelete, ref data);
        }

        if (_icon != 0)
        {
            DestroyIcon(_icon);
            _icon = 0;
        }

        _window.Dispose();
    }

    /// <summary>The system window text colour, which is legible on whatever the taskbar is.</summary>
    private static Rgb SystemWindowText()
    {
        uint colour = GetSysColor(ColorWindowText);

        // COLORREF is 0x00BBGGRR, not RGB. Getting this backwards is invisible for a grey and
        // obvious for anything else, which is a poor way to find out.
        return new Rgb((byte)colour, (byte)(colour >> 8), (byte)(colour >> 16));
    }

    // ------------------------------------------------------------------------------- interop

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
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(nint menu, uint flags, uint id, string? item);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenu(
        nint menu, uint flags, int x, int y, int reserved, nint window, nint rectangle);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;
    private const uint MfChecked = 0x0008;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmBottomAlign = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>Releases an HICON built by <see cref="CreateIcon"/> (#274).</summary>
    /// <remarks>
    /// The caller owns what <c>CreateIcon</c> returns, whether it went to the notification area or
    /// to a taskbar button. Exposed so the overlay does not need its own <c>DestroyIcon</c>
    /// declaration for the handles this class made.
    /// </remarks>
    internal static void Destroy(nint icon)
    {
        if (icon != 0)
        {
            DestroyIcon(icon);
        }
    }

    /// <summary>The colour §9.4.3 gives a mode's badge, high contrast included (#274).</summary>
    /// <remarks>
    /// Delegates to the same <see cref="ColourFor"/> the notification area uses, so a hexagon means
    /// the same thing and is the same colour in both places.
    /// </remarks>
    internal static Rgb OverlayColour(ReceiverMode mode) => ColourFor(ReceiverModes.SeverityOf(mode));

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateIconIndirect(ref ICONINFO icon);

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
