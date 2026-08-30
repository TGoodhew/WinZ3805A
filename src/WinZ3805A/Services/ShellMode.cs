using WinZ3805A.Controls;

namespace WinZ3805A.Services;

/// <summary>
/// The mode a shell surface should be showing.
/// </summary>
/// <remarks>
/// <para>
/// One expression, in one place, because three surfaces show it — the notification area, the
/// taskbar badge and the window — and the invariant that matters is that they agree with each
/// other about the same receiver. A mode is a claim about <i>now</i>, and a link that has dropped
/// no longer justifies the last one the store happened to hold, whatever readings are still on
/// screen going stale honestly (§9.11).
/// </para>
/// <para>
/// <b>It lives in its own file since #319</b>, and both facts about that are the same fact: it was
/// declared at the bottom of <c>TaskbarOverlayService.cs</c>, which speaks WinUI and so cannot be
/// linked into the test project — so <c>MainViewModel</c>, which is linked in, could not call it
/// and restated the expression instead. The duplication the type existed to prevent was caused by
/// where the type was kept.
/// </para>
/// </remarks>
public static class ShellMode
{
    /// <summary>The mode to show for a store and a connection state.</summary>
    public static ReceiverMode For(ReceiverStateStore store, ConnectionStatus connection)
    {
        ArgumentNullException.ThrowIfNull(store);

        return connection == ConnectionStatus.Connected
            ? ReceiverModes.FromSyncState(store.SyncState)
            : ReceiverMode.Disconnected;
    }
}
