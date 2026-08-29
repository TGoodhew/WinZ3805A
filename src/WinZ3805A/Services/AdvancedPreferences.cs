namespace WinZ3805A.Services;

/// <summary>The opt-in advanced features, off on a fresh install.</summary>
/// <remarks>
/// <para>
/// <b>Opting in changes what is reachable, never what is permitted.</b> The §10.11 console is a
/// picker over the same §8.1 allowlist every other page uses, so turning it on adds no command the
/// application could not already send — it adds a way to reach the ones nothing else surfaces yet.
/// The §8.4 exclusions are absent from the catalog, so they are absent from the console, opted in
/// or not.
/// </para>
/// <para>
/// Off by default because §10.11 says hidden by default, and because a picker listing every
/// catalogued command including the tier C ones is not what a user wanting a glanceable clock
/// should meet on their first run.
/// </para>
/// </remarks>
public sealed record AdvancedPreferences
{
    /// <summary>Whether the §10.11 Advanced Console appears in the navigation pane.</summary>
    public bool IsConsoleEnabled { get; init; }

    /// <summary>Whether §8.5's undocumented read-only queries appear on the Diagnostics page.</summary>
    /// <remarks>
    /// Six queries, and nothing else. §8.4 excludes the <i>set</i> forms of undocumented nodes
    /// permanently and with no override, so this switch cannot reach them — it is the difference
    /// between six questions being visible and six questions being hidden, not between two modes.
    /// </remarks>
    public bool AreExperimentalQueriesEnabled { get; init; }

    /// <summary>Whether losing GPS lock raises a Windows notification (P1-9).</summary>
    /// <remarks>
    /// <b>On by default</b>, unlike the two switches above it, and for the opposite reason: those
    /// two reveal surfaces a user has to go looking for, while this one exists precisely for the
    /// user who is <i>not</i> looking. It is safe to default on only because <see cref="LockWatch"/>
    /// stays quiet through the flapping that dominates a real receiver's log — without that
    /// restraint the honest default would be off.
    /// </remarks>
    public bool AreLockNotificationsEnabled { get; init; } = true;

    /// <summary>Whether closing the window leaves the application running (#280).</summary>
    /// <remarks>
    /// <b>On by default</b>, for the reason §9.1 gives the whole application: this is left docked on
    /// a second monitor for weeks, and a close button that stops the polling stops it exactly when
    /// the user was trying to get the window out of the way. With it off, the tray icon and P1-9's
    /// notifications only work while a window is open somewhere, which is the case they are least
    /// needed for.
    /// </remarks>
    public bool KeepRunningWhenClosed { get; init; } = true;

    /// <summary>Whether the application starts with no window, in the notification area (#280).</summary>
    /// <remarks>
    /// Off by default. An application that starts with no window is indistinguishable from one that
    /// failed to start, so this is something a user asks for rather than meets.
    /// </remarks>
    public bool StartMinimised { get; init; }

    /// <summary>Whether the user has been told once that closing does not exit (#280).</summary>
    /// <remarks>
    /// <b>Not a preference the user sets; a fact the application remembers.</b> It lives here
    /// because this is the file that survives a restart, and the alternative — telling them every
    /// time — is the behaviour the acceptance criterion forbids. Silently changing what the close
    /// button does is the well-known way to annoy people; saying so once is the cure, and saying so
    /// twice is the disease.
    /// </remarks>
    public bool HasSeenCloseToTrayNotice { get; init; }

    /// <summary>A fresh install's preferences.</summary>
    public static AdvancedPreferences Default { get; } = new();
}

/// <summary>Where <see cref="AdvancedPreferences"/> are kept.</summary>
public interface IAdvancedPreferenceStore
{
    /// <summary>Reads the stored preferences, or <see cref="AdvancedPreferences.Default"/>.</summary>
    AdvancedPreferences Load();

    /// <summary>Writes the preferences.</summary>
    void Save(AdvancedPreferences preferences);
}

/// <summary>Keeps <see cref="AdvancedPreferences"/> in a JSON file beside the others.</summary>
public sealed class LocalAdvancedPreferenceStore : IAdvancedPreferenceStore
{
    private readonly JsonPreferenceFile<AdvancedPreferences> _file;

    /// <summary>Creates a store over the default location.</summary>
    public LocalAdvancedPreferenceStore()
        : this(DefaultPath())
    {
    }

    /// <summary>Creates a store over a given file, which the tests use.</summary>
    public LocalAdvancedPreferenceStore(string path) =>
        _file = new JsonPreferenceFile<AdvancedPreferences>(path, AdvancedPreferences.Default);

    /// <summary>Where the preferences live.</summary>
    public static string DefaultPath() => JsonPreferenceFile<AdvancedPreferences>.PathFor("advanced.json");

    /// <inheritdoc />
    public AdvancedPreferences Load() => _file.Load();

    /// <inheritdoc />
    public void Save(AdvancedPreferences preferences) => _file.Save(preferences);
}
