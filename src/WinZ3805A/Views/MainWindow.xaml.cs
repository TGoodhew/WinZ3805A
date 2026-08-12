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

        RootFrame.Navigate(typeof(MainPage));
    }
}
