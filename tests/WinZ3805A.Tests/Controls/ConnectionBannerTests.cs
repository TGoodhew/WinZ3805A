using WinZ3805A.Controls;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Controls;

/// <summary>§9.11's Details-window banner, and the distinction it exists to keep (#252).</summary>
public class ConnectionBannerTests
{
    /// <summary>A disconnect the user asked for is informational, and offers the way back.</summary>
    /// <remarks>
    /// §9.11's copy: "Not connected. Choose a serial port to connect." / <b>Choose a port</b>. The
    /// main window already shows the medallion for this state; the banner is what the Details window
    /// gets instead, because a page of dashes with no explanation is not a state anyone can act on.
    /// </remarks>
    [Fact]
    public void ADisconnectTheUserAskedForIsInformationalAndOffersAWayBack()
    {
        ConnectionBannerState banner = ConnectionBanner.For(ConnectionStatus.Disconnected);

        Assert.True(banner.IsOpen);
        Assert.False(banner.IsError);
        Assert.Equal("Not connected. Choose a serial port to connect.", banner.Message);
        Assert.Equal("Choose a port", banner.ActionLabel);
    }

    /// <summary>A healthy link says nothing at all.</summary>
    [Fact]
    public void AConnectedSessionShowsNoBanner() =>
        Assert.False(ConnectionBanner.For(ConnectionStatus.Connected).IsOpen);

    /// <summary>Connecting says nothing either, deliberately.</summary>
    /// <remarks>
    /// It is a transient the user asked for and it resolves in a couple of seconds. §9.11 gives it
    /// no row, and a bar that appears and vanishes on its own is noise rather than information.
    /// </remarks>
    [Fact]
    public void ConnectingShowsNoBanner() =>
        Assert.False(ConnectionBanner.For(ConnectionStatus.Connecting).IsOpen);

    /// <summary>A dropped link does not borrow the informational treatment.</summary>
    /// <remarks>
    /// <para>
    /// <b>The distinction this whole type exists for.</b> §9.11 puts Disconnected and Connection
    /// lost in adjacent rows and says an intentional disconnect is not a fault; <c>ConnectionStatus</c>
    /// says the same in its own remarks — "collapsing the two into one 'not connected' is the
    /// shortcut that makes an app cry wolf".
    /// </para>
    /// <para>
    /// So until #248 builds the error row properly — with the §7.2 countdown and both <b>Retry
    /// now</b> and <b>Stop retrying</b> — these states show nothing rather than showing the wrong
    /// thing. An absent bar is a gap somebody will notice; a bar reading "Not connected. Choose a
    /// serial port" while the app is mid-reconnect is a lie that looks finished.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(ConnectionStatus.Reconnecting)]
    [InlineData(ConnectionStatus.Faulted)]
    public void ADroppedLinkDoesNotBorrowTheInformationalRow(ConnectionStatus status)
    {
        ConnectionBannerState banner = ConnectionBanner.For(status);

        Assert.False(banner.IsOpen);
        Assert.NotEqual(ConnectionBanner.DisconnectedMessage, banner.Message);
        Assert.Null(banner.ActionLabel);
    }

    /// <summary>Every state is decided, so a new one cannot fall through to a wrong row.</summary>
    /// <remarks>
    /// Enumerated rather than listed by hand: adding a sixth <c>ConnectionStatus</c> should make
    /// somebody choose its row, and this is what asks the question. The assertion is weak on purpose
    /// — it says only that the call is total and returns something coherent — because what each new
    /// state should say is a §9.11 decision, not one a test can make.
    /// </remarks>
    [Fact]
    public void EveryConnectionStateIsDecided()
    {
        foreach (ConnectionStatus status in Enum.GetValues<ConnectionStatus>())
        {
            ConnectionBannerState banner = ConnectionBanner.For(status);

            if (banner.IsOpen)
            {
                Assert.False(string.IsNullOrWhiteSpace(banner.Message));
            }
            else
            {
                Assert.Null(banner.ActionLabel);
            }
        }
    }

    /// <summary>The copy is §9.11's, not an invention.</summary>
    [Fact]
    public void TheCopyIsTheOneTheSpecificationGives()
    {
        Assert.Equal("Not connected. Choose a serial port to connect.", ConnectionBanner.DisconnectedMessage);
        Assert.Equal("Choose a port", ConnectionBanner.ChoosePortLabel);
    }
}
