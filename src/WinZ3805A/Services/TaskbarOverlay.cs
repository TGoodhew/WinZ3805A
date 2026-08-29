using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using WinZ3805A.Controls;

namespace WinZ3805A.Services;

/// <summary>
/// The taskbar overlay badge, showing the receiver's state on the app's taskbar button (#274).
/// </summary>
/// <remarks>
/// <para>
/// <b>The BADGE is shown only when the receiver is NOT locked; the DESCRIPTION is always set.</b>
/// A plain app icon means "nothing to say" and a badge means look. Windows' overlay geometry is
/// fixed and generous — the badge covers a
/// meaningful part of the icon rather than annotating a corner of it — so a permanent green disc
/// would obscure the icon in the one state that needs no attention, and would make the button
/// harder to pick out of a crowded taskbar. Showing it only on exception also makes the badge's
/// <i>appearance</i> informative, not just its colour.
/// </para>
/// <para>
/// <b>The artwork is <see cref="TrayIconRaster"/>'s, not a second set.</b> §9.4.3's shapes are
/// already drawn there and the #129 spike measured them legible at 16, 20 and 32 px. Two
/// rasterisers would be two chances for the tray and the taskbar to disagree about what a hexagon
/// means.
/// </para>
/// <para>
/// <b>The description reaches assistive technology.</b> The spike established that
/// <c>pszDescription</c> surfaces as the taskbar button's <c>HelpText</c>, which Narrator
/// announces — so it is a whole sentence in the style of <see cref="StatusMedallion"/>'s
/// automation name, not a word.
/// </para>
/// <para>
/// <b>The caller owns the HICON.</b> <c>SetOverlayIcon</c> copies what it needs, so the handle is
/// destroyed immediately after the call. The spike ran fifty swaps and watched GDI and USER handle
/// counts return to where they started.
/// </para>
/// <para>
/// Guarded whole and never fatal, like the tray icon beside it. A badge is a convenience; a shell
/// that refuses one is not a reason for the application to stop.
/// </para>
/// </remarks>
public sealed class TaskbarOverlay : IDisposable
{
    private readonly nint _window;
    private readonly ILogger _logger;
    private readonly ITaskbarList3? _taskbar;

    private ReceiverMode? _shown;
    private bool _disposed;

    /// <summary>Creates the overlay for one window, or records that it could not.</summary>
    /// <param name="window">The window whose taskbar button carries the badge.</param>
    /// <param name="logger">Where a refusal by the shell is recorded.</param>
    public TaskbarOverlay(nint window, ILogger? logger = null)
    {
        _window = window;
        _logger = logger ?? NullLogger.Instance;

        try
        {
            _taskbar = (ITaskbarList3)new TaskbarInstance();
            _taskbar.HrInit();
        }
        catch (Exception exception)
        {
            // At warning, on the same reasoning as the notification sink: a feature switching
            // itself off must say so loudly enough that someone can find out why.
            _logger.LogWarning(exception, "The taskbar overlay is unavailable; no badge this session.");
            _taskbar = null;
        }
    }

    /// <summary>Shows the badge for a mode, or clears it when the receiver is locked.</summary>
    /// <remarks>
    /// Called on every property change while polling, so an unchanged mode is discarded here rather
    /// than rasterised and handed to the shell many times a second.
    /// </remarks>
    public void Update(ReceiverMode mode)
    {
        if (_disposed || _taskbar is null || _shown == mode)
        {
            return;
        }

        _shown = mode;

        try
        {
            if (mode == ReceiverMode.Locked)
            {
                // No icon, but STILL A DESCRIPTION, and the difference is a defect found by
                // reading the taskbar button back rather than trusting the call.
                //
                // Passing null here does clear the badge - the icon really does disappear - but
                // Windows KEEPS THE PREVIOUS pszDescription as the button's HelpText. The receiver
                // had gone from Disconnected at launch to Locked, the badge had gone, and Narrator
                // was still being told "Disconnected: this application is not talking to the
                // receiver." A sighted user saw the truth and a screen reader user did not, which
                // is the exact inversion A11Y exists to prevent.
                //
                // So the description is always current, whether or not there is a badge to explain.
                // The badge is decluttered in the healthy state; the assistive text is not.
                _taskbar.SetOverlayIcon(_window, 0, Describe(mode));
                return;
            }

            nint icon = TrayIcon.CreateIcon(
                TrayIconRaster.Render(ReceiverModes.SeverityOf(mode), OverlaySize, TrayIcon.OverlayColour(mode)),
                OverlaySize);

            if (icon == 0)
            {
                return;
            }

            try
            {
                _taskbar.SetOverlayIcon(_window, icon, Describe(mode));
            }
            finally
            {
                TrayIcon.Destroy(icon);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Setting the taskbar overlay for {Mode} failed.", mode);
        }
    }

    /// <summary>The sentence a screen reader announces for this mode.</summary>
    /// <remarks>
    /// A whole sentence, because <c>pszDescription</c> becomes <c>HelpText</c> and is read aloud.
    /// "Holdover" alone is a word from this application's vocabulary; someone meeting it through
    /// Narrator on a taskbar button has no other context to read it in.
    /// </remarks>
    private static string Describe(ReceiverMode mode) => mode switch
    {
        // Locked carries no badge and still carries this. See Update for why.
        ReceiverMode.Locked => "Locked to GPS: the receiver is disciplined and healthy.",
        ReceiverMode.Recovering => "Recovering: the receiver is re-acquiring GPS.",
        ReceiverMode.Waiting => "Waiting to recover: the receiver is not yet disciplining.",
        ReceiverMode.Holdover => "Holdover: the receiver is free-running without GPS.",
        ReceiverMode.PowerUp => "Powering up: the receiver is not yet reporting a state.",
        ReceiverMode.Off => "Diagnostic or off: the receiver is not disciplining.",
        _ => "Disconnected: this application is not talking to the receiver.",
    };

    /// <inheritdoc />
    /// <remarks>
    /// Clears the badge on the way out. An overlay outliving the process would leave a red hexagon
    /// on a taskbar button belonging to nothing.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _taskbar?.SetOverlayIcon(_window, 0, null);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Clearing the taskbar overlay failed.");
        }
    }

    /// <summary>
    /// The badge raster's size in pixels.
    /// </summary>
    /// <remarks>
    /// Windows scales the overlay to the taskbar button, and the spike found the §9.4.3 shapes
    /// legible from 16 px up. Rendered at 32 so a 200% display has real pixels to scale from rather
    /// than a doubled 16.
    /// </remarks>
    private const int OverlaySize = 32;
}

/// <summary>The shell's taskbar list, for <c>SetOverlayIcon</c>.</summary>
/// <remarks>
/// <b>Every method above <c>SetOverlayIcon</c> must be declared, in order, even though none is
/// called.</b> This is a vtable, not a name lookup: the interface inherits <c>ITaskbarList2</c> and
/// <c>ITaskbarList</c>, and omitting one of their methods would silently move every slot below it
/// so that <c>SetOverlayIcon</c> called something else entirely.
/// </remarks>
[ComImport]
[Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITaskbarList3
{
    // ITaskbarList
    void HrInit();

    void AddTab(nint window);

    void DeleteTab(nint window);

    void ActivateTab(nint window);

    void SetActiveAlt(nint window);

    // ITaskbarList2
    void MarkFullscreenWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

    // ITaskbarList3
    void SetProgressValue(nint window, ulong completed, ulong total);

    void SetProgressState(nint window, int flags);

    void RegisterTab(nint window, nint parent);

    void UnregisterTab(nint window);

    void SetTabOrder(nint window, nint insertBefore);

    void SetTabActive(nint window, nint parent, uint reserved);

    void ThumbBarAddButtons(nint window, uint count, nint buttons);

    void ThumbBarUpdateButtons(nint window, uint count, nint buttons);

    void ThumbBarSetImageList(nint window, nint images);

    void SetOverlayIcon(nint window, nint icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);

    void SetThumbnailTooltip(nint window, [MarshalAs(UnmanagedType.LPWStr)] string? tip);

    void SetThumbnailClip(nint window, nint rectangle);
}

/// <summary>The coclass <see cref="ITaskbarList3"/> is obtained from.</summary>
[ComImport]
[Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
[ClassInterface(ClassInterfaceType.None)]
internal class TaskbarInstance
{
}
