using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using Microsoft.UI;

using Windows.ApplicationModel;
using Windows.Storage;
using Windows.Storage.Pickers;

using WinRT.Interop;

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
        // §6.3: the product name is read at runtime and never hard-coded in XAML. Both the card's
        // header and the button carry it, so the row still names what it quits when the pane is
        // narrow enough for SettingsCard to stack the control under its header.
        ExitCard.Header = $"Exit {Package.Current.DisplayName}";
        ExitButton.Content = $"Exit {Package.Current.DisplayName}";

        _signInDescription = SignInStartNote.Text;
    }

    /// <summary>The start-at-sign-in card's own description, shown when there is no note instead.</summary>
    private readonly string _signInDescription;

    /// <summary>
    /// False while the start-at-sign-in choice is being set from Windows' answer (#548).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="_ready"/> because the state arrives asynchronously, after the
    /// switches, and because setting the selection raises <c>SelectionChanged</c> - which would
    /// otherwise ask Windows to change the very state it had just reported.
    /// </remarks>
    private bool _signInReady;

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
        ActivityLampSwitch.IsOn = stored.IsActivityLampEnabled;
        LockNotificationsSwitch.IsOn = stored.AreLockNotificationsEnabled;
        KeepRunningSwitch.IsOn = stored.KeepRunningWhenClosed;
        StartMinimisedSwitch.IsOn = stored.StartMinimised;

        _appearance = App.Services?.GetService<IAppearancePreferenceStore>();
        SystemAccentSwitch.IsOn = Appearance.UseSystemAccent;

        _ready = true;

        // Read from Windows every time, never remembered: the user can switch it off in Task Manager
        // while this page is closed (#548).
        _ = ShowSignInStartAsync(SignInTask.GetStateAsync(SignInLog()));
    }

    /// <summary>Shows the start-at-sign-in choice for the state Windows reports (#548).</summary>
    /// <param name="reading">The call that returns the state: a read, an enable or a disable.</param>
    /// <remarks>
    /// Whatever was asked for, what is shown is what Windows then says, so a request it refused
    /// shows as refused - with the reason, from <see cref="SignInStartPolicy"/> - rather than as the
    /// option the user picked. Never throws: <see cref="SignInTask"/> answers
    /// <see cref="SignInTaskState.Unavailable"/> instead.
    /// </remarks>
    private async Task ShowSignInStartAsync(Task<SignInTaskState> reading)
    {
        _signInReady = false;
        SignInStartBox.IsEnabled = false;

        SignInTaskState state = await reading;
        bool hidden = (_preferences?.Load() ?? AdvancedPreferences.Default).StartAtSignInHidden;
        SignInStartView view = SignInStartPolicy.For(state, hidden);

        SignInStartBox.SelectedIndex = (int)view.Choice;
        SignInStartBox.IsEnabled = view.CanChange;
        SignInStartNote.Text = view.Note ?? _signInDescription;

        _signInReady = true;
    }

    /// <summary>Turns start at sign-in on or off, or changes how it starts (#548).</summary>
    /// <remarks>
    /// How it starts is saved before Windows is asked to turn it on, so that a sign-in straight
    /// after already starts the way the control says.
    /// </remarks>
    private async void OnSignInStartChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_signInReady || SignInStartBox.SelectedIndex < 0)
        {
            return;
        }

        SignInStartChoice choice = (SignInStartChoice)SignInStartBox.SelectedIndex;

        if (choice == SignInStartChoice.Off)
        {
            await ShowSignInStartAsync(SignInTask.DisableAsync(SignInLog()));
            return;
        }

        _preferences?.Save(_preferences.Load() with
        {
            StartAtSignInHidden = choice == SignInStartChoice.NotificationArea,
        });

        await ShowSignInStartAsync(SignInTask.EnableAsync(SignInLog()));
    }

    /// <summary>Saves the whole history to a file the user chooses (#551).</summary>
    /// <remarks>
    /// Written to a private file first and then copied into the chosen one, through the
    /// <c>StorageFile</c> the picker returned: SQLite writes by path and will not overwrite, while
    /// the picker has already created the file, and a cloud-backed folder wants its updates
    /// deferred rather than synced half-written - the same reason the CSV export works this way.
    /// </remarks>
    private async void OnExportHistoryClicked(object sender, RoutedEventArgs e)
    {
        if (App.Services?.GetService<TrendStore>() is not TrendStore store || XamlRoot is null)
        {
            return;
        }

        TimeProvider time = App.Services.GetRequiredService<TimeProvider>();

        FileSavePicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"receiver-history-{time.GetLocalNow():yyyy-MM-dd}",
        };

        picker.FileTypeChoices.Add("Receiver history", [HistoryFile.Extension]);
        InitializeWithWindow.Initialize(picker, OwnerWindow());

        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        string scratch = ScratchFile();
        ExportHistoryButton.IsEnabled = false;

        try
        {
            // Read after the picker, not before: the user can leave it open, and the manifest's
            // export time must not be older than the newest sample in the file.
            DateTimeOffset now = time.GetUtcNow();
            string? receiver = ConnectedReceiver();
            string version = AppVersion();
            HistoryManifest manifest = await Task.Run(() => store.ExportTo(scratch, receiver, version, now));

            CachedFileManager.DeferUpdates(file);

            using (Stream target = await file.OpenStreamForWriteAsync())
            {
                target.SetLength(0);

                await using FileStream source = File.OpenRead(scratch);
                await source.CopyToAsync(target);
            }

            await CachedFileManager.CompleteUpdatesAsync(file);

            ShowHistoryStatus(HistoryText.Exported(manifest, file.Name, TimeZoneInfo.Local));
            HistoryLog()?.LogInformation("History exported: {Rows} samples to {File}.", manifest.Rows, file.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException or ObjectDisposedException)
        {
            HistoryLog()?.LogWarning(exception, "History export to {File} failed.", file.Path);
            await ShowHistoryProblemAsync("Couldn't export the history", $"{file.Name} could not be written. {exception.Message}");
        }
        finally
        {
            DeleteScratch(scratch);
            ExportHistoryButton.IsEnabled = true;
        }
    }

    /// <summary>Adds the readings in an exported file to this history, after saying what that will do (#551).</summary>
    /// <remarks>
    /// <para>
    /// <b>A private copy is inspected and imported, never the chosen file itself.</b> The original
    /// may be in a cloud-backed folder, open elsewhere, or a <c>trend.db</c> in WAL mode that needs
    /// companion files created beside it just to be read.
    /// </para>
    /// <para>
    /// <b>Nothing changes until the user has read the confirmation</b>, because an import cannot be
    /// undone. When the file came from a different receiver, Cancel is the default button: the
    /// safe choice is the one Enter makes.
    /// </para>
    /// </remarks>
    private async void OnImportHistoryClicked(object sender, RoutedEventArgs e)
    {
        if (App.Services?.GetService<TrendStore>() is not TrendStore store || XamlRoot is null)
        {
            return;
        }

        FileOpenPicker picker = new() { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(HistoryFile.Extension);
        picker.FileTypeFilter.Add(".db");
        InitializeWithWindow.Initialize(picker, OwnerWindow());

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        string scratch = ScratchFile();
        ImportHistoryButton.IsEnabled = false;

        try
        {
            using (Stream source = await file.OpenStreamForReadAsync())
            {
                await using FileStream copy = File.Create(scratch);
                await source.CopyToAsync(copy);
            }

            long now = App.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcTicks;
            HistoryInspection inspection = await Task.Run(() => HistoryFile.Inspect(scratch, now - store.Retention.Ticks));

            if (!inspection.IsValid)
            {
                await ShowHistoryProblemAsync("Couldn't import the history", $"{file.Name}: {inspection.Problem}");
                return;
            }

            if (inspection.RowsInRetention == 0)
            {
                await ShowHistoryProblemAsync(
                    "Nothing to import",
                    $"Every reading in {file.Name} is older than the {store.Retention.TotalDays:0} days this history keeps, so none of it would stay.");
                return;
            }

            string? receiver = ConnectedReceiver();
            ReceiverMatch match = HistoryFile.Compare(inspection.Manifest?.ReceiverIdentity, receiver);

            ContentDialog confirm = new()
            {
                XamlRoot = XamlRoot,
                Title = "Import this history?",
                Content = new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = HistoryText.Describe(inspection, receiver, store.Retention, TimeZoneInfo.Local),
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
                PrimaryButtonText = "Import",
                CloseButtonText = "Cancel",
                DefaultButton = match == ReceiverMatch.Different ? ContentDialogButton.Close : ContentDialogButton.Primary,
            };

            if (await confirm.KeepBelowTitleBar().ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            HistoryImportResult result = await Task.Run(() => store.Import(scratch, now));

            ShowHistoryStatus(HistoryText.Imported(result, file.Name));
            HistoryLog()?.LogInformation(
                "History imported from {File}: {Added} samples added, {Present} already present; receiver {Match}.",
                file.Path,
                result.Added,
                result.AlreadyPresent,
                match);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException or ObjectDisposedException)
        {
            HistoryLog()?.LogWarning(exception, "History import from {File} failed.", file.Path);
            await ShowHistoryProblemAsync(
                "Couldn't import the history",
                $"{file.Name} could not be imported, and nothing in this history was changed. {exception.Message}");
        }
        finally
        {
            DeleteScratch(scratch);
            ImportHistoryButton.IsEnabled = true;
        }
    }

    /// <summary>The window this page is in, which owns the pickers.</summary>
    /// <remarks>From the XamlRoot, as the CSV export does: a page cannot see its own Window.</remarks>
    private nint OwnerWindow() =>
        Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);

    /// <summary>The connected receiver's <c>*IDN?</c> answer, or null when nothing is connected.</summary>
    private static string? ConnectedReceiver() =>
        App.Services?.GetKeyedService<DeviceContext>(DeviceKeys.Primary)?.Session is { Status: ConnectionStatus.Connected } session
            ? session.Identity
            : null;

    private static string AppVersion()
    {
        PackageVersion version = Package.Current.Id.Version;
        return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
    }

    /// <summary>A file of the application's own, in its temporary folder, that does not exist yet.</summary>
    private static string ScratchFile() =>
        Path.Combine(Path.GetTempPath(), $"wz-history-{Guid.NewGuid():N}{HistoryFile.Extension}");

    /// <summary>Removes a scratch file and any journal SQLite left beside it.</summary>
    private static void DeleteScratch(string path)
    {
        foreach (string file in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A temporary file Windows will clear eventually is not worth an error.
            }
        }
    }

    private void ShowHistoryStatus(string text)
    {
        HistoryStatus.Text = text;
        HistoryStatus.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Reports a failed export or import.
    /// </summary>
    /// <remarks>
    /// Loud where a preference failure is silent (§10.13): this is the user's only copy of history
    /// that cannot be gathered again, and a picker that closes on nothing reads as success.
    /// </remarks>
    private async Task ShowHistoryProblemAsync(string title, string message)
    {
        HistoryStatus.Visibility = Visibility.Collapsed;

        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "Close",
        }.KeepBelowTitleBar().ShowAsync();
    }

    private static ILogger? HistoryLog() =>
        App.Services?.GetService<ILoggerFactory>()?.CreateLogger("History");

    /// <summary>Where the startup task's answers are recorded (#548).</summary>
    private static ILogger? SignInLog() =>
        App.Services?.GetService<ILoggerFactory>()?.CreateLogger("SignIn");

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

    /// <summary>
    /// Applies the lamp switch to the receiver that is connected now (#440).
    /// </summary>
    /// <remarks>
    /// <b>Immediately, rather than at the next connect.</b> A switch whose effect appears the next
    /// time you plug something in is a switch a user cannot tell they have set — and this one's
    /// whole purpose is a light they can look at. Turning it off restores what the lamp read before
    /// the application borrowed it, which is the same thing a disconnect does.
    /// </remarks>
    private async void OnActivityLampToggled(object sender, RoutedEventArgs e)
    {
        Save();

        if (!_ready || App.Services?.GetKeyedService<DeviceContext>(DeviceKeys.Primary) is not DeviceContext device)
        {
            return;
        }

        // #462: the per-command flashing starts and stops with the switch rather than at the next
        // connect, for the same reason the lamp itself is armed here — a setting that only takes
        // effect the next time you plug something in is a switch a user cannot tell they have set.
        device.Session.FlashLampPerCommand = ActivityLampSwitch.IsOn;

        try
        {
            if (ActivityLampSwitch.IsOn)
            {
                await device.Lamp.ArmAsync();
            }
            else
            {
                await device.Lamp.RestoreAsync();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // A lamp that would not change is not a setting that failed to save. The preference is
            // already written above, and the Diagnostics page can set the lamp by hand.
        }
    }

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
            IsActivityLampEnabled = ActivityLampSwitch.IsOn,
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
