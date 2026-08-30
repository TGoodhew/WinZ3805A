using WinZ3805A.Controls;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// What A11Y-9 says out loud, and — as often — what it declines to.
/// </summary>
/// <remarks>
/// A live region that announces too much is worse than one that announces nothing: a reader that
/// interrupts every second is switched off, and then the transitions that mattered are gone too.
/// Half of these tests assert silence for that reason.
/// </remarks>
public sealed class StateAnnouncerTests
{
    [Fact]
    public void FirstObservationIsSilent()
    {
        StateAnnouncer announcer = new();

        Assert.Null(announcer.Observe(ConnectionStatus.Connected, ReceiverMode.Locked, isCoasting: false));
    }

    [Fact]
    public void UnchangedStateIsSilent()
    {
        StateAnnouncer announcer = new();
        announcer.Observe(ConnectionStatus.Connected, ReceiverMode.Locked, isCoasting: false);

        Assert.Null(announcer.Observe(ConnectionStatus.Connected, ReceiverMode.Locked, isCoasting: false));
        Assert.Null(announcer.Observe(ConnectionStatus.Connected, ReceiverMode.Locked, isCoasting: false));
    }

    [Fact]
    public void ModeChangeIsAnnouncedInTheWordsOnScreen()
    {
        StateAnnouncer announcer = new();
        announcer.Observe(ConnectionStatus.Connected, ReceiverMode.Locked, isCoasting: false);

        Announcement? announcement =
            announcer.Observe(ConnectionStatus.Connected, ReceiverMode.Recovering, isCoasting: false);

        Assert.NotNull(announcement);
        Assert.Equal(ReceiverModes.TextOf(ReceiverMode.Recovering), announcement.Text);
        Assert.Equal(AnnouncementUrgency.Polite, announcement.Urgency);
    }

    /// <remarks>
    /// Holdover is the one mode §9.4.3 calls Critical, and urgency is derived from that severity
    /// rather than listed again — so this test is as much about the two not diverging.
    /// </remarks>
    [Fact]
    public void HoldoverInterrupts()
    {
        StateAnnouncer announcer = new();
        announcer.Observe(ConnectionStatus.Connected, ReceiverMode.Locked, isCoasting: false);

        Announcement? announcement =
            announcer.Observe(ConnectionStatus.Connected, ReceiverMode.Holdover, isCoasting: false);

        Assert.NotNull(announcement);
        Assert.Equal(AnnouncementUrgency.Assertive, announcement.Urgency);
    }

    [Fact]
    public void ConnectionLostInterruptsAndIsNotCalledDisconnected()
    {
        StateAnnouncer announcer = new();
        announcer.Observe(ConnectionStatus.Connected, ReceiverMode.Locked, isCoasting: false);

        Announcement? announcement =
            announcer.Observe(ConnectionStatus.Faulted, ReceiverMode.Disconnected, isCoasting: false);

        Assert.NotNull(announcement);
        Assert.Equal("Connection lost.", announcement.Text);
        Assert.Equal(AnnouncementUrgency.Assertive, announcement.Urgency);
    }

    /// <remarks>
    /// §9.11 keeps the two apart on screen; the difference is exactly what a listener cannot see.
    /// </remarks>
    [Fact]
    public void DeliberateDisconnectDoesNotInterrupt()
    {
        StateAnnouncer announcer = new();
        announcer.Observe(ConnectionStatus.Connected, ReceiverMode.Locked, isCoasting: false);

        Announcement? announcement =
            announcer.Observe(ConnectionStatus.Disconnected, ReceiverMode.Disconnected, isCoasting: false);

        Assert.NotNull(announcement);
        Assert.Equal("Disconnected.", announcement.Text);
        Assert.Equal(AnnouncementUrgency.Polite, announcement.Urgency);
    }

    [Fact]
    public void ConnectingNamesThePort()
    {
        StateAnnouncer announcer = new();
        announcer.Observe(ConnectionStatus.Disconnected, ReceiverMode.Disconnected, isCoasting: false);

        Announcement? announcement = announcer.Observe(
            ConnectionStatus.Connected,
            ReceiverMode.Disconnected,
            isCoasting: false,
            portName: "COM3");

        Assert.NotNull(announcement);
        Assert.Equal("Connected on COM3.", announcement.Text);
    }

    /// <remarks>
    /// The receiver reaches Locked and reports no satellites in the same poll. Announcing "Locked
    /// to GPS" and stopping would tell the listener the opposite of what has happened.
    /// </remarks>
    [Fact]
    public void CoastingOutranksTheModeChangeThatCarriesIt()
    {
        StateAnnouncer announcer = new();
        announcer.Observe(ConnectionStatus.Connected, ReceiverMode.Recovering, isCoasting: false);

        Announcement? announcement =
            announcer.Observe(ConnectionStatus.Connected, ReceiverMode.Locked, isCoasting: true);

        Assert.NotNull(announcement);
        Assert.Contains("no satellites", announcement.Text, StringComparison.Ordinal);
        Assert.Equal(AnnouncementUrgency.Assertive, announcement.Urgency);
    }

    [Fact]
    public void CoastingIsAnnouncedWithoutAModeChange()
    {
        StateAnnouncer announcer = new();
        announcer.Observe(ConnectionStatus.Connected, ReceiverMode.Locked, isCoasting: false);

        Announcement? announcement =
            announcer.Observe(ConnectionStatus.Connected, ReceiverMode.Locked, isCoasting: true);

        Assert.NotNull(announcement);
        Assert.Equal(AnnouncementUrgency.Assertive, announcement.Urgency);
    }

    [Fact]
    public void CoastingIsAnnouncedOnceAndNotEverySecond()
    {
        StateAnnouncer announcer = new();
        announcer.Observe(ConnectionStatus.Connected, ReceiverMode.Locked, isCoasting: false);
        announcer.Observe(ConnectionStatus.Connected, ReceiverMode.Locked, isCoasting: true);

        Assert.Null(announcer.Observe(ConnectionStatus.Connected, ReceiverMode.Locked, isCoasting: true));
    }

    [Fact]
    public void RecoveryFromCoastingIsAnnounced()
    {
        StateAnnouncer announcer = new();
        announcer.Observe(ConnectionStatus.Connected, ReceiverMode.Locked, isCoasting: true);

        Announcement? announcement =
            announcer.Observe(ConnectionStatus.Connected, ReceiverMode.Locked, isCoasting: false);

        Assert.NotNull(announcement);
        Assert.Equal("Tracking satellites again.", announcement.Text);
        Assert.Equal(AnnouncementUrgency.Polite, announcement.Urgency);
    }

    /// <remarks>
    /// Every one of the §10.3 modes has to have something to say — a mode that announced an empty
    /// string would be silence the user could not distinguish from no change at all.
    /// </remarks>
    [Theory]
    [InlineData(ReceiverMode.Locked)]
    [InlineData(ReceiverMode.Recovering)]
    [InlineData(ReceiverMode.Waiting)]
    [InlineData(ReceiverMode.Holdover)]
    [InlineData(ReceiverMode.PowerUp)]
    [InlineData(ReceiverMode.Off)]
    public void EveryModeHasAnAnnouncement(ReceiverMode mode)
    {
        StateAnnouncer announcer = new();
        announcer.Observe(ConnectionStatus.Connected, ReceiverMode.Disconnected, isCoasting: false);

        Announcement? announcement = announcer.Observe(ConnectionStatus.Connected, mode, isCoasting: false);

        Assert.NotNull(announcement);
        Assert.False(string.IsNullOrWhiteSpace(announcement.Text));
    }
}
