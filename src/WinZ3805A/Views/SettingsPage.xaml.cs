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
/// See the XAML for why it holds one section. §10.13.1 lists what is deliberately absent and what
/// is merely unbuilt, which are different things and are shown differently.
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

        AdvancedPreferences stored = _preferences?.Load() ?? AdvancedPreferences.Default;
        ConsoleSwitch.IsOn = stored.IsConsoleEnabled;
        ExperimentalSwitch.IsOn = stored.AreExperimentalQueriesEnabled;
        LockNotificationsSwitch.IsOn = stored.AreLockNotificationsEnabled;

        _ready = true;
    }

    private void OnConsoleToggled(object sender, RoutedEventArgs e) => Save();

    private void OnExperimentalToggled(object sender, RoutedEventArgs e) => Save();

    private void OnLockNotificationsToggled(object sender, RoutedEventArgs e) => Save();

    /// <summary>
    /// Writes both switches, because the record is written whole.
    /// </summary>
    /// <remarks>
    /// Saving one field at a time would mean constructing the record from one switch and the
    /// default for the other, which silently turns the other one off. The record is small; writing
    /// it whole is both simpler and the only version that is correct.
    /// </remarks>
    private void Save()
    {
        if (!_ready)
        {
            return;
        }

        _preferences?.Save(new AdvancedPreferences
        {
            IsConsoleEnabled = ConsoleSwitch.IsOn,
            AreExperimentalQueriesEnabled = ExperimentalSwitch.IsOn,
            AreLockNotificationsEnabled = LockNotificationsSwitch.IsOn,
        });

        // The notifier reads its own switch rather than being told, so this is one call whatever
        // changed - and switching it off resets the policy as well as silencing it, so turning it
        // back on cannot announce an outage that began while nobody was listening.
        App.StartLockNotifications();

        AdvancedChanged?.Invoke(this, EventArgs.Empty);
    }
}
