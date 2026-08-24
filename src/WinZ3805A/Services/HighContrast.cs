using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace WinZ3805A.Services;

/// <summary>
/// Whether Windows is in a high-contrast theme. One answer for the whole application.
/// </summary>
/// <remarks>
/// <b>This is asked in two very different places and they must not disagree.</b>
/// <c>TrayIcon</c> asks so it can draw the icon in the system's own colours;
/// <c>AccentPalette</c> asks so it can refuse to repaint the user's system colours with the
/// brand ramp. They used to carry separate implementations, and one of them was wrong (#189).
/// <para>
/// <b><c>SystemParametersInfo</c> rather than <c>AccessibilitySettings</c>.</b> The WinRT type wants
/// a thread with a <c>CoreWindow</c>-ish context, which a Windows App SDK desktop application does
/// not have, and it is called from wherever the caller happens to have marshalled to. The Win32
/// call has no such requirement.
/// </para>
/// <para>
/// <b>An indeterminate answer is reported as <see langword="true"/>, not <see langword="false"/>.</b>
/// This is the part that was backwards. A guard protecting a user's accessibility setting has to
/// fail <i>towards</i> the protection: if we cannot tell, we assume high contrast and leave the
/// colours alone. The cost of being wrong that way is a missing brand accent. The cost of being
/// wrong the other way is eleven of the user's own system colours silently overwritten, for
/// precisely the user who chose them.
/// </para>
/// </remarks>
internal static class HighContrast
{
    private const uint SpiGetHighContrast = 0x0042;
    private const uint HighContrastOn = 0x00000001;

    /// <summary>Whether Windows is currently in a high-contrast theme.</summary>
    /// <param name="logger">Where an indeterminate answer is recorded. Optional.</param>
    /// <returns>
    /// <see langword="true"/> when high contrast is on, <b>and also</b> when the setting could not
    /// be read at all — see the remarks on the class.
    /// </returns>
    internal static bool IsEnabled(ILogger? logger = null)
    {
        ILogger log = logger ?? NullLogger.Instance;

        try
        {
            HIGHCONTRAST info = new() { cbSize = Marshal.SizeOf<HIGHCONTRAST>() };

            if (SystemParametersInfo(SpiGetHighContrast, (uint)info.cbSize, ref info, 0))
            {
                return Decide(queried: true, info.dwFlags);
            }

            log.LogWarning(
                "SystemParametersInfo(SPI_GETHIGHCONTRAST) failed with Win32 error {Error}. "
                + "Assuming high contrast is on, so the user's system colours are left alone.",
                Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex,
                "Could not determine the high-contrast setting. Assuming it is on, so the user's "
                + "system colours are left alone.");
        }

        return true;
    }

    /// <summary>
    /// The decision itself, separated from the reading so it can be tested. <paramref name="queried"/>
    /// is whether <c>SystemParametersInfo</c> succeeded.
    /// </summary>
    /// <remarks>
    /// This exists because the polarity is the part that was wrong in #189 and the part most likely
    /// to be "tidied" back. A failed query returns <see langword="true"/> - see the class remarks -
    /// and no amount of comment survives as well as a test that fails.
    /// </remarks>
    internal static bool Decide(bool queried, uint flags) =>
        !queried || (flags & HighContrastOn) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct HIGHCONTRAST
    {
        public int cbSize;
        public uint dwFlags;
        public nint lpszDefaultScheme;
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint param,
        ref HIGHCONTRAST info,
        uint update);
}
