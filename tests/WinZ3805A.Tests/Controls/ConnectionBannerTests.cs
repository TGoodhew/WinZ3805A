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

    /// <summary>A dropped link is an error, and does not borrow the informational row.</summary>
    /// <remarks>
    /// <para>
    /// <b>The distinction this whole type exists for.</b> §9.11 puts Disconnected and Connection
    /// lost in adjacent rows and says an intentional disconnect is not a fault;
    /// <c>ConnectionStatus</c> says the same in its own remarks — "collapsing the two into one 'not
    /// connected' is the shortcut that makes an app cry wolf".
    /// </para>
    /// <para>
    /// This assertion used to say these states showed <i>nothing</i>, which was right while #248 was
    /// unbuilt: an absent bar is a gap somebody notices, where a bar reading "Not connected. Choose
    /// a serial port" mid-reconnect is a lie that looks finished. Now that the row exists, the
    /// enduring property is that it is a different row — different severity, different copy,
    /// different actions — rather than that it is missing.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(ConnectionStatus.Reconnecting)]
    [InlineData(ConnectionStatus.Faulted)]
    public void ADroppedLinkIsAnErrorAndNotTheInformationalRow(ConnectionStatus status)
    {
        ConnectionBannerState banner = ConnectionBanner.For(status, "COM3", TimeSpan.FromSeconds(4));

        Assert.True(banner.IsOpen);
        Assert.True(banner.IsError);
        Assert.NotEqual(ConnectionBanner.DisconnectedMessage, banner.Message);
        Assert.NotEqual(ConnectionBanner.ChoosePortLabel, banner.ActionLabel);
        Assert.Contains("Lost the connection", banner.Message, StringComparison.Ordinal);
    }

    /// <summary>Reconnecting counts down and offers both of §9.11's actions.</summary>
    /// <remarks>
    /// §9.11's copy pattern verbatim: "Lost the connection to COM3. Retrying in 4 seconds." /
    /// <b>Retry now</b> · <b>Stop retrying</b>. The countdown is the reason the session had to start
    /// publishing its schedule — the fact of retrying was already visible, and it is the *when* that
    /// the user staring at a 30 s cap has no other way to learn.
    /// </remarks>
    [Fact]
    public void ReconnectingCountsDownAndOffersBothActions()
    {
        ConnectionBannerState banner =
            ConnectionBanner.For(ConnectionStatus.Reconnecting, "COM3", TimeSpan.FromSeconds(4));

        Assert.Equal("Lost the connection to COM3. Retrying in 4 seconds.", banner.Message);
        Assert.Equal("Retry now", banner.ActionLabel);
        Assert.Equal("Stop retrying", banner.SecondaryActionLabel);
    }

    /// <summary>The countdown reads like a person wrote it.</summary>
    /// <remarks>
    /// Rounded <i>up</i>, so "1 second" is never followed by a second of silence at zero, and so the
    /// first tick of a 4 s backoff reads "4 seconds" rather than "3". Singular at one, because
    /// "Retrying in 1 seconds" is the kind of detail that makes an interface look unfinished.
    /// </remarks>
    [Theory]
    [InlineData(4.0, "Retrying in 4 seconds.")]
    [InlineData(3.2, "Retrying in 4 seconds.")]
    [InlineData(1.0, "Retrying in 1 second.")]
    [InlineData(0.4, "Retrying in 1 second.")]
    [InlineData(30.0, "Retrying in 30 seconds.")]
    public void TheCountdownRoundsUpAndAgreesWithItself(double seconds, string expected) =>
        Assert.EndsWith(
            expected,
            ConnectionBanner.For(
                ConnectionStatus.Reconnecting, "COM3", TimeSpan.FromSeconds(seconds)).Message,
            StringComparison.Ordinal);

    /// <summary>With no schedule, the sentence says so rather than inventing a number.</summary>
    /// <remarks>
    /// Null is the attempt itself: the session clears <c>NextRetryAt</c> while it is trying, because
    /// there is no next time until this one has failed. "Retrying in 0 seconds" would be a countdown
    /// that had stopped counting.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    public void WithNoScheduleTheSentenceSaysARetryIsUnderWay(double? seconds)
    {
        TimeSpan? retryIn = seconds is double s ? TimeSpan.FromSeconds(s) : null;

        Assert.Equal(
            "Lost the connection to COM3. Retrying now.",
            ConnectionBanner.For(ConnectionStatus.Reconnecting, "COM3", retryIn).Message);
    }

    /// <summary>Faulted offers a way back but does not pretend to be counting.</summary>
    /// <remarks>
    /// Nothing is coming, so there is no countdown and no <b>Stop retrying</b> — it is already
    /// stopped. <b>Retry now</b> stays, because it is the way back for somebody who stopped and
    /// changed their mind, or whose receiver has since been switched on.
    /// </remarks>
    [Fact]
    public void FaultedOffersAWayBackWithoutACountdown()
    {
        ConnectionBannerState banner =
            ConnectionBanner.For(ConnectionStatus.Faulted, "COM3", TimeSpan.FromSeconds(4));

        Assert.True(banner.IsError);
        Assert.Equal("Lost the connection to COM3. Not retrying.", banner.Message);
        Assert.Equal("Retry now", banner.ActionLabel);
        Assert.Null(banner.SecondaryActionLabel);
    }

    /// <summary>Without a port name the copy still reads as a sentence.</summary>
    /// <remarks>
    /// Reachable: the session can drop before <c>PortName</c> is set. "Lost the connection to ."
    /// is worse than a slightly vaguer sentence.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutAPortNameTheCopyStillReads(string? portName) =>
        Assert.StartsWith(
            "Lost the connection to the receiver.",
            ConnectionBanner.For(ConnectionStatus.Reconnecting, portName, TimeSpan.FromSeconds(4)).Message,
            StringComparison.Ordinal);

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
