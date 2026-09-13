using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Drivers.Nmea;
using WinZ3805A.Device.Models;

namespace WinZ3805A.Tests.Nmea;

/// <summary>
/// What this family may send now that it can send anything at all (#508).
/// </summary>
/// <remarks>
/// <para>
/// <b>The protection used to be structural and is now a policy, which is a real loss of safety
/// unless the policy is tested.</b> Before #508 there was no send path, so a u-blox's
/// <c>$PUBX,41</c> — which reconfigures a port and can leave a receiver unreachable at the settings
/// it was found on — was excluded by there being nowhere to type it. Now it is excluded by a rule,
/// and a rule that is not tested is a comment.
/// </para>
/// <para>
/// The rule is deliberately <b>refuse by prefix, permit one exception</b>, rather than naming the
/// dangerous sentences. The dangerous ones are not knowable: <c>$PMTK</c> and <c>$PSRF</c> are whole
/// vendor languages nobody here has read, and a list of what to refuse would be a list of what
/// somebody happened to think of.
/// </para>
/// </remarks>
public sealed class NmeaPollPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 2, 0, 0, TimeSpan.Zero);

    private static IReceiverDriver Driver() => new NmeaDriver(new FakeTimeProvider(Now));

    /// <summary>The one thing it may send.</summary>
    [Fact]
    public void TheTimePollIsPermitted()
    {
        Assert.False(Driver().IsBlocked("$PUBX,04"));
        Assert.False(Driver().IsBlocked("$PUBX,04*37"));
    }

    /// <summary>
    /// Everything else proprietary is refused, including the rest of the same vendor's language.
    /// </summary>
    /// <remarks>
    /// <c>$PUBX,41</c> is the one that matters most: it sets a port's baud rate and protocols, and
    /// getting it wrong leaves the receiver unreachable by the settings auto-detect found it on.
    /// </remarks>
    [Theory]
    [InlineData("$PUBX,41")]
    [InlineData("$PUBX,40")]
    [InlineData("$PUBX,00")]
    [InlineData("$PUBX,03")]
    [InlineData("$PMTK251")]
    [InlineData("$PSRF100")]
    [InlineData("$PQTMCFG")]
    [InlineData("$PGRMC")]
    public void EveryOtherProprietarySentenceIsRefused(string header) =>
        Assert.True(Driver().IsBlocked(header));

    /// <summary>
    /// A near miss is still refused. <c>$PUBX,040</c> is not <c>$PUBX,04</c>, and a rule that
    /// matched loosely would permit a vendor sentence nobody has read.
    /// </summary>
    [Theory]
    [InlineData("$PUBX,0")]
    [InlineData("$PUBX")]
    [InlineData("$PUB")]
    public void ATruncatedOrPartialPollIsRefused(string header) =>
        Assert.True(Driver().IsBlocked(header));

    /// <summary>
    /// Standard sentences are not this predicate's business: they are broadcast, nothing sends one,
    /// and the console offers only the catalog.
    /// </summary>
    [Theory]
    [InlineData("$GPRMC")]
    [InlineData("$GNGGA")]
    [InlineData("$GPTXT")]
    [InlineData("")]
    [InlineData(null)]
    public void StandardSentencesAndNothingAreNotRefused(string? header) =>
        Assert.False(Driver().IsBlocked(header));

    // -------------------------------------------------------------------------------------
    // Who it may be sent to
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Only a receiver that has said it is u-blox. Silence is the answer when nothing is known —
    /// the cost is a dashed field, and the alternative is guessing whose hardware this is.
    /// </summary>
    [Fact]
    public void OnlyAReceiverThatSaidItIsUbloxIsAsked()
    {
        Assert.True(NmeaPoll.IsUnderstoodBy(new TalkerBanner { Hardware = "UBX-M8130" }));
        Assert.True(NmeaPoll.IsUnderstoodBy(new TalkerBanner { Hardware = "UBX-G70xx" }));

        Assert.False(NmeaPoll.IsUnderstoodBy(new TalkerBanner { Hardware = "MT3339" }));
        Assert.False(NmeaPoll.IsUnderstoodBy(new TalkerBanner { Firmware = "SPG 3.01" }));
        Assert.False(NmeaPoll.IsUnderstoodBy(TalkerBanner.None));
        Assert.False(NmeaPoll.IsUnderstoodBy(null));
    }

    // -------------------------------------------------------------------------------------
    // The reply
    // -------------------------------------------------------------------------------------

    private static string Sentence(string body)
    {
        byte checksum = 0;
        foreach (char c in body)
        {
            checksum ^= (byte)c;
        }

        return $"${body}*{checksum:X2}";
    }

    /// <summary>The bench VK-162's own reply, 7 Sep 2026.</summary>
    [Fact]
    public void TheBenchReplyIsReadInFull()
    {
        PollReadings? readings = NmeaPoll.Parse(
            Sentence("PUBX,04,020530.00,080926,180329.99,2435,18,-831247,-847.876,21"));

        Assert.NotNull(readings);
        Assert.Equal(18, readings.LeapSeconds);
        Assert.Equal(-831247, readings.ClockBiasNanoseconds);
        Assert.Equal(-847.876, readings.ClockDriftNanosecondsPerSecond);
        Assert.Equal(21, readings.TimePulseGranularityNanoseconds);
    }

    /// <summary>
    /// <b>The D suffix is the whole reason the leap second is worth reporting.</b> u-blox writes
    /// <c>18D</c> for its firmware default and a bare <c>18</c> for a value decoded from the
    /// satellites. Only the second is a measurement; reporting the first as GPS − UTC would present
    /// a constant the module was built with as a reading of what GPS is broadcasting now.
    /// </summary>
    [Fact]
    public void AFirmwareDefaultLeapSecondIsNotReportedAsAReading()
    {
        PollReadings? readings = NmeaPoll.Parse(
            Sentence("PUBX,04,020530.00,080926,180329.99,2435,18D,-831247,-847.876,21"));

        Assert.NotNull(readings);
        Assert.Null(readings.LeapSeconds);

        // The rest of the reply is still good — only the leap second is a default.
        Assert.Equal(-831247, readings.ClockBiasNanoseconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a sentence")]
    [InlineData("$GPRMC,020530.00,A,4731.31483,N,12212.36635,W,0.02,,080926,,,A*61")]
    public void AnythingThatIsNotTheReplyReadsAsNothing(string reply) =>
        Assert.Null(NmeaPoll.Parse(reply));

    /// <summary>A corrupted reply is refused rather than half read.</summary>
    [Fact]
    public void AReplyWithABadChecksumIsRefused() =>
        Assert.Null(NmeaPoll.Parse("$PUBX,04,020530.00,080926,180329.99,2435,18,-831247,-847.876,21*FF"));

    /// <summary>A different PUBX message is not this one, even well formed.</summary>
    [Fact]
    public void ADifferentPubxMessageIsNotRead() =>
        Assert.Null(NmeaPoll.Parse(Sentence("PUBX,00,081350.00,4717.113210,N,00833.915187,E,546.589,G3")));
}
