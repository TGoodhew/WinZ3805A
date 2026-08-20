using Microsoft.Extensions.DependencyInjection;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using WinZ3805A.Services;

namespace WinZ3805A.Views;

/// <summary>
/// The Settings page, currently carrying only §10.11's Advanced opt-in.
/// </summary>
/// <remarks>
/// See the XAML for why it holds one section: §10 has no section describing this page at all, and
/// #55 needs exactly one switch on it.
/// </remarks>
public sealed partial class SettingsPage : Page
{
    private IAdvancedPreferenceStore? _preferences;

    /// <summary>
    /// Raised when a setting changed that the window has to act on.
    /// </summary>
    /// <remarks>
    /// The console appears and disappears from the navigation pane, which is the window's to
    /// rebuild rather than a page's. A page reaching up into <c>Nav.FooterMenuItems</c> would be a
    /// page that only works inside one window.
    /// </remarks>
    public static event EventHandler? AdvancedChanged;

    /// <summary>Creates the page.</summary>
    public SettingsPage() => InitializeComponent();

    /// <summary>
    /// False until the stored value has been restored.
    /// </summary>
    /// <remarks>
    /// <c>ToggleSwitch.IsOn</c> raises <c>Toggled</c>, so without this, restoring the preference
    /// saves it straight back and — worse — raises <see cref="AdvancedChanged"/> on every
    /// navigation to this page.
    /// </remarks>
    private bool _ready;

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _preferences = App.Services?.GetService<IAdvancedPreferenceStore>();
        ConsoleSwitch.IsOn = _preferences?.Load().IsConsoleEnabled ?? false;
        _ready = true;
    }

    private void OnConsoleToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        _preferences?.Save(new AdvancedPreferences { IsConsoleEnabled = ConsoleSwitch.IsOn });
        AdvancedChanged?.Invoke(this, EventArgs.Empty);
    }
}
