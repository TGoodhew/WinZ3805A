using System.Globalization;

using WinZ3805A.Device.Models;

namespace WinZ3805A.Device.Drivers.Nmea;

/// <summary>
/// The one thing this driver is allowed to send, and what comes back (#508).
/// </summary>
/// <remarks>
/// <para>
/// <b>This family listened and never spoke, and that was worth something.</b> Until #508 there was
/// no send path at all, so a u-blox's <c>$PUBX,41</c> — which reconfigures a port and can leave a
/// receiver unreachable at the settings it was found on — was excluded by there being nowhere to
/// type it. That guarantee is now a policy instead, enforced by <c>NmeaDriver.IsBlocked</c>, and
/// this class is the whole of what the policy permits.
/// </para>
/// <para>
/// <b><c>$PUBX,04</c> is a poll and configures nothing.</b> It asks for time and clock information
/// and the receiver answers once. Nothing is written to the module's configuration, nothing
/// persists, and a receiver that does not understand it says nothing at all — which is why the
/// failure mode of asking the wrong receiver is silence rather than damage.
/// </para>
/// <para>
/// <b>It is still only asked of a receiver that has said it is u-blox</b> — see
/// <see cref="IsUnderstoodBy"/>. A MediaTek or Quectel talker is never sent it, not because it would
/// be dangerous but because sending a vendor's private sentence to another vendor's hardware is
/// guessing, and this driver has a rule against that.
/// </para>
/// </remarks>
public static class NmeaPoll
{
    /// <summary>The poll, without its checksum — also the one prefix <c>IsBlocked</c> permits.</summary>
    public const string TimePoll = "$PUBX,04";

    /// <summary>The identifier the reply parses as, its talker being the proprietary <c>P</c>.</summary>
    public const string ReplyIdentifier = "UBX";

    /// <summary>The key the reply is kept under, and the mnemonic the poll is catalogued as.</summary>
    public static string ReplyKey { get; } = NmeaSentence.KeyFor(ReplyIdentifier);

    /// <summary>
    /// The poll as it goes on the wire, checksummed.
    /// </summary>
    /// <remarks>
    /// Built rather than written down, for the reason <c>NmeaDriverTests</c> records: two of that
    /// file's first expectations were wrong because a checksum was computed by hand.
    /// </remarks>
    public static string Outgoing { get; } =
        $"{TimePoll}*{NmeaSentence.Checksum(TimePoll.AsSpan(1)):X2}";

    /// <summary>
    /// Whether the receiver that sent <paramref name="banner"/> understands <see cref="TimePoll"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Silence is the answer when nothing is known.</b> A talker that has not printed a banner —
    /// most of them, most of the time — is not asked. That is the conservative direction: the cost
    /// is a field staying dashed, and the alternative is sending a vendor sentence to hardware that
    /// never said whose it was.
    /// </para>
    /// <para>
    /// The hardware string is the evidence, not the talker prefix. <c>GN</c> and <c>GP</c> say which
    /// constellations a fix used and nothing about who made the module; <c>HW UBX-M8130</c> says it
    /// outright (#515).
    /// </para>
    /// </remarks>
    public static bool IsUnderstoodBy(TalkerBanner? banner) =>
        banner?.Hardware?.StartsWith("UBX-", StringComparison.OrdinalIgnoreCase) ?? false;

    /// <summary>
    /// Reads a <c>$PUBX,04</c> reply into the readings it carries, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on the bench VK-162, 7 Sep 2026:
    /// <c>$PUBX,04,020530.00,080926,180329.99,2435,18,-831247,-847.876,21*1A</c> — time, date, time
    /// of week, week, leap seconds, clock bias in ns, clock drift in ns/s, and the time-pulse
    /// granularity in ns.
    /// </para>
    /// <para>
    /// <b>The <c>D</c> suffix on the leap second is the whole reason that field is worth having.</b>
    /// u-blox writes <c>18D</c> when the value is the firmware's default and a bare <c>18</c> when it
    /// has been decoded from the satellites. Only the second is a measurement of what GPS is
    /// currently broadcasting; the first is a number the module was built with, and reporting it as
    /// GPS − UTC would be presenting a constant as a reading. The bench unit reported a bare 18.
    /// </para>
    /// </remarks>
    public static PollReadings? Parse(string? reply)
    {
        if (reply is null)
        {
            return null;
        }

        foreach (string line in reply.Split('\n'))
        {
            NmeaSentence? sentence = NmeaSentence.TryParse(line);
            if (sentence is null || !sentence.ChecksumValid || sentence.Identifier != "UBX" || sentence.Field(0) != "04")
            {
                continue;
            }

            return new PollReadings
            {
                LeapSeconds = DecodedLeapSecond(sentence.Field(5)),
                ClockBiasNanoseconds = Number(sentence.Field(6)),
                ClockDriftNanosecondsPerSecond = Number(sentence.Field(7)),
                TimePulseGranularityNanoseconds = Number(sentence.Field(8)),
            };
        }

        return null;
    }

    /// <summary>The leap second only when it was decoded rather than defaulted.</summary>
    private static int? DecodedLeapSecond(string? field)
    {
        string? value = field?.Trim();
        if (string.IsNullOrEmpty(value) || value.EndsWith('D') || value.EndsWith('d'))
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
            ? seconds
            : null;
    }

    private static double? Number(string? field) =>
        double.TryParse(field?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
}

/// <summary>What a <c>$PUBX,04</c> reply carries (#508).</summary>
/// <remarks>
/// Every member is nullable: a reply can be well formed and still leave a field empty, and §11.1's
/// rule is that an unreadable field is absent rather than guessed.
/// </remarks>
public sealed record PollReadings
{
    /// <summary>
    /// GPS − UTC in whole seconds, <b>only when decoded from the satellites</b>.
    /// </summary>
    /// <remarks>
    /// Null when the module reported its firmware default, which is a constant rather than a
    /// reading — see <see cref="NmeaPoll.Parse"/>.
    /// </remarks>
    public int? LeapSeconds { get; init; }

    /// <summary>The receiver's clock offset in nanoseconds.</summary>
    public double? ClockBiasNanoseconds { get; init; }

    /// <summary>How fast that offset is changing, in nanoseconds per second.</summary>
    public double? ClockDriftNanosecondsPerSecond { get; init; }

    /// <summary>Time-pulse quantisation error in nanoseconds — an uncertainty figure.</summary>
    public double? TimePulseGranularityNanoseconds { get; init; }
}
