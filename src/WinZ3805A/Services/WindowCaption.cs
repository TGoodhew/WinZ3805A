using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinZ3805A.Services;

/// <summary>
/// Keeps a window's caption — the text Windows has, which the taskbar, Alt+Tab and Narrator name
/// the window by — when a <see cref="TitleBar"/> control draws its title (#637, #663).
/// </summary>
/// <remarks>
/// <para>
/// <b>The <see cref="TitleBar"/> control writes its own <see cref="TitleBar.Title"/> over the
/// window's caption when it loads.</b> Measured on Windows 10 22H2 and Windows 11 26H2 with the
/// Windows App SDK 2.3.1, 2 Oct 2026: the caption was still <c>Help - WinZ3805A</c> after
/// <c>SetTitleBar</c> and after the window's first activation, and was the bare display name once
/// the title bar had loaded. So the Help and Details windows both reached Windows named exactly
/// like the main window, and a keyboard or Narrator user could not tell the three apart.
/// </para>
/// <para>
/// The drawn title is right as it is — the display name, with the window's name as the subtitle —
/// so the caption is set again after the control has had its say, rather than changing what it
/// draws.
/// </para>
/// <para>
/// <b>Not on load only (#663).</b> At 225 % the Details window reached Windows as the bare display
/// name again, with #637's load handler in place. So the caption Windows actually holds is read
/// back with <c>GetWindowText</c> at each point the title bar might have written it, and set again
/// through <see cref="AppWindow.Title"/> whenever it is wrong — never compared with
/// <see cref="Window.Title"/>, which is the value this class asked for rather than the value the
/// window has. Each correction is logged with the event that found it.
/// </para>
/// <para>
/// <b>What the log found, 3 Oct 2026</b> (QA-Win10 and QA-Win11, 100 % and 225 %, Light, Dark and
/// high contrast): the title bar writes the caption <b>when it is first sized, which is after it has
/// loaded</b>. Every correction came from the size change, the caption being still right at load, so
/// #637's load handler had been working only because of the order the two happened to arrive in at
/// 100 %. The other hooks found nothing wrong and are kept as cheap insurance: each costs one
/// <c>GetWindowText</c> and writes nothing while the caption is right.
/// </para>
/// </remarks>
internal static class WindowCaption
{
    /// <summary>Gives <paramref name="window"/> <paramref name="caption"/>, and keeps it there.</summary>
    /// <param name="window">The window whose caption to keep.</param>
    /// <param name="titleBar">The title bar that overwrites it.</param>
    /// <param name="caption">The caption Windows should name the window by.</param>
    /// <param name="logger">Where corrections are recorded, if anywhere.</param>
    public static void Keep(Window window, TitleBar titleBar, string caption, ILogger? logger = null) =>
        _ = new Keeper(window, titleBar, caption, logger);

    /// <summary>The handlers, as methods so the one on the framework's XamlRoot can be removed.</summary>
    /// <remarks>
    /// The title bar and the AppWindow die with the window, so their subscriptions need no undoing.
    /// The XamlRoot is the framework's and outlives the window, so that one is removed on Closed —
    /// a handler left there holds the window, which is #487's leak.
    /// </remarks>
    private sealed class Keeper
    {
        private readonly Window _window;
        private readonly string _caption;
        private readonly ILogger? _logger;
        private readonly nint _handle;
        private XamlRoot? _root;

        public Keeper(Window window, TitleBar titleBar, string caption, ILogger? logger)
        {
            _window = window;
            _caption = caption;
            _logger = logger;
            _handle = WinRT.Interop.WindowNative.GetWindowHandle(window);

            window.Title = caption;
            titleBar.Loaded += OnTitleBarLoaded;
            titleBar.SizeChanged += (_, _) => Restore("the title bar's size changed");
            window.Activated += (_, _) => Restore("the window was activated");
            window.AppWindow.Changed += OnAppWindowChanged;
            window.Closed += OnClosed;
        }

        private void OnTitleBarLoaded(object sender, RoutedEventArgs e)
        {
            Restore("the title bar loaded");

            if (_root is null && sender is FrameworkElement element && element.XamlRoot is { } root)
            {
                _root = root;
                _root.Changed += OnRootChanged;
            }
        }

        private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) =>
            Restore("the XamlRoot changed");

        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (args.DidSizeChange || args.DidPositionChange || args.DidPresenterChange)
            {
                Restore("the window moved or resized");
            }
        }

        private void OnClosed(object sender, WindowEventArgs args)
        {
            _root?.Changed -= OnRootChanged;
            _root = null;
        }

        private void Restore(string when)
        {
            string actual = Read(_handle);
            if (string.Equals(actual, _caption, StringComparison.Ordinal))
            {
                return;
            }

            _window.AppWindow.Title = _caption;
            _logger?.LogInformation(
                "Window caption restored when {When}: it was \"{Actual}\", it is \"{Now}\"",
                when,
                actual,
                Read(_handle));
        }
    }

    private static string Read(nint handle)
    {
        char[] buffer = new char[256];
        int length = GetWindowTextW(handle, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(length, 0));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint window, [Out] char[] text, int maxCount);
}
