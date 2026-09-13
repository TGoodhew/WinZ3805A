using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers.Nmea;
using WinZ3805A.Device.Models;

namespace WinZ3805A.Tests.Nmea;

/// <summary>
/// <c>GST</c> and <c>GBS</c> — the position's real uncertainty, and whether a satellite is lying (#516).
/// </summary>
/// <remarks>
/// <para>
/// #435 filed both of these as waiting for a receiver that emits them, and neither unit on the bench
/// does. Both will if asked: <c>UBX-CFG-MSG</c> to RAM, which
/// <c>build/Capture-Talker.ps1 -EnableSentences GST,GBS</c> sends, and which a power cycle undoes.
/// The bench lines below are from the forM8N on 12 Sep 2026, and the capture they came from is in
/// <c>Captures/form8n-gst-gbs.nmea</c>.
/// </para>
/// <para>
/// <b>The distinction these tests exist to protect is between "nothing is wrong" and "nobody
/// looked".</b> A healthy <c>GBS</c> leaves its fault fields empty, so the good news and the absent
/// sentence produce nulls in the same places; only the presence of the report tells them apart. A
/// UI that conflated them would show a talker as integrity-monitored when it had never been asked.
/// </para>
/// </remarks>
public sealed class NmeaFixIntegrityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 17, 0, 0, TimeSpan.Zero);

    /// <summary>A cycle from the bench, with both sentences present and a 12-satellite fix.</summary>
    private static string BenchCycle() =>
        string.Join(
            '\n',
            "$GNRMC,004546.00,A,4731.31405,N,12212.36602,W,0.02,,120926,,,A*6D",
            "$GNGGA,004546.00,4731.31405,N,12212.36602,W,1,12,0.74,24.9,M,-18.8,M,,*41",
            "$GNGSA,A,3,08,30,07,14,04,27,09,05,22,,,,1.36,0.74,1.14,1*06",
            "$GNGST,004546.00,23,,,,1.8,2.6,4.0*42",
            "$GNGBS,004546.00,1.8,2.6,4.0,,,,,,*55");

    // -------------------------------------------------------------------------------------
    // GST — how wrong the position might be
    // -------------------------------------------------------------------------------------

    /// <summary>The three per-axis deviations and the ranging RMS, exactly as the bench sent them.</summary>
    [Fact]
    public void TheBenchUncertaintyIsReadFieldForField()
    {
        ReceiverStatus status = NmeaStatusParser.Parse(BenchCycle(), Now);

        PositionUncertainty uncertainty = Assert.IsType<PositionUncertainty>(status.Uncertainty);

        Assert.Equal(23, uncertainty.RangeResidualRmsMetres);
        Assert.Equal(1.8, uncertainty.LatitudeSigmaMetres);
        Assert.Equal(2.6, uncertainty.LongitudeSigmaMetres);
        Assert.Equal(4.0, uncertainty.AltitudeSigmaMetres);
    }

    /// <summary>
    /// The error ellipse is empty on this hardware, and the sentence is still read.
    /// </summary>
    /// <remarks>
    /// u-blox fills the RMS and the three deviations and leaves the ellipse blank. A parser that
    /// required the whole sentence would report nothing at all from a sentence carrying four good
    /// numbers, which is the failure §11.1 is written to prevent.
    /// </remarks>
    [Fact]
    public void AnAbsentErrorEllipseDoesNotCostTheRestOfTheSentence()
    {
        ReceiverStatus status = NmeaStatusParser.Parse(BenchCycle(), Now);

        PositionUncertainty uncertainty = Assert.IsType<PositionUncertainty>(status.Uncertainty);

        Assert.Null(uncertainty.SemiMajorMetres);
        Assert.Null(uncertainty.SemiMinorMetres);
        Assert.Null(uncertainty.OrientationDegrees);
        Assert.False(uncertainty.IsEmpty);
    }

    /// <summary>The two horizontal deviations combine into one figure, and it is the root sum of squares.</summary>
    [Fact]
    public void TheHorizontalDeviationsCombineIntoOneFigure()
    {
        ReceiverStatus status = NmeaStatusParser.Parse(BenchCycle(), Now);

        double drms = Assert.IsType<PositionUncertainty>(status.Uncertainty).HorizontalDrmsMetres!.Value;

        // sqrt(1.8² + 2.6²) = 3.162…
        Assert.Equal(3.162, drms, 3);
    }

    /// <summary>With one axis missing there is no combined figure, rather than a figure from one axis.</summary>
    /// <remarks>
    /// The tempting failure is to fall back to whichever axis is present. That reports a horizontal
    /// error smaller than the one axis it actually knows about, which is worse than reporting
    /// nothing — it is a number, it looks reasonable, and it is optimistic.
    /// </remarks>
    [Fact]
    public void OneAxisAloneYieldsNoCombinedFigure()
    {
        PositionUncertainty half = new() { LatitudeSigmaMetres = 1.8 };

        Assert.Null(half.HorizontalDrmsMetres);
    }

    /// <summary>No <c>GST</c> in the cycle is null, not an empty record.</summary>
    [Fact]
    public void ACycleWithoutGstReportsNoUncertainty()
    {
        ReceiverStatus status = NmeaStatusParser.Parse(
            "$GNGGA,004546.00,4731.31405,N,12212.36602,W,1,12,0.74,24.9,M,-18.8,M,,*41", Now);

        Assert.Null(status.Uncertainty);
    }

    /// <summary>Dilution and uncertainty are separate readings and neither is derived from the other.</summary>
    /// <remarks>
    /// The whole argument of #516. The bench cycle carries an excellent HDOP of 0.74 and a
    /// horizontal uncertainty over three metres; a reader told only the first would conclude the
    /// position was good to centimetres.
    /// </remarks>
    [Fact]
    public void GeometryAndErrorAreReportedSeparately()
    {
        ReceiverStatus status = NmeaStatusParser.Parse(BenchCycle(), Now);

        Assert.Equal(0.74, Assert.IsType<DilutionOfPrecision>(status.Dop).Horizontal);
        Assert.True(Assert.IsType<PositionUncertainty>(status.Uncertainty).HorizontalDrmsMetres > 3.0);
    }

    // -------------------------------------------------------------------------------------
    // GBS — whether anything is lying
    // -------------------------------------------------------------------------------------

    /// <summary>The healthy bench report: errors estimated, no satellite named.</summary>
    [Fact]
    public void AHealthyIntegrityReportNamesNoSatelliteAndIsNotEmpty()
    {
        ReceiverStatus status = NmeaStatusParser.Parse(BenchCycle(), Now);

        IntegrityReport integrity = Assert.IsType<IntegrityReport>(status.Integrity);

        Assert.Equal(1.8, integrity.LatitudeErrorMetres);
        Assert.Equal(2.6, integrity.LongitudeErrorMetres);
        Assert.Equal(4.0, integrity.AltitudeErrorMetres);

        Assert.False(integrity.FaultSuspected);
        Assert.False(integrity.IsEmpty);
    }

    /// <summary>
    /// A named satellite is read, with its bias — the state no sitting has produced.
    /// </summary>
    /// <remarks>
    /// <b>Synthetic, and labelled as such.</b> Every other assertion here is against bytes a
    /// receiver sent; this one is against a sentence written by hand from the standard, because the
    /// fault state cannot be summoned on demand. It pins the field offsets for the day it happens,
    /// which is the only day nobody will be able to check them.
    /// </remarks>
    [Fact]
    public void AFaultedSatelliteIsNamedWithItsBias()
    {
        string cycle = Sentence("GNGBS,015509.00,-0.031,-0.006,0.008,21,0.000,-33.5,5.1");

        IntegrityReport integrity = Assert.IsType<IntegrityReport>(
            NmeaStatusParser.Parse(cycle, Now).Integrity);

        Assert.True(integrity.FaultSuspected);
        Assert.Equal(21, integrity.SuspectSatelliteId);
        Assert.Equal(0.0, integrity.MissedDetectionProbability);
        Assert.Equal(-33.5, integrity.BiasMetres);
        Assert.Equal(5.1, integrity.BiasSigmaMetres);
    }

    /// <summary>No <c>GBS</c> at all is null, which is not the same as a report finding nothing.</summary>
    /// <remarks>
    /// The pair of assertions this file exists for. Both a talker that was never asked and a talker
    /// reporting perfect health produce <c>FaultSuspected == false</c> when read carelessly; only
    /// one of them has anything to say.
    /// </remarks>
    [Fact]
    public void SilenceAndGoodNewsAreDifferentAnswers()
    {
        ReceiverStatus silent = NmeaStatusParser.Parse(
            "$GNGGA,004546.00,4731.31405,N,12212.36602,W,1,12,0.74,24.9,M,-18.8,M,,*41", Now);
        ReceiverStatus healthy = NmeaStatusParser.Parse(BenchCycle(), Now);

        Assert.Null(silent.Integrity);
        Assert.NotNull(healthy.Integrity);
        Assert.False(healthy.Integrity.FaultSuspected);
    }

    // -------------------------------------------------------------------------------------
    // The driver, and the corpus
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The driver keeps both sentences, or the parser above never sees one.
    /// </summary>
    /// <remarks>
    /// A sentence the driver does not classify is discarded before the parser runs, so every
    /// assertion in this file would pass while the application showed nothing. That is a whole layer
    /// between the bytes and the screen, and it is invisible to a parser test.
    /// </remarks>
    [Theory]
    [InlineData("$GNGST,004546.00,23,,,,1.8,2.6,4.0*42")]
    [InlineData("$GNGBS,004546.00,1.8,2.6,4.0,,,,,,*55")]
    public void TheDriverKeepsBothSentences(string line) =>
        Assert.NotNull(new NmeaDriver(new FakeTimeProvider(Now)).ClassifyLine(line));

    /// <summary>
    /// Against every committed capture: whatever is read must be credible, because a wrong offset
    /// yields a plausible number rather than a failure.
    /// </summary>
    /// <remarks>
    /// The same argument as <c>EveryCaptureYieldsCredibleFixQuality</c>. Reading <c>GST</c> one
    /// field low would take the ranging RMS as a latitude deviation and report a metre-scale fix as
    /// tens of metres, with nothing throwing.
    /// </remarks>
    [Theory]
    [MemberData(nameof(NmeaCaptureReplayTests.Captures), MemberType = typeof(NmeaCaptureReplayTests))]
    public void EveryCaptureYieldsCredibleErrorStatistics(string capture)
    {
        string text = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Nmea", "Captures", capture));

        ReceiverStatus status = NmeaStatusParser.Parse(text, Now);

        if (status.Uncertainty is { } uncertainty)
        {
            // Metres, non-negative, and nothing a working receiver reports is kilometres.
            //
            // RangeResidualRmsMetres is NOT in this list, and its absence is a finding rather than
            // an oversight: on the bench forM8N that field ranges from 17 to 3,179,277 between one
            // second and the next while these deviations hold steady. See the remarks on the model.
            // Range-checking it here would fail on real bytes from a healthy receiver.
            foreach (double? figure in new[]
            {
                uncertainty.SemiMajorMetres,
                uncertainty.SemiMinorMetres,
                uncertainty.LatitudeSigmaMetres,
                uncertainty.LongitudeSigmaMetres,
                uncertainty.AltitudeSigmaMetres,
            })
            {
                if (figure is { } value)
                {
                    Assert.InRange(value, 0.0, 1000.0);
                }
            }

            // It must still be a number rather than negative, which is all that can be said of it.
            if (uncertainty.RangeResidualRmsMetres is { } rms)
            {
                Assert.True(rms >= 0.0, $"a negative range RMS in {capture}");
            }

            if (uncertainty.OrientationDegrees is { } orientation)
            {
                Assert.InRange(orientation, 0.0, 360.0);
            }
        }

        if (status.Integrity is { } integrity)
        {
            if (integrity.SuspectSatelliteId is { } id)
            {
                Assert.InRange(id, 1, 255);
            }

            if (integrity.MissedDetectionProbability is { } probability)
            {
                Assert.InRange(probability, 0.0, 1.0);
            }
        }
    }

    /// <summary>The capture taken for #516 really does carry both sentences.</summary>
    /// <remarks>
    /// <b>Without this the theory above is a decoration.</b> Every one of its checks is guarded by
    /// "if the reading is present", so a corpus in which no capture carries <c>GST</c> passes it
    /// completely — which was the state of the world before this issue, and is the trap the
    /// corpus's own README describes.
    /// </remarks>
    [Fact]
    public void TheCorpusContainsACaptureCarryingBothSentences()
    {
        string text = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Nmea", "Captures", "form8n-gst-gbs.nmea"));

        ReceiverStatus status = NmeaStatusParser.Parse(text, Now);

        Assert.NotNull(status.Uncertainty);
        Assert.NotNull(status.Integrity);
    }

    /// <summary>
    /// The range RMS this hardware sends is unusable, and the deviations beside it are not (#516).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A finding, pinned so it cannot be quietly un-found.</b> The obvious reading of a field
    /// holding 3,179,277 is that the parser took the wrong offset, and the obvious repair is to
    /// shift it — which would silently corrupt the three deviations that are correct. This asserts
    /// both halves at once: the RMS really does go into the millions, and the same sentences carry
    /// sane metre-scale deviations, so the offsets are right and the receiver is the problem.
    /// </para>
    /// <para>
    /// It also guards the display decision. Nothing shows the RMS, and if someone later surfaces it
    /// this test is where the reason it was left out is written down.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheBenchRangeRmsIsUnusableWhileItsDeviationsAreSound()
    {
        string[] lines = File.ReadAllLines(
            Path.Combine(AppContext.BaseDirectory, "Nmea", "Captures", "form8n-gst-gbs.nmea"));

        List<PositionUncertainty> readings = [];
        foreach (string line in lines)
        {
            if (line.Contains("GST,", StringComparison.Ordinal) &&
                NmeaStatusParser.Parse(line, Now).Uncertainty is { } reading)
            {
                readings.Add(reading);
            }
        }

        Assert.Equal(300, readings.Count);

        // The field the receiver cannot be trusted on.
        Assert.Contains(readings, r => r.RangeResidualRmsMetres > 1_000_000);

        // The fields it can, through every cycle including those.
        Assert.All(readings, r =>
        {
            Assert.InRange(r.LatitudeSigmaMetres!.Value, 1.0, 5.0);
            Assert.InRange(r.LongitudeSigmaMetres!.Value, 1.0, 5.0);
            Assert.InRange(r.AltitudeSigmaMetres!.Value, 1.0, 10.0);
        });
    }

    /// <summary>A sentence with its checksum computed, for the synthetic cases.</summary>
    private static string Sentence(string body)
    {
        byte checksum = 0;
        foreach (char c in body)
        {
            checksum ^= (byte)c;
        }

        return $"${body}*{checksum:X2}";
    }
}
