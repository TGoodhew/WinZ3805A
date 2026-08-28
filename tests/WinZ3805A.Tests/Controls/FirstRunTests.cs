using WinZ3805A.Controls;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Controls;

/// <summary>§9.11's first-run surface, and the one decision the row does not make (#253).</summary>
public class FirstRunTests
{
    /// <summary>A machine that has never had a port chosen gets the introduction.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AMachineWithNoStoredPortGetsTheIntroduction(string? stored) =>
        Assert.True(FirstRun.ShouldShow(stored, ConnectionStatus.Disconnected));

    /// <summary>Once a port has been chosen it is never a first run again.</summary>
    /// <remarks>
    /// <para>
    /// <b>The decision §9.11 leaves open, and the reason it goes this way.</b> The two adjacent rows
    /// are written for different readers: first run explains what the application <i>is</i>;
    /// Disconnected says "Not connected. Choose a serial port to connect." and assumes you know.
    /// </para>
    /// <para>
    /// So the likeliest first run of all — somebody who opens the app before plugging the adapter
    /// in, picks a port and fails to connect — lands on Disconnected. They have a connection
    /// problem, not a comprehension problem. Under the alternative rule, "first run until a
    /// connection succeeds", that user would be told what the application is over and over while
    /// trying to fix a cable.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(ConnectionStatus.Disconnected)]
    [InlineData(ConnectionStatus.Connecting)]
    [InlineData(ConnectionStatus.Connected)]
    [InlineData(ConnectionStatus.Reconnecting)]
    [InlineData(ConnectionStatus.Faulted)]
    public void AChosenPortEndsFirstRunWhateverHappensNext(ConnectionStatus status) =>
        Assert.False(FirstRun.ShouldShow("COM3", status));

    /// <summary>It never covers a live session.</summary>
    /// <remarks>
    /// Not reachable through the ordinary path — <c>ConnectOnLaunchAsync</c> returns early with no
    /// stored port, so nothing auto-connects on a first run — but a port can be chosen and connected
    /// within a session before preferences are written, and a full-page takeover over a working
    /// receiver is worth one extra condition to rule out.
    /// </remarks>
    [Theory]
    [InlineData(ConnectionStatus.Connecting)]
    [InlineData(ConnectionStatus.Connected)]
    [InlineData(ConnectionStatus.Reconnecting)]
    [InlineData(ConnectionStatus.Faulted)]
    public void ItNeverCoversALiveSession(ConnectionStatus status) =>
        Assert.False(FirstRun.ShouldShow(null, status));

    /// <summary>The copy is §9.11's, to the word.</summary>
    /// <remarks>
    /// Asserted rather than eyeballed because the row is prescriptive about it, and because the body
    /// line is the only place in the application that says what it talks to — a reader who has never
    /// seen a Z3805A learns from this sentence or from nothing.
    /// </remarks>
    [Fact]
    public void TheCopyIsTheOneTheSpecificationGives()
    {
        Assert.Equal("Connect your receiver", FirstRun.Headline);
        Assert.Equal(
            "This app talks to HP and Symmetricom GPS receivers over a serial port. "
            + "Pick the port your receiver is on to begin.",
            FirstRun.Body);
        Assert.Equal("Choose a port", FirstRun.ActionLabel);
    }

    /// <summary>The body names both makes the application actually supports.</summary>
    /// <remarks>
    /// §6.2 and §8.6 have it serving the SmartClock family across two badges, and somebody with a
    /// 58503A needs to recognise their instrument in the one sentence that describes the product.
    /// </remarks>
    [Fact]
    public void TheBodyNamesBothMakes()
    {
        Assert.Contains("HP", FirstRun.Body, StringComparison.Ordinal);
        Assert.Contains("Symmetricom", FirstRun.Body, StringComparison.Ordinal);
    }
}
