using Microsoft.Extensions.Logging;

using Windows.ApplicationModel;

namespace WinZ3805A.Services;

/// <summary>
/// The package's startup task: the one thing that makes Windows start this application at sign-in
/// (#548).
/// </summary>
/// <remarks>
/// <para>
/// <b>A startup task and not a <c>Run</c> key or a scheduled task.</b> Both of those work for a
/// desktop program and both are wrong for an MSIX: the package's registry writes are virtualised,
/// and either would outlive an uninstall, starting a program that no longer exists at every
/// sign-in. The task is declared in <c>Package.appxmanifest</c>, is removed with the package, and
/// appears in Task Manager and in Windows Settings, where the user can switch it off without this
/// application's help.
/// </para>
/// <para>
/// <b>Every call answers rather than throws.</b> A task that cannot be read (outside a package, or
/// when the call fails) is <see cref="SignInTaskState.Unavailable"/>, which the Settings control shows
/// as a disabled Off with a reason. Starting at sign-in is a convenience, and no failure of it is a
/// reason for the Settings page not to open.
/// </para>
/// </remarks>
public static class SignInTask
{
    /// <summary>The task's id, which must match <c>uap5:StartupTask/@TaskId</c> in the manifest.</summary>
    public const string TaskId = "WinZ3805AStartAtSignIn";

    /// <summary>Reads the task's state.</summary>
    public static Task<SignInTaskState> GetStateAsync(ILogger? log) =>
        CallAsync(task => Task.FromResult(task.State), "read", log);

    /// <summary>Asks Windows to start the application at sign-in, and returns the resulting state.</summary>
    /// <remarks>
    /// Returns <see cref="SignInTaskState.DisabledByUser"/> without enabling anything when the user
    /// has switched the task off in Windows; only they can switch it back on, from there.
    /// </remarks>
    public static Task<SignInTaskState> EnableAsync(ILogger? log) =>
        CallAsync(async task => await task.RequestEnableAsync(), "enable", log);

    /// <summary>Stops the application starting at sign-in, and returns the resulting state.</summary>
    public static Task<SignInTaskState> DisableAsync(ILogger? log) =>
        CallAsync(
            task =>
            {
                task.Disable();
                return Task.FromResult(task.State);
            },
            "disable",
            log);

    private static async Task<SignInTaskState> CallAsync(
        Func<StartupTask, Task<StartupTaskState>> call,
        string what,
        ILogger? log)
    {
        try
        {
            StartupTask task = await StartupTask.GetAsync(TaskId);
            SignInTaskState state = Map(await call(task));

            log?.LogInformation("Start at sign-in: {What} returned {State}.", what, state);
            return state;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            log?.LogWarning(exception, "Start at sign-in: could not {What} the startup task.", what);
            return SignInTaskState.Unavailable;
        }
    }

    private static SignInTaskState Map(StartupTaskState state) => state switch
    {
        StartupTaskState.Disabled => SignInTaskState.Disabled,
        StartupTaskState.DisabledByUser => SignInTaskState.DisabledByUser,
        StartupTaskState.Enabled => SignInTaskState.Enabled,
        StartupTaskState.DisabledByPolicy => SignInTaskState.DisabledByPolicy,
        StartupTaskState.EnabledByPolicy => SignInTaskState.EnabledByPolicy,
        _ => SignInTaskState.Unavailable,
    };
}
