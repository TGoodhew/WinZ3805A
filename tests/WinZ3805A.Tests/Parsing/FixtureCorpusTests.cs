using System.Text;

using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Models;
using WinZ3805A.Device.Parsing;

namespace WinZ3805A.Tests.Parsing;

/// <summary>
/// Every captured screen, held against what must be true of all of them (§11.1, P0-4, #4).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StatusScreenParserTests"/> asserts what one screen says, value by value, cross-checked
/// against the scalar queries taken in the same session. Those assertions cannot be written until
/// someone has read the capture. <b>These can be written before it exists</b>, which is the point:
/// #185's sitting produces four states in one afternoon — power-up, acquiring, holdover, a failing
/// health monitor — and each is only capturable while the hardware is being moved. A fixture that is
/// covered the moment it lands is worth more than one waiting for someone to find an afternoon.
/// </para>
/// <para>
/// <b>What can be asserted without knowing the state</b> is narrower than it looks, and §11.1 is why:
/// the parser never throws and an unparseable field becomes <see langword="null"/>, so demanding that
/// any particular value be present would be asserting the opposite of the requirement. What is left
/// is real all the same — a PRN outside 1–32, an elevation past the zenith, or a tracked count that
/// disagrees with the receiver's own <c>Tracking:</c> line are all wrong on any screen, in any state,
/// and all three are what column detection gets wrong first on a table it has not seen.
/// </para>
/// <para>
/// The corpus is both the promoted fixtures and anything under <c>captured/</c>, so a screen is
/// covered from the moment the harness writes it rather than from the moment it is promoted.
/// </para>
/// </remarks>
public class FixtureCorpusTests
{
    /// <summary>
    /// An instant to pin the clock at. Arbitrary, and it has to be: these screens have not been
    /// captured yet, so nothing here may depend on when they were.
    /// </summary>
    /// <remarks>
    /// The §7.4 rollover correction is a function of this, so a test that asserted a corrected date
    /// would be asserting this constant. None does — the rollover is <see cref="StatusScreenParserTests"/>'s
    /// business, against a screen whose capture instant is known.
    /// </remarks>
    private static readonly DateTimeOffset Whenever = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Corpus
    {
        get
        {
            TheoryData<string> data = [];

            foreach (string path in FixturePaths())
            {
                data.Add(Path.GetRelativePath(FixtureRoot, path).Replace('\\', '/'));
            }

            return data;
        }
    }

    private static string FixtureRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    // -------------------------------------------------------------------------------------
    // The corpus itself
    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// A theory over an empty set passes silently, which would make every assertion below a
    /// decoration. #181's rule: a check has to be run against something.
    /// </remarks>
    [Fact]
    public void ThereIsACorpusToCheck() =>
        Assert.NotEmpty(FixturePaths());

    /// <summary>Every file the corpus collects is actually a captured screen.</summary>
    /// <remarks>
    /// <para>
    /// <b>The assertions below cannot fail on a file that is not a screen</b>, which makes this the
    /// one that has to notice. §11.1 requires the parser never to throw and unparseable fields to
    /// become <see langword="null"/>, so an arbitrary text file parses to nulls and then satisfies
    /// every PRN, angle, count and health check vacuously — a corpus of junk passes, and reports
    /// itself as covered.
    /// </para>
    /// <para>
    /// This is not hypothetical. <c>Capture-Fixtures.ps1</c> wrote its <c>capture-log.txt</c> into
    /// the very directory it fills with fixtures, and the corpus globs <c>*.txt</c> through every
    /// subdirectory — so the log was collected as a screen and passed. Caught on 27 Aug by
    /// dry-running the harness against the receiver before the sitting; the harness now writes
    /// <c>capture-log.md</c> (#221 — <c>.log</c> was tried first and is gitignored, so the
    /// provenance never reached the repository), and this makes the next such file fail loudly
    /// rather than pad the count.
    /// </para>
    /// <para>
    /// The bar is deliberately low. A screen is at least a few hundred bytes and states the mode
    /// the receiver is in, and both are true of every state §11.1 describes — including the four
    /// #185 exists to capture, of which power-up, acquiring and holdover have since been seen and
    /// only the health-monitor failure has not. Anything narrower would be asserting what a screen
    /// says, which is <see cref="StatusScreenParserTests"/>'s business against a capture someone
    /// has read.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void EveryFixtureLooksLikeAStatusScreen(string name)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(FixtureRoot, name));

        Assert.True(
            bytes.Length >= 200,
            $"{name} is {bytes.Length} bytes. A status screen is not that short, so this is not one.");

        string text = Encoding.Latin1.GetString(bytes);

        Assert.True(
            text.Contains("SmartClock Mode", StringComparison.Ordinal),
            $"{name} has no 'SmartClock Mode' line, so it is not a captured status screen. "
                + "If the harness put it here, it belongs outside the corpus.");
    }

    /// <remarks>
    /// <b>The exact bytes are the point.</b> The parser derives satellite columns from the position
    /// of tokens in the header row, so a fixture whose line endings were converted on the way into
    /// the repository is not the screen the receiver printed. <c>.gitattributes</c> marks the folder
    /// <c>-text</c> to prevent it; this is what notices if that ever stops working.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void EveryFixtureKeepsItsCarriageReturns(string name)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(FixtureRoot, name));

        int bare = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n' && (i == 0 || bytes[i - 1] != (byte)'\r'))
            {
                bare++;
            }
        }

        Assert.True(bare == 0, $"{name} has {bare} line feed(s) with no carriage return before them.");
        Assert.Contains((byte)'\r', bytes);
    }

    // -------------------------------------------------------------------------------------
    // What must hold of any screen, in any state
    // -------------------------------------------------------------------------------------

    /// <summary>§11.1's first rule, against real device output rather than hand-written strings.</summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void TheParserNeverThrows(string name)
    {
        ReceiverStatus status = Parse(name);

        Assert.NotNull(status);
    }

    /// <remarks>
    /// Truncation is the failure a move produces: the receiver loses power part-way through a screen
    /// and the harness writes what arrived. Every prefix of a real screen has to parse as calmly as
    /// the whole one — that is what "never throws" is worth having for.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void EveryPrefixOfEveryFixtureAlsoParses(string name)
    {
        string[] lines = ReadLines(name);

        for (int take = 0; take < lines.Length; take++)
        {
            ReceiverStatus status = ParserAt().Parse(string.Join("\r\n", lines[..take]));
            Assert.NotNull(status);
        }
    }

    /// <summary>A PRN is one of the 32 GPS slots, whatever column it was read out of.</summary>
    /// <remarks>
    /// The first thing column detection gets wrong on an unfamiliar table is the column boundary,
    /// and the symptom is a PRN carrying a digit from its neighbour — 118 rather than 18, or 25
    /// rather than 2 with a 5 lost off the elevation. A range check catches that without anyone
    /// knowing what the screen was supposed to say.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void EveryPrnIsAGpsSlot(string name)
    {
        ReceiverStatus status = Parse(name);

        foreach (int prn in status.Tracked.Select(s => s.Prn).Concat(status.NotTracked.Select(s => s.Prn)))
        {
            Assert.InRange(prn, 1, 32);
        }
    }

    /// <summary>Nothing is above the zenith or off the compass.</summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void EveryAngleIsOnTheSky(string name)
    {
        ReceiverStatus status = Parse(name);

        foreach ((int? elevation, int? azimuth) in
            status.Tracked.Select(s => (s.ElevationDegrees, s.AzimuthDegrees))
                .Concat(status.NotTracked.Select(s => (s.ElevationDegrees, s.AzimuthDegrees))))
        {
            // Null is allowed and specified: §11.1 turns an unparseable column into null rather
            // than into a guess. What is not allowed is a number that cannot be a direction.
            if (elevation is int e)
            {
                Assert.InRange(e, -90, 90);
            }

            if (azimuth is int a)
            {
                Assert.InRange(a, 0, 360);
            }
        }
    }

    /// <summary>No satellite is in both tables at once.</summary>
    /// <remarks>
    /// The screen puts tracked and not-tracked in two side-by-side column groups, so a group
    /// boundary read one column wide would put the same PRN in both. It cannot be in both: it is
    /// either being tracked or it is not.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void NoSatelliteIsBothTrackedAndNotTracked(string name)
    {
        ReceiverStatus status = Parse(name);

        int[] overlap = status.Tracked.Select(s => s.Prn)
            .Intersect(status.NotTracked.Select(s => s.Prn))
            .ToArray();

        Assert.True(overlap.Length == 0, $"{name}: PRN(s) {string.Join(", ", overlap)} are in both tables.");
    }

    /// <summary>And none appears twice in the same table.</summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void NoSatelliteIsListedTwice(string name)
    {
        ReceiverStatus status = Parse(name);

        Assert.Equal(status.Tracked.Count, status.Tracked.Select(s => s.Prn).Distinct().Count());
        Assert.Equal(status.NotTracked.Count, status.NotTracked.Select(s => s.Prn).Distinct().Count());
    }

    /// <remarks>
    /// The receiver states its own counts on the <c>Tracking:</c> line, so the parsed tables can be
    /// held against the screen's own arithmetic rather than against an expectation. This is the
    /// single most useful invariant for a state nobody has read: a power-up screen with no
    /// satellites and an acquiring one with a part-filled table both get checked against what the
    /// receiver said about itself.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void TheTablesMatchTheCountsTheScreenStates(string name)
    {
        string text = string.Join("\r\n", ReadLines(name));
        ReceiverStatus status = Parse(name);

        System.Text.RegularExpressions.Match tracking =
            System.Text.RegularExpressions.Regex.Match(text, @"Tracking:\s*(\d+)");
        System.Text.RegularExpressions.Match notTracking =
            System.Text.RegularExpressions.Regex.Match(text, @"Not Tracking:\s*(\d+)");

        if (tracking.Success)
        {
            Assert.Equal(int.Parse(tracking.Groups[1].Value), status.Tracked.Count);
        }

        if (notTracking.Success)
        {
            Assert.Equal(int.Parse(notTracking.Groups[1].Value), status.NotTracked.Count);
        }
    }

    /// <remarks>
    /// §11.1 keeps the raw advisory text beside the decoded enum (#81), so a screen whose health
    /// line says something other than OK must not come back reporting health is fine. This is the
    /// one that matters for the state #4 calls "health failure", which is opportunistic and may be
    /// the only capture of it anyone gets.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void HealthAgreesWithTheHealthLine(string name)
    {
        string text = string.Join("\r\n", ReadLines(name));
        ReceiverStatus status = Parse(name);

        System.Text.RegularExpressions.Match health =
            System.Text.RegularExpressions.Regex.Match(text, @"HEALTH MONITOR[\. ]*\[(?<v>[^\]]*)\]");

        if (!health.Success)
        {
            return;
        }

        bool screenSaysOk = health.Groups["v"].Value.Trim() == "OK";
        Assert.True(
            status.HealthOk == screenSaysOk,
            $"{name}: the screen's health monitor reads '{health.Groups["v"].Value.Trim()}' " +
            $"but the model reports HealthOk = {status.HealthOk}.");
    }

    // -------------------------------------------------------------------------------------

    private static string[] FixturePaths() =>
        Directory.Exists(FixtureRoot)
            ? Directory.GetFiles(FixtureRoot, "*.txt", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray()
            : [];

    private static StatusScreenParser ParserAt() => new(new FakeTimeProvider(Whenever));

    private static ReceiverStatus Parse(string name) =>
        ParserAt().Parse(string.Join("\r\n", ReadLines(name)));

    /// <summary>Reads a fixture as the device wrote it — see <see cref="StatusScreenParserTests"/>.</summary>
    private static string[] ReadLines(string name)
    {
        string text = Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(FixtureRoot, name)));
        return text.TrimEnd('\r', '\n').Split("\r\n");
    }
}
