using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using Windows.ApplicationModel;

using WinZ3805A.Controls;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

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
    private IAppearancePreferenceStore? _appearance;

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
    public SettingsPage()
    {
        InitializeComponent();

        // §6.3: read the display name from the manifest, never hard-code it in XAML. This button
        // carried the literal "Exit WinZ3805A" until #319 — the one place in the application that
        // had it, which is exactly how a rename would have shipped a window whose title said one
        // thing and whose Exit button said another.
        ExitButton.Content = $"Exit {Package.Current.DisplayName}";
    }

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
        KeepRunningSwitch.IsOn = stored.KeepRunningWhenClosed;
        StartMinimisedSwitch.IsOn = stored.StartMinimised;

        _appearance = App.Services?.GetService<IAppearancePreferenceStore>();
        SystemAccentSwitch.IsOn = Appearance.UseSystemAccent;

        _ready = true;
    }

    /// <summary>The stored appearance preferences, or the defaults.</summary>
    private AppearancePreferences Appearance =>
        _appearance?.Load() ?? AppearancePreferences.Default;

    /// <summary>
    /// Saves the accent choice, applies it, and warns if it collides (§9.4.2).
    /// </summary>
    /// <remarks>
    /// The palette is applied before the tip is shown, on purpose: the warning is about a colour
    /// the user can see, and describing a collision that has not happened yet would leave them
    /// deciding in the abstract. Switching it on and immediately seeing why is the whole argument.
    /// </remarks>
    private void OnSystemAccentToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || _appearance is null)
        {
            return;
        }

        _appearance.Save(Appearance with { UseSystemAccent = SystemAccentSwitch.IsOn });
        App.ApplyAccent();

        AccentRamp? system = AccentPalette.ReadSystemRamp(AccentLog());
        AccentCollision? collision = AppearanceViewModel.WarningFor(Appearance, system);

        if (collision is null)
        {
            CollisionTip.IsOpen = false;
            return;
        }

        CollisionTip.Subtitle = AccentGuard.Describe(collision);
        CollisionTip.IsOpen = true;
    }

    /// <summary>
    /// Takes the tip's offer and goes back to the built-in accent.
    /// </summary>
    private void OnRevertAccent(TeachingTip sender, object args)
    {
        if (_appearance is null)
        {
            return;
        }

        _appearance.Save(AppearanceViewModel.Revert(Appearance));

        // The switch raises Toggled, which would save and re-evaluate on top of what was just
        // written. Suppressed rather than reasoned about: the store is already correct.
        _ready = false;
        SystemAccentSwitch.IsOn = false;
        _ready = true;

        App.ApplyAccent();
        sender.IsOpen = false;
    }

    /// <summary>
    /// Dismisses the tip, recording which accent it was dismissed for.
    /// </summary>
    /// <remarks>
    /// The colour is stored so that a later change to a different colliding accent is warned about
    /// again — see <see cref="AppearanceViewModel.WarningFor"/>.
    /// </remarks>
    private void OnKeepAccent(TeachingTip sender, object args)
    {
        if (_appearance is not null
            && AccentPalette.ReadSystemRamp(AccentLog()) is AccentRamp system)
        {
            _appearance.Save(AppearanceViewModel.Acknowledge(Appearance, system));
        }

        sender.IsOpen = false;
    }

    private void OnConsoleToggled(object sender, RoutedEventArgs e) => Save();

    private void OnExperimentalToggled(object sender, RoutedEventArgs e) => Save();

    private void OnLockNotificationsToggled(object sender, RoutedEventArgs e) => Save();

    private void OnKeepRunningToggled(object sender, RoutedEventArgs e) => Save();

    private void OnStartMinimisedToggled(object sender, RoutedEventArgs e) => Save();

    /// <summary>Quits, without needing the notification area to be reachable (#280).</summary>
    /// <remarks>
    /// No confirmation. Polling is not a transaction and <c>trend.db</c> commits as it goes, so
    /// there is nothing to lose by stopping - and a prompt on the way out of an application whose
    /// close button already asks a question once would be the second interruption in the same job.
    /// </remarks>
    private void OnExitClicked(object sender, RoutedEventArgs e) =>
        (Application.Current as App)?.RequestExit();

    /// <summary>
    /// Writes every switch at once, onto the record that is already stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Saving one field at a time would construct the record from one switch and the defaults for
    /// the others, silently turning them off. The original note said as much and built a whole new
    /// record from the switches, which was correct while every field <i>was</i> a switch.
    /// </para>
    /// <para>
    /// <b>It stopped being correct with #280.</b> <c>HasSeenCloseToTrayNotice</c> is a fact the
    /// application remembers rather than a preference anyone sets, so no switch carries it - and a
    /// freshly constructed record would reset it to false on every settings change, re-showing a
    /// notice whose entire purpose is to appear once. So this loads and applies <c>with</c>:
    /// switches overwrite their own fields and nothing else is touched, which stays right as fields
    /// are added.
    /// </para>
    /// </remarks>
    private void Save()
    {
        if (!_ready || _preferences is null)
        {
            return;
        }

        _preferences.Save(_preferences.Load() with
        {
            IsConsoleEnabled = ConsoleSwitch.IsOn,
            AreExperimentalQueriesEnabled = ExperimentalSwitch.IsOn,
            AreLockNotificationsEnabled = LockNotificationsSwitch.IsOn,
            KeepRunningWhenClosed = KeepRunningSwitch.IsOn,
            StartMinimised = StartMinimisedSwitch.IsOn,
        });

        // The notifier reads its own switch rather than being told, so this is one call whatever
        // changed - and switching it off resets the policy as well as silencing it, so turning it
        // back on cannot announce an outage that began while nobody was listening.
        App.StartLockNotifications();

        AdvancedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The logger a failed accent read is recorded through (#290).</summary>
    /// <remarks>
    /// These two call sites are on the preview path, where a user has just chosen the Windows
    /// accent and is being shown the result. A read that fails here is exactly the moment they need
    /// explaining, so they get a logger rather than the null default.
    /// </remarks>
    private static ILogger? AccentLog() =>
        App.Services?.GetService<ILoggerFactory>()?.CreateLogger("Accent");
}
