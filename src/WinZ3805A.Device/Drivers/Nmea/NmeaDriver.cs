using System.Globalization;

using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Models;
using WinZ3805A.Device.Transport;

namespace WinZ3805A.Device.Drivers.Nmea;

/// <summary>
/// A driver for any NMEA 0183 GNSS talker — a u-blox module, the GPS half of a BG7TBL, a marine
/// receiver — and the worked example of adding a receiver family (#310).
/// </summary>
/// <remarks>
/// <para>
/// <b>Read it beside <c>docs/tutorial-nmea-driver.md</c></b>, which walks the eleven steps of
/// <c>docs/adding-a-receiver.md</c> with this file as the result. This folder is the whole
/// driver: the sentence codec, the cycle parser and this class. Nothing in it depends on the
/// simulator under <c>tools/</c>; the simulator depends on the codec.
/// </para>
/// <para>
/// <b>The family is the opposite shape to the SmartClock</b>, which is why it was chosen. A talker
/// speaks unprompted, once a second, and is never written to; it has no status screen, no error
/// queue and no commands. Everything the seam assumed about a receiver that answers questions had
/// to be made explicit for one that does not: <see cref="Link"/> says which kind this is,
/// <see cref="Overhear"/> recognises it by what it says, and <see cref="ClassifyLine"/> tells the
/// session's listener which sentence answers which plan entry.
/// </para>
/// <para>
/// <b>Two mappings are judgement calls, stated here so they can be argued with.</b> The sync state
/// handed to the fast sweep uses the common vocabulary the primary window already renders —
/// <c>LOCK</c> for a fix, <c>POW</c> for none — because a receiver with a GPS fix is locked to GPS
/// in the only sense a GPS receiver has, and one without is where a receiver is at power-up. And
/// the satellite count is the satellites being <i>tracked</i> (GSV entries with a signal), not the
/// number used in the fix (GGA's count), because tracked is what the SmartClock reports and what
/// the readout says. Neither invents a figure the receiver did not give; both choose which of its
/// words to say in the application's.
/// </para>
/// </remarks>
public sealed class NmeaDriver(TimeProvider timeProvider) : IReceiverDriver
{
    /// <summary>The family name, which is also the manufacturer field of every identity this driver claims.</summary>
    public const string FamilyName = "NMEA 0183";

    /// <summary>The sync token for a receiver with a fix, in the common vocabulary.</summary>
    public const string FixToken = "LOCK";

    /// <summary>The sync token for a receiver without one.</summary>
    public const string NoFixToken = "POW";

    /// <summary>Three missed cycles of a 1 Hz talker is a talker that has stopped.</summary>
    private static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The talkers this driver claims: GPS, GLONASS, Galileo, BeiDou, QZSS, NavIC and the combined <c>GN</c>.</summary>
    private static readonly IReadOnlySet<string> GnssTalkers =
        new HashSet<string>(StringComparer.Ordinal) { "GP", "GN", "GL", "GA", "GB", "BD", "QZ", "GI" };

    /// <summary>The sentences the driver reads. Anything else a talker sends is heard and discarded.</summary>
    private static readonly IReadOnlySet<string> Sentences =
        new HashSet<string>(StringComparer.Ordinal) { "RMC", "GGA", "GSA", "GSV", "ZDA", "GLL", "VTG" };

    private static readonly string[] FastTierOrder =
    [
        NmeaSentence.KeyFor("RMC"),
        NmeaSentence.KeyFor("GGA"),
        NmeaSentence.KeyFor("GSA"),
        NmeaSentence.KeyFor("GSV"),
    ];

    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <inheritdoc />
    public string Family => FamilyName;

    /// <inheritdoc />
    public LinkStyle Link => LinkStyle.Broadcast;

    /// <summary>
    /// The allowlist, which for a talker is reads only: one entry per sentence the driver
    /// understands, and the whole cycle. There is nothing here that can be sent.
    /// </summary>
    public IReadOnlyList<ScpiCommand> Commands { get; } =
    [
        Read("RMC", "Recommended minimum", "Time, date, status, position, speed and track — the one sentence every talker sends, and this driver's cycle boundary."),
        Read("GGA", "Fix data", "Time, position, fix quality, satellites used, dilution of precision and altitude."),
        Read("GSA", "Active satellites", "Fix mode (none, 2D, 3D), the satellites used in the fix and the dilution of precision."),
        Read("GSV", "Satellites in view", "Every satellite in view with its elevation, azimuth and signal-to-noise ratio, across several pages."),
        Read("ZDA", "Time and date", "UTC time and the full date, with the local zone offset."),
        Read("GLL", "Position", "Latitude and longitude with the time of the fix."),
        Read("VTG", "Track and speed", "Track made good and ground speed."),
        new(
            Mnemonic: PollPlan.WholeCycle,
            ShortForm: PollPlan.WholeCycle,
            Tier: SafetyTier.Safe,
            IsQuery: true,
            DisplayName: "Whole cycle",
            Description: "Every sentence of the last complete cycle, which is what the status parser reads.",
            Parameters: [],
            ResponseFormat: ResponseFormat.MultiLine),
    ];

    /// <summary>Once a second for the readings, every five for the whole cycle — the talker's own rate and a multiple of it.</summary>
    public PollCadence Cadence { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

    /// <summary>The standard's 4800, then the 9600 most modules actually ship at, then the high-speed 38400.</summary>
    public IReadOnlyList<SerialSettings> AutoDetectSequence { get; } =
    [
        new() { BaudRate = 4800, DataBits = 8, Parity = System.IO.Ports.Parity.None, StopBits = System.IO.Ports.StopBits.One },
        new() { BaudRate = 9600, DataBits = 8, Parity = System.IO.Ports.Parity.None, StopBits = System.IO.Ports.StopBits.One },
        new() { BaudRate = 38400, DataBits = 8, Parity = System.IO.Ports.Parity.None, StopBits = System.IO.Ports.StopBits.One },
    ];

    /// <summary>RMC first, because it is the cycle boundary; then the sentences the readings come from; the whole cycle for the parser.</summary>
    public PollPlan Plan { get; } = new(FastTierOrder, RefusableIndex: null, FullStatus: PollPlan.WholeCycle);

    /// <inheritdoc />
    public bool Recognises(DeviceIdentity? identity) =>
        identity is not null && string.Equals(identity.Manufacturer, FamilyName, StringComparison.Ordinal);

    /// <summary>
    /// Claims the receiver when a sentence with a valid checksum from a GNSS talker is among the
    /// lines heard. One is enough: a checksum that matches is not noise, and a wrong baud rate
    /// never produces one.
    /// </summary>
    public DeviceIdentity? Overhear(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        foreach (string line in lines)
        {
            NmeaSentence? sentence = NmeaSentence.TryParse(line);
            if (sentence is not null && sentence.ChecksumValid && GnssTalkers.Contains(sentence.Talker) && Sentences.Contains(sentence.Identifier))
            {
                return IdentityFor(sentence.Talker);
            }
        }

        return null;
    }

    /// <summary>The identity this driver reports for a talker — the family as manufacturer, the talker as model.</summary>
    public static DeviceIdentity IdentityFor(string talker) =>
        new(FamilyName, $"{talker} talker", string.Empty, string.Empty, ReceiverModel.Unknown);

    /// <inheritdoc />
    public string? ClassifyLine(string line)
    {
        NmeaSentence? sentence = NmeaSentence.TryParse(line);
        return sentence is not null && sentence.ChecksumValid && GnssTalkers.Contains(sentence.Talker) && Sentences.Contains(sentence.Identifier)
            ? sentence.Key
            : null;
    }

    /// <inheritdoc />
    public ScpiCommand? Find(string? mnemonic)
    {
        string? wanted = mnemonic?.Trim();
        return string.IsNullOrEmpty(wanted)
            ? null
            : Commands.FirstOrDefault(command => string.Equals(command.Mnemonic, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Nothing is excluded because nothing can be sent: the catalog holds reads only and the
    /// console offers only the catalog. A talker with proprietary configuration sentences — a
    /// u-blox's <c>$PUBX</c>, a MediaTek's <c>$PMTK</c> — is protected by their absence, which
    /// is §8.4's rule applied to a family that has no setters to exclude.
    /// </summary>
    public bool IsBlocked(string? header) => false;

    /// <inheritdoc />
    public TimeSpan TimeoutFor(string? mnemonic) => SilenceTimeout;

    /// <inheritdoc />
    public ReceiverStatus Parse(string? response) => NmeaStatusParser.Parse(response, _timeProvider.GetUtcNow());

    /// <summary>
    /// Reads the fast tier: RMC for the cycle and its status, GGA for the fix quality, GSA for the
    /// mode, GSV for the satellites being tracked.
    /// </summary>
    /// <remarks>
    /// A sweep whose first answer is not an RMC sentence is not a reading from a talker this driver
    /// understands — the same rule as the SmartClock's sync-state discriminator — and is rejected
    /// with what was seen.
    /// </remarks>
    public SweepInterpretation InterpretSweep(IReadOnlyList<string?> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);

        NmeaSentence? rmc = LastSentence(At(answers, 0), "RMC");
        NmeaSentence? gga = LastSentence(At(answers, 1), "GGA");
        int tracked = TrackedIn(At(answers, 3));

        if (rmc is null)
        {
            return new SweepInterpretation(
                new FastReadings(null, null, null, null, null, tracked > 0 ? tracked : null),
                $"the cycle boundary read \"{Summarise(At(answers, 0))}\", which is not an RMC sentence");
        }

        int quality = int.TryParse(gga?.Field(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out int q) ? q : (rmc.Field(1) == "A" ? 1 : 0);

        FastReadings readings = new(
            SyncState: quality > 0 ? FixToken : NoFixToken,
            Tfom: null,
            Ffom: null,
            TimeIntervalNanoseconds: null,
            EfcPercent: null,
            SatellitesTracked: tracked);

        return new SweepInterpretation(readings, Rejection: null);
    }

    private static ScpiCommand Read(string identifier, string displayName, string description) => new(
        Mnemonic: NmeaSentence.KeyFor(identifier),
        ShortForm: NmeaSentence.KeyFor(identifier),
        Tier: SafetyTier.Safe,
        IsQuery: true,
        DisplayName: displayName,
        Description: description,
        Parameters: [],
        ResponseFormat: identifier == "GSV" ? ResponseFormat.MultiLine : ResponseFormat.Text);

    private static string? At(IReadOnlyList<string?> answers, int index) =>
        index < answers.Count ? answers[index] : null;

    /// <summary>The last valid sentence of a kind in an answer, which may hold several lines.</summary>
    private static NmeaSentence? LastSentence(string? answer, string identifier)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return null;
        }

        NmeaSentence? found = null;
        foreach (string line in answer.Split('\n'))
        {
            NmeaSentence? sentence = NmeaSentence.TryParse(line);
            if (sentence is not null && sentence.ChecksumValid && sentence.Identifier == identifier)
            {
                found = sentence;
            }
        }

        return found;
    }

    /// <summary>Satellites with a signal across the GSV pages in an answer, each PRN counted once.</summary>
    private static int TrackedIn(string? gsvPages)
    {
        if (string.IsNullOrWhiteSpace(gsvPages))
        {
            return 0;
        }

        HashSet<int> tracked = [];
        foreach (string line in gsvPages.Split('\n'))
        {
            NmeaSentence? page = NmeaSentence.TryParse(line);
            if (page is null || !page.ChecksumValid || page.Identifier != "GSV")
            {
                continue;
            }

            for (int index = 3; index + 3 < page.Fields.Count; index += 4)
            {
                if (int.TryParse(page.Field(index), NumberStyles.Integer, CultureInfo.InvariantCulture, out int prn)
                    && int.TryParse(page.Field(index + 3), NumberStyles.Integer, CultureInfo.InvariantCulture, out int snr)
                    && snr > 0)
                {
                    tracked.Add(prn);
                }
            }
        }

        return tracked.Count;
    }

    private static string Summarise(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return "(empty)";
        }

        string oneLine = answer.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= 60 ? oneLine : oneLine[..60] + "…";
    }
}
