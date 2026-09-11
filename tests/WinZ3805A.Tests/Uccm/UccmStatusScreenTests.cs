using System.Text;

using WinZ3805A.Device.Drivers.Uccm;
using WinZ3805A.Device.Models;

namespace WinZ3805A.Tests.Uccm;

/// <summary>
/// The <c>SYST:STAT?</c> screen, read from the first UCCM capture this repository has (#416).
/// </summary>
/// <remarks>
/// <para>
/// <b>These are the first UCCM assertions with a receiver behind them.</b> Everything else in this
/// folder is checked against Lady Heather's source; these are checked against
/// <c>Uccm/Captures/trimble-uccm-p-2026-09-11.txt</c>, taken from a Trimble UCCM-P at
/// 57600-8-N-1 on 11 Sep 2026 while it was tracking and holding a surveyed position.
/// </para>
/// <para>
/// <b>#416 predicted the wrong thing and the capture says so.</b> The issue expects
/// <c>SYST:STAT?</c> to return "a line containing a <c>C5 </c> marker followed by ~44
/// space-separated two-digit hex values". It returns an 80-column text screen; the C5 frames are
/// the module's unsolicited broadcast, which arrives between a reply and its prompt (#470).
/// </para>
/// </remarks>
public sealed class UccmStatusScreenTests
{
    private static readonly DateTimeOffset Whenever = new(2026, 9, 11, 1, 40, 0, TimeSpan.Zero);

    private static ReceiverStatus Parsed() =>
        UccmStatusParser.Parse(Screen(), Whenever, UccmProfile.Unknown);

    /// <summary>
    /// Seven tracked satellites, with the columns the receiver printed.
    /// </summary>
    /// <remarks>
    /// <b>This is what a leading NUL cost.</b> Rows end <c>0D 0A 00</c>, so read as CRLF lines each
    /// row begins with a NUL — which is not whitespace, so it neither trimmed nor split, and became
    /// the row's first token where the PRN should be. Every row was discarded as having no PRN, and
    /// a receiver tracking seven satellites reported none at all, with no warning anywhere.
    /// </remarks>
    [Fact]
    public void TheTrackedTableIsReadWithItsColumnsIntact()
    {
        ReceiverStatus status = Parsed();

        Assert.Equal(7, status.Tracked.Count);
        Assert.Equal([12, 13, 24, 19, 21, 6, 11], status.Tracked.Select(satellite => satellite.Prn));

        TrackedSatellite first = status.Tracked[0];
        Assert.Equal(60, first.ElevationDegrees);
        Assert.Equal(214, first.AzimuthDegrees);
        Assert.Equal(40, first.SignalStrength);

        Assert.All(status.Tracked, satellite => Assert.NotNull(satellite.SignalStrength));
    }

    /// <summary>
    /// The second table on the same rows is the not-tracked one, and does not contaminate the first.
    /// </summary>
    /// <remarks>
    /// The header is <c>PRN  El  Az  C/N   PRN  El  Az</c>: two satellites and a panel line share
    /// every row. Splitting a row on whitespace glues the second satellite's PRN onto the first's
    /// C/N column, which is the failure this exists to prevent — so the PRNs are asserted on both
    /// sides rather than only the count.
    /// </remarks>
    [Fact]
    public void TheNotTrackedTableBesideItIsReadSeparately()
    {
        ReceiverStatus status = Parsed();

        Assert.Equal(4, status.NotTracked.Count);
        Assert.Equal([22, 25, 29, 5], status.NotTracked.Select(satellite => satellite.Prn));

        PredictedSatellite first = status.NotTracked[0];
        Assert.Equal(6, first.ElevationDegrees);
        Assert.Equal(58, first.AzimuthDegrees);

        // No PRN appears in both tables: that is what a row split the wrong way produces.
        Assert.Empty(status.Tracked.Select(s => s.Prn).Intersect(status.NotTracked.Select(s => s.Prn)));
    }

    /// <summary>The figures of merit and the elevation mask, which parsed before this work too.</summary>
    [Fact]
    public void TheFiguresOfMeritAndElevationMaskAreRead()
    {
        ReceiverStatus status = Parsed();

        Assert.Equal(2, status.Tfom);
        Assert.Equal(0, status.Ffom);
        Assert.Equal(5, status.ElevationMaskDegrees);
    }

    /// <summary>
    /// The time panel, with the scale the receiver labelled it rather than an assumption.
    /// </summary>
    /// <remarks>
    /// The row reads <c>GPS      01:40:39     11 Sep 2026</c>. GPS time and UTC differ by the leap
    /// seconds, so a reading that records which one it is can be corrected later where one that
    /// silently claims UTC cannot.
    /// </remarks>
    [Fact]
    public void TheTimePanelIsReadWithItsScale()
    {
        ReceiverStatus status = Parsed();

        Assert.Equal(TimeScale.Gps, status.TimeScale);
        Assert.Equal(new DateTimeOffset(2026, 9, 11, 1, 40, 39, TimeSpan.Zero), status.DeviceDateTime);
    }

    /// <summary>The position panel: held, and where.</summary>
    /// <remarks>
    /// <c>LAT      S  34:32:39.019</c> and <c>LON      E 150:50:25.107</c> — degrees, minutes and
    /// seconds with a hemisphere letter, so south and west are negative. Asserted to six decimal
    /// places, which is about 0.1 m and finer than the receiver's own resolution.
    /// </remarks>
    [Fact]
    public void ThePositionPanelIsReadAsSignedDegrees()
    {
        ReceiverStatus status = Parsed();

        Assert.Equal(PositionMode.Hold, status.PositionMode);
        Assert.Equal(PositionQualifier.Held, status.PositionQualifier);

        Assert.NotNull(status.Position);
        Assert.Equal(-34.544172, status.Position.LatitudeDegrees!.Value, 6);
        Assert.Equal(150.840308, status.Position.LongitudeDegrees!.Value, 6);
        Assert.Equal(49.72, status.Position.HeightMetres!.Value, 2);
        Assert.Equal(HeightDatum.Msl, status.HeightDatum);

        Assert.Equal(0d, status.AntennaDelayNanoseconds);
    }

    /// <summary>
    /// <c>[GPS 1PPS Valid]</c> is taken from the text, not inferred from a time code.
    /// </summary>
    /// <remarks>
    /// The reply carries no <c>C5</c> frame — those arrive unprompted, between a reply and its
    /// prompt — so a validity that could only be read out of one would be absent on every screen
    /// this module prints.
    /// </remarks>
    [Fact]
    public void OnePpsValidityIsReadFromTheScreenItself()
    {
        ReceiverStatus status = Parsed();

        Assert.True(status.GpsOnePpsValid);
        Assert.DoesNotContain("C5 ", Screen(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A screen that parsed cleanly says nothing, and the count cross-check is what proves it.
    /// </summary>
    /// <remarks>
    /// The screen states <c>Tracking: 7</c> and <c>Not Tracking: 4</c> in words, above the table it
    /// then prints. The parser compares the two, so a firmware that lays the columns out
    /// differently produces a warning rather than a quietly short list — which is the failure mode
    /// that hid this defect in the first place.
    /// </remarks>
    [Fact]
    public void AScreenThatParsedCleanlyCarriesNoWarnings()
    {
        Assert.Empty(Parsed().ParseWarnings);
    }

    /// <summary>
    /// Moving a column breaks the count cross-check rather than silently losing satellites.
    /// </summary>
    /// <remarks>
    /// Deliberate violation: the header's second <c>PRN</c> is pushed four columns right, so the
    /// not-tracked block starts inside the tracked one's C/N column and the rows stop lining up.
    /// The point is not which count changes but that the reply stops claiming to be clean.
    /// </remarks>
    [Fact]
    public void AColumnLayoutThisDriverCannotReadIsReportedRatherThanSwallowed()
    {
        string moved = Screen().Replace(
            "PRN  El  Az  C/N   PRN  El  Az",
            "PRN  El  Az  C/N       PRN  El",
            StringComparison.Ordinal);

        ReceiverStatus status = UccmStatusParser.Parse(moved, Whenever, UccmProfile.Unknown);

        Assert.NotEmpty(status.ParseWarnings);
    }

    /// <summary>The reply as the receiver sent it, from the capture's payload lines.</summary>
    private static string Screen()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory, "Uccm", "Captures", "trimble-uccm-p-2026-09-11.txt");

        Assert.True(File.Exists(path), $"The capture is missing from the test output: {path}");

        StringBuilder screen = new();
        bool inCommand = false;
        bool inLines = false;

        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith("==== SENT: ", StringComparison.Ordinal))
            {
                inCommand = line["==== SENT: ".Length..].Trim() == "SYST:STAT?";
                inLines = false;
                continue;
            }

            if (!inCommand)
            {
                continue;
            }

            if (line.StartsWith("---- LINES", StringComparison.Ordinal))
            {
                inLines = true;
                continue;
            }

            const string payload = "  [Payload] ";
            if (inLines && line.StartsWith(payload, StringComparison.Ordinal))
            {
                screen.Append(line[payload.Length..]).Append("\r\n");
            }
        }

        return screen.ToString();
    }
}
