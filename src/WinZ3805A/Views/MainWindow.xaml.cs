using Microsoft.UI.Xaml;

using Windows.ApplicationModel;

namespace WinZ3805A.Views;

/// <summary>
/// The application window. Scaffolding only — §10.3 specifies the shipped main
/// window as a small status-medallion surface, and §9.7 puts the NavigationView
/// shell in a separate Receiver Details window.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // §6.3: the display name is read from the manifest, never hard-coded.
        // Package identity is effectively permanent; the display name is a
        // one-line change, and coupling them in code destroys that option.
        string displayName = Package.Current.DisplayName;
        Title = displayName;
        AppTitleBar.Title = displayName;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // §10.3 / P0-3: the window never gets smaller than the compact layout needs. Enforced on
        // the AppWindow rather than by a MinWidth on the content, because the user drags the frame,
        // not the page.
        AppWindow.Changed += OnAppWindowChanged;

        RootFrame.Navigate(typeof(MainPage));
    }

    /// <summary>The §10.3 floor: 380 x 240, which is what the compact layout needs to stay legible.</summary>
    private const int MinimumWidth = 380;
    private const int MinimumHeight = 240;

    /// <remarks>
    /// WinUI has no MinWidth on AppWindow, so the size is corrected after the fact. Comparing before
    /// resizing matters: assigning the same size again inside the change handler would recurse.
    /// </remarks>
    private void OnAppWindowChanged(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange)
        {
            return;
        }

        int width = Math.Max(sender.Size.Width, MinimumWidth);
        int height = Math.Max(sender.Size.Height, MinimumHeight);

        if (width != sender.Size.Width || height != sender.Size.Height)
        {
            sender.Resize(new Windows.Graphics.SizeInt32(width, height));
        }
    }
}
