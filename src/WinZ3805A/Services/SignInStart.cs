namespace WinZ3805A.Services;

/// <summary>What happens when the user signs in to Windows (#548).</summary>
public enum SignInStartChoice
{
    /// <summary>The application does not start at sign-in.</summary>
    Off,

    /// <summary>It starts with no window, monitoring from the notification area.</summary>
    NotificationArea,

    /// <summary>It starts with its window open.</summary>
    Window,
}

/// <summary>
/// The package's startup task as Windows reports it, plus the case where Windows could not say.
/// </summary>
/// <remarks>
/// Mirrors <c>Windows.ApplicationModel.StartupTaskState</c> member for member, so that
/// <see cref="SignInStartPolicy"/> can be tested without WinRT. <see cref="Unavailable"/> is the one
/// addition: the task cannot be read outside a package, and reading it can fail.
/// </remarks>
public enum SignInTaskState
{
    /// <summary>Off, and the application may turn it on.</summary>
    Disabled,

    /// <summary>Turned off by the user in Windows; the application cannot turn it back on.</summary>
    DisabledByUser,

    /// <summary>On.</summary>
    Enabled,

    /// <summary>Turned off by group policy.</summary>
    DisabledByPolicy,

    /// <summary>Turned on by group policy.</summary>
    EnabledByPolicy,

    /// <summary>Windows could not be asked, or did not answer.</summary>
    Unavailable,
}

/// <summary>What the Settings control shows for one state of the startup task.</summary>
/// <param name="Choice">The option shown as selected.</param>
/// <param name="CanChange">Whether the control accepts a change.</param>
/// <param name="Note">
/// Why it cannot, and what to do instead, or <see langword="null"/> when there is nothing to say.
/// </param>
public sealed record SignInStartView(SignInStartChoice Choice, bool CanChange, string? Note);

/// <summary>
/// Maps the startup task's state and the stored preference onto the Settings control (#548).
/// </summary>
/// <remarks>
/// <para>
/// <b>Windows owns whether it is on; this application owns only how it starts.</b> The user can
/// turn the task off in Task Manager or in Windows Settings without opening this application, so
/// the state is read from Windows every time the page is shown and no copy is kept that could
/// disagree with it. What is stored is the one thing Windows does not know: whether a sign-in start
/// goes to the notification area or opens the window.
/// </para>
/// <para>
/// <b>A switch the application cannot move is shown as such, with the reason.</b> Once the user has
/// turned the task off in Windows, <c>RequestEnableAsync</c> returns without enabling anything. A
/// control that let them pick an option and then quietly snapped back to Off would look broken, so
/// it is disabled and says where the setting now lives.
/// </para>
/// </remarks>
public static class SignInStartPolicy
{
    /// <summary>The note for a task the user turned off in Windows.</summary>
    public const string DisabledByUserNote =
        "Turned off in Windows, so it can't be turned back on from here. "
        + "To turn it on, open Windows Settings, then Apps, then Startup.";

    /// <summary>The note for a task turned off by group policy.</summary>
    public const string DisabledByPolicyNote = "Your organisation has turned this off.";

    /// <summary>The note for a task turned on by group policy.</summary>
    public const string EnabledByPolicyNote = "Your organisation has turned this on, so it can't be changed here.";

    /// <summary>The note for a task whose state could not be read.</summary>
    public const string UnavailableNote = "Windows didn't say whether this is on, so it can't be changed right now.";

    /// <summary>Returns what the control shows.</summary>
    /// <param name="state">The startup task's state, as just read from Windows.</param>
    /// <param name="startHidden">The stored preference: whether a sign-in start has no window.</param>
    public static SignInStartView For(SignInTaskState state, bool startHidden)
    {
        SignInStartChoice on = startHidden ? SignInStartChoice.NotificationArea : SignInStartChoice.Window;

        return state switch
        {
            SignInTaskState.Enabled => new SignInStartView(on, CanChange: true, Note: null),
            SignInTaskState.Disabled => new SignInStartView(SignInStartChoice.Off, CanChange: true, Note: null),
            SignInTaskState.DisabledByUser => new SignInStartView(SignInStartChoice.Off, CanChange: false, DisabledByUserNote),
            SignInTaskState.DisabledByPolicy => new SignInStartView(SignInStartChoice.Off, CanChange: false, DisabledByPolicyNote),
            SignInTaskState.EnabledByPolicy => new SignInStartView(on, CanChange: false, EnabledByPolicyNote),
            _ => new SignInStartView(SignInStartChoice.Off, CanChange: false, UnavailableNote),
        };
    }

    /// <summary>
    /// Whether a launch should begin with no window (#548, #280).
    /// </summary>
    /// <param name="launch">How this launch began.</param>
    /// <param name="preferences">The stored preferences.</param>
    /// <remarks>
    /// A sign-in start follows its own setting, and a launch by hand follows <i>Start in the
    /// notification area</i>. They are separate so that a user who wants the application quiet at
    /// sign-in does not also get a windowless start when they open it themselves from Start - the
    /// case §10.13 defaults that switch off for, since it looks exactly like a failed start.
    /// </remarks>
    public static bool StartsHidden(LaunchContext launch, AdvancedPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(preferences);

        return launch.IsSignInStart ? preferences.StartAtSignInHidden : preferences.StartMinimised;
    }
}

/// <summary>How this run of the application began.</summary>
/// <param name="IsSignInStart">
/// Whether Windows started it at sign-in through the package's startup task (#548), rather than a
/// person starting it.
/// </param>
public sealed record LaunchContext(bool IsSignInStart)
{
    /// <summary>A launch by a person, which is every launch before #548.</summary>
    public static LaunchContext ByHand { get; } = new(IsSignInStart: false);
}
