using System.Text;

using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Drivers.Nmea;
using WinZ3805A.Device.Models;
using WinZ3805A.Device.Transport;

namespace WinZ3805A.Tests.Nmea;

/// <summary>
/// Every captured talker, replayed through the listener and the parser that will meet it (#420).
/// </summary>
/// <remarks>
/// <para>
/// The NMEA family was written against <c>tools/NmeaSimulator</c> and shipped having never heard a
/// real receiver (#310). A simulator only emits what its author thought of, so the whole family
/// rested on one person's idea of what a talker says. These tests are the other half: the bytes a
/// receiver actually sent, replayed offline, for ever.
/// </para>
/// <para>
/// <b>They are written the way <c>FixtureCorpusTests</c> is, and for the same reason.</b> What can
/// be asserted about a capture nobody has read yet is narrower than it looks — §11.1 says an
/// unreadable field becomes <see langword="null"/>, so demanding that a value be present would
/// assert the opposite of the requirement. What is left is real all the same. A PRN below 1, an
/// elevation past the zenith, an azimuth past the compass, a signal strength outside the C/N
/// scale, a position off the globe or a clock that runs backwards are wrong in any state, on any
/// receiver, and each is what a field-index error produces first on output nobody has read.
/// </para>
/// <para>
/// <b>Nothing here asserts a fix.</b> A capture may hold a cold start, an antenna covered or a
/// cable pulled — #420 asks for exactly those — so a test demanding satellites or a position would
/// fail on the most valuable file in the folder. These invariants hold whether the receiver can
/// see the sky or not.
/// </para>
/// </remarks>
public sealed class NmeaCaptureReplayTests
{
    /// <summary>
    /// An instant to pin the clock at. Arbitrary, and it has to be: a capture carries its own time
    /// in its sentences, and nothing here may depend on when it was replayed.
    /// </summary>
    private static readonly DateTimeOffset Whenever = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The driver's silence timeout. The clock never advances here, so nothing goes stale.</summary>
    private static readonly TimeSpan Silence = TimeSpan.FromSeconds(3);

    /// <summary>A sitting worth committing. Below this a file is a smoke test rather than evidence.</summary>
    private const int LeastCycles = 10;

    public static TheoryData<string> Captures
    {
        get
        {
            TheoryData<string> data = [];
            foreach (string path in CapturePaths())
            {
                data.Add(Path.GetFileName(path));
            }

            return data;
        }
    }

    private static string CaptureRoot => Path.Combine(AppContext.BaseDirectory, "Nmea", "Captures");

    private static IReadOnlyList<string> CapturePaths() =>
        Directory.Exists(CaptureRoot)
            ? [.. Directory.GetFiles(CaptureRoot, "*.nmea", SearchOption.AllDirectories).Order(StringComparer.Ordinal)]
            : [];

    // -------------------------------------------------------------------------------------
    // The corpus itself
    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// A theory over an empty set passes silently, which would make every assertion below a
    /// decoration — #181's rule, and the one this folder's own README quotes.
    /// </remarks>
    [Fact]
    public void ThereAreCapturesToReplay() =>
        Assert.NotEmpty(CapturePaths());

    /// <summary>Every capture has its provenance note beside it, and somebody has filled it in.</summary>
    /// <remarks>
    /// <c>Capture-Talker.ps1</c> writes the note with its "What was happening" section deliberately
    /// blank, because only the person who was there can write it. A capture whose note still holds
    /// that placeholder is a file rather than evidence, and the difference is invisible until
    /// someone tries to use the bytes a year later.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Captures))]
    public void EveryCaptureCarriesItsProvenance(string name)
    {
        string note = Path.Combine(CaptureRoot, Path.ChangeExtension(name, ".md"));
        Assert.True(File.Exists(note), $"{name} has no provenance note beside it.");
        Assert.DoesNotContain("_Fill this in by hand", File.ReadAllText(note), StringComparison.Ordinal);
    }

    /// <summary>Every capture is talker output rather than something that landed here by accident.</summary>
    /// <remarks>
    /// The tests below cannot fail on a file that is not a talker: an arbitrary file yields no
    /// cycles, every per-cycle invariant then holds vacuously, and the corpus reports itself as
    /// covered. That is the trap the SmartClock corpus was built around (#221), and it costs one
    /// test to close. The count is of sentences that pass their checksum because the checksum is
    /// what distinguishes a real cycle from the plausible-looking rubbish a wrong baud rate makes.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Captures))]
    public void EveryCaptureIsTalkerOutput(string name)
    {
        IReadOnlyList<string> lines = LinesOf(Bytes(name));

        int candidates = 0;
        int valid = 0;
        HashSet<string> identifiers = new(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            if (!line.StartsWith('$'))
            {
                continue;
            }

            candidates++;
            if (NmeaSentence.TryParse(line) is { ChecksumValid: true } sentence)
            {
                valid++;
                identifiers.Add(sentence.Identifier);
            }
        }

        Assert.True(candidates >= 100, $"{name} holds {candidates} candidate sentences, which is not a sitting.");
        Assert.True(valid * 100 >= candidates * 95, $"{name}: only {valid} of {candidates} sentences passed their checksum.");
        // RMC is required by construction: it is this driver's cycle boundary, so a capture without
        // one would never complete a cycle and could not be evidence of anything.
        Assert.Contains("RMC", identifiers);

        // A position-bearing sentence, but NOT specifically GGA (#429).
        //
        // This asserted GGA until 8 Sep 2026, which quietly encoded the very assumption #429 exists
        // to question — that a receiver always sends it. It does not: `vk162-gns-no-gga` is the same
        // VK-162 with `UBX-CFG-MSG` used to enable GNS and disable GGA, and it emits 720 GNS and not
        // one GGA. The capture is valid talker output and this test rejected it, so the assertion
        // was describing the corpus it happened to have rather than what makes a capture a capture.
        Assert.True(
            identifiers.Contains("GGA") || identifiers.Contains("GNS"),
            $"{name} carries neither GGA nor GNS, so nothing in it reports a position.");

        DeviceIdentity? identity = new NmeaDriver(new FakeTimeProvider(Whenever)).Overhear(lines);
        Assert.Equal(NmeaDriver.FamilyName, identity?.Manufacturer);
    }

    // -------------------------------------------------------------------------------------
    // The replay
    // -------------------------------------------------------------------------------------

    /// <summary>Every cycle the listener assembles, parsed, against what must be true of any of them.</summary>
    /// <remarks>
    /// <para>
    /// The lines go in one at a time through <see cref="BroadcastListener.Seed"/>, which is the
    /// path a heard line takes — the driver's classifier, the cycle boundary, the whole-cycle
    /// answer — with only the byte-to-line splitting left out. That half is
    /// <see cref="TheSameCyclesArriveHoweverTheBytesAreChunked"/>'s business, and it is left out
    /// here deliberately: this walks every cycle in the file, which needs the replay to be
    /// synchronous rather than racing a read loop.
    /// </para>
    /// <para>
    /// The tracked count is asserted twice over, from the two code paths that compute it —
    /// <see cref="NmeaStatusParser"/> for the sky plot and <see cref="NmeaDriver.InterpretSweep"/>
    /// for the readout. They read the same GSV groups with different bounds, and a disagreement
    /// between them is a satellite the sky plot draws and the medallion does not count.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Captures))]
    public async Task EveryCycleParsesWithinItsInvariants(string name)
    {
        FakeTimeProvider clock = new(Whenever);
        NmeaDriver driver = new(clock);
        await using FakeTransport transport = new() { Silent = true, EchoCommands = false, EmitPrompt = false };
        await transport.OpenAsync();
        await using BroadcastListener listener = new(transport, driver, clock);

        int completed = 0;
        int parsed = 0;
        DateTimeOffset? previous = null;

        foreach (string line in LinesOf(Bytes(name)))
        {
            listener.Seed([line]);
            if (listener.CyclesHeard == completed)
            {
                continue;
            }

            completed = listener.CyclesHeard;
            parsed++;

            ReceiverStatus status = driver.Parse(Joined(listener, PollPlan.WholeCycle));

            Assert.DoesNotContain(
                status.ParseWarnings,
                warning => warning.Contains("failed unexpectedly", StringComparison.Ordinal));

            foreach (TrackedSatellite satellite in status.Tracked)
            {
                Assert.InRange(satellite.Prn, 1, 999);

                // A tracked satellite has a signal by construction, so the bound that carries the
                // weight is the upper one: NMEA's C/N is 0-99 dB-Hz, and a field-index error that
                // read an azimuth here would land outside it.
                Assert.True(
                    satellite.SignalStrength is >= 1 and <= 99,
                    $"{name}: PRN {satellite.Prn} tracked at a signal strength of {satellite.SignalStrength?.ToString() ?? "null"}.");
                AssertSky(satellite.ElevationDegrees, satellite.AzimuthDegrees, name);
            }

            foreach (PredictedSatellite satellite in status.NotTracked)
            {
                Assert.InRange(satellite.Prn, 1, 999);
                AssertSky(satellite.ElevationDegrees, satellite.AzimuthDegrees, name);
            }

            Assert.Empty(status.Tracked.Select(s => s.Prn).Intersect(status.NotTracked.Select(s => s.Prn)));

            if (status.Position is GeoPosition position)
            {
                Assert.InRange(position.LatitudeDegrees ?? 0, -90, 90);
                Assert.InRange(position.LongitudeDegrees ?? 0, -180, 180);
            }

            if (status.DeviceDateTime is DateTimeOffset time)
            {
                Assert.Equal(TimeSpan.Zero, time.Offset);
                Assert.InRange(time.Year, 2020, 2099);

                // A clock that goes backwards is the midnight-straddle defect the parser's own
                // comment describes: a time from one sentence paired with a date from the next.
                Assert.True(
                    previous is null || time >= previous,
                    $"{name}: the clock went back from {previous:o} to {time:o}.");
                previous = time;
            }

            SweepInterpretation sweep = driver.InterpretSweep(
            [
                Joined(listener, NmeaSentence.KeyFor("RMC")),
                Joined(listener, NmeaSentence.KeyFor("GGA")),
                Joined(listener, NmeaSentence.KeyFor("GSA")),
                Joined(listener, NmeaSentence.KeyFor("GSV")),
            ]);

            Assert.Null(sweep.Rejection);
            Assert.Equal(status.Tracked.Count, sweep.Readings.SatellitesTracked ?? 0);
        }

        Assert.True(parsed >= LeastCycles, $"{name} yielded {parsed} complete cycles, which is not a sitting.");
        Assert.Equal(0, listener.CyclesAbandoned);
    }

    /// <summary>The same cycle arrives whether the bytes come one at a time or all at once.</summary>
    /// <remarks>
    /// This is the half <see cref="EveryCycleParsesWithinItsInvariants"/> leaves out, and the whole
    /// reason the captures are stored as bytes: a real talker splits a sentence across reads, ends
    /// a line with a bare LF and truncates one when a cable is pulled, and none of that survives a
    /// capture tidied into lines. A serial port at 9600 baud delivers a few bytes at a time, so the
    /// one-byte case is the realistic one rather than the pathological one.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Captures))]
    public async Task TheSameCyclesArriveHoweverTheBytesAreChunked(string name)
    {
        // A slice, because what is under test is the boundary between reads rather than the length
        // of the file, and one byte at a time over a whole sitting is a quarter of a million writes.
        byte[] bytes = Bytes(name);
        byte[] slice = bytes[..Math.Min(8192, bytes.Length)];

        // What the slice holds, read the synchronous way, so the chunked runs below have a figure
        // to settle on rather than a moment to guess at. Waiting for "a cycle" instead compares a
        // different cycle in each run - the whole file is consumed before the assertion at one
        // chunk size and a fraction of it at another - which is a race that passes on a short file.
        (int expectedCycles, string expected) = await SeedWholeAsync(slice);
        Assert.True(expectedCycles > 0, $"{name}: the first {slice.Length} bytes hold no complete cycle.");

        foreach (int chunk in new[] { 1, 7, 512, slice.Length })
        {
            FakeTimeProvider clock = new(Whenever);
            NmeaDriver driver = new(clock);
            await using FakeTransport transport = new() { Silent = true, EchoCommands = false, EmitPrompt = false };
            await transport.OpenAsync();
            await using BroadcastListener listener = new(transport, driver, clock);
            listener.Start();

            for (int offset = 0; offset < slice.Length; offset += chunk)
            {
                await transport.EmitAsync(slice.AsMemory(offset, Math.Min(chunk, slice.Length - offset)));
            }

            int heard = await SettleAsync(() => listener.CyclesHeard, seen => seen >= expectedCycles);
            Assert.True(
                heard == expectedCycles,
                $"{name}: {heard} cycles at a chunk size of {chunk}, where seeding the same bytes gave {expectedCycles}.");
            Assert.Equal(expected, Joined(listener, PollPlan.WholeCycle));
        }
    }

    // -------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------

    private static byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(CaptureRoot, name));

    /// <summary>
    /// Seeds bytes line by line and reports how many complete cycles they held and what the last
    /// one was — the synchronous path, used as the answer the chunked runs are held to.
    /// </summary>
    private static async Task<(int Cycles, string LastCycle)> SeedWholeAsync(byte[] bytes)
    {
        FakeTimeProvider clock = new(Whenever);
        await using FakeTransport transport = new() { Silent = true, EchoCommands = false, EmitPrompt = false };
        await transport.OpenAsync();
        await using BroadcastListener listener = new(transport, new NmeaDriver(clock), clock);

        // Never started: seeding is synchronous and needs no read loop, which is the whole point.
        listener.Seed(LinesOf(bytes));
        return (listener.CyclesHeard, Joined(listener, PollPlan.WholeCycle));
    }

    /// <summary>An answer's lines as the parser and the sweep want them: one sentence per line.</summary>
    private static string Joined(BroadcastListener listener, string key) =>
        string.Join('\n', listener.Answer(key, Silence).Lines);

    /// <summary>
    /// The bytes split the way <see cref="BroadcastListener"/> splits them — on LF, decoded as
    /// Latin-1, a carriage return trimmed, and a trailing partial line held back.
    /// </summary>
    private static IReadOnlyList<string> LinesOf(byte[] bytes)
    {
        List<string> lines = [];
        int start = 0;
        for (int index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n')
            {
                continue;
            }

            lines.Add(Encoding.Latin1.GetString(bytes, start, index - start).TrimEnd('\r'));
            start = index + 1;
        }

        return lines;
    }

    private static void AssertSky(int? elevation, int? azimuth, string name)
    {
        if (elevation is int degrees)
        {
            Assert.True(degrees is >= 0 and <= 90, $"{name}: an elevation of {degrees} degrees.");
        }

        if (azimuth is int bearing)
        {
            Assert.True(bearing is >= 0 and <= 359, $"{name}: an azimuth of {bearing} degrees.");
        }
    }

    private static async Task<T> SettleAsync<T>(Func<T> read, Func<T, bool> done)
    {
        T value = read();
        for (int attempt = 0; attempt < 200 && !done(value); attempt++)
        {
            await Task.Delay(10);
            value = read();
        }

        return value;
    }
}
