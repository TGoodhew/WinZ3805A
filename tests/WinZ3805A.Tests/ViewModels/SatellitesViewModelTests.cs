using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Controls;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// The §10.5 Satellites page's judgements, and §11.1's two signal-strength scales.
/// </summary>
public sealed class SatellitesViewModelTests
{
    private static readonly DateTimeOffset Captured = new(2026, 8, 13, 19, 0, 0, TimeSpan.Zero);

    private static SatellitesViewModel Connected(ReceiverStatus? status)
    {
        FakeTimeProvider clock = new(Captured);
        ReceiverStateStore store = new(clock);

        if (status is not null)
        {
            store.UpdateFull(status);
        }

        return new SatellitesViewModel(store) { Connection = ConnectionStatus.Connected };
    }

    private static ReceiverStatus Screen(
        IReadOnlyList<TrackedSatellite>? tracked = null,
        IReadOnlyList<PredictedSatellite>? notTracked = null,
        int? mask = 10,
        SignalStrengthKind kind = SignalStrengthKind.CarrierToNoise) => new()
        {
            Tracked = tracked ?? [],
            NotTracked = notTracked ?? [],
            ElevationMaskDegrees = mask,
            SignalStrengthKind = kind,
            CapturedAt = Captured,
        };

    // ---- The two scales ---------------------------------------------------------------------

    /// <remarks>
    /// §11.1 is emphatic that these are not interchangeable. A bar scaled to the wrong one is not
    /// mislabelled, it is wrong by a factor of five — and this is the field a user judges an
    /// antenna by.
    /// </remarks>
    [Fact]
    public void TheTwoScalesHaveDifferentDomains()
    {
        SignalStrengthScale cn = SignalStrengthScale.For(SignalStrengthKind.CarrierToNoise);
        SignalStrengthScale ss = SignalStrengthScale.For(SignalStrengthKind.SignalStrength);

        Assert.Equal((26, 55), (cn.Minimum, cn.Maximum));
        Assert.Equal((0, 255), (ss.Minimum, ss.Maximum));
        Assert.Equal("C/N", cn.Label);
        Assert.Equal("SS", ss.Label);
    }

    /// <remarks>
    /// The same number means opposite things on the two scales: 30 is weak C/N and, on the raw
    /// scale, the top of the band §11.1 calls weak.
    /// </remarks>
    [Fact]
    public void GoodnessIsJudgedPerScale()
    {
        Assert.False(SignalStrengthScale.For(SignalStrengthKind.CarrierToNoise).IsGood(30));
        Assert.True(SignalStrengthScale.For(SignalStrengthKind.CarrierToNoise).IsGood(49));
        Assert.True(SignalStrengthScale.For(SignalStrengthKind.SignalStrength).IsGood(30));
    }

    /// <remarks>
    /// An unknown scale is not drawn at all. A reader cannot tell a wrong bar from a right one, so
    /// a plausible-looking bar of unknown provenance is worse than no bar.
    /// </remarks>
    [Fact]
    public void AnUnknownScaleIsNotDrawable()
    {
        SignalStrengthScale unknown = SignalStrengthScale.For(SignalStrengthKind.Unknown);

        Assert.False(unknown.IsKnown);
        Assert.False(unknown.IsGood(200));
        Assert.Contains("scale unknown", unknown.Describe(200));
    }

    [Fact]
    public void AReadingIsClampedIntoItsDomain()
    {
        SignalStrengthScale cn = SignalStrengthScale.For(SignalStrengthKind.CarrierToNoise);

        Assert.Equal(26, cn.Clamp(0));
        Assert.Equal(55, cn.Clamp(9000));
        Assert.Equal(26, cn.Clamp(null));
    }

    /// <remarks>
    /// "49" alone is meaningless without knowing whether the scale tops out at 55 or 255, and a
    /// screen-reader user has no bar to look at for the proportion.
    /// </remarks>
    [Fact]
    public void TheSpokenFormNamesTheScale()
    {
        Assert.Equal("C/N 49 of 55", SignalStrengthScale.For(SignalStrengthKind.CarrierToNoise).Describe(49));
        Assert.Equal("SS 120 of 255", SignalStrengthScale.For(SignalStrengthKind.SignalStrength).Describe(120));
        Assert.Contains("not reported", SignalStrengthScale.For(SignalStrengthKind.CarrierToNoise).Describe(null));
    }

    // ---- Rows -------------------------------------------------------------------------------

    [Fact]
    public void TrackedRowsCarryTheReceiversOwnOrderAndScale()
    {
        SatellitesViewModel model = Connected(Screen(
            tracked:
            [
                new() { Prn = 18, ElevationDegrees = 79, AzimuthDegrees = 2, SignalStrength = 32 },
                new() { Prn = 5, ElevationDegrees = 25, AzimuthDegrees = 50, SignalStrength = 49 },
            ]));

        Assert.Equal([18, 5], model.Tracked.Select(row => row.Prn));
        Assert.Equal("79°", model.Tracked[0].ElevationText);
        Assert.Equal("2°", model.Tracked[0].AzimuthText);
        Assert.All(model.Tracked, row => Assert.Equal(SignalStrengthKind.CarrierToNoise, row.Kind));
        Assert.Contains("c/n 32 of 55", model.Tracked[0].Description);
    }

    /// <remarks>
    /// §11.1 forbids the parser from throwing, so every column but the PRN can be null. A row that
    /// rendered "0°" for a column that did not parse would be a reading the receiver never made.
    /// </remarks>
    [Fact]
    public void AnUnparsedColumnShowsADashRatherThanZero()
    {
        SatellitesViewModel model = Connected(Screen(
            tracked: [new() { Prn = 7, ElevationDegrees = null, AzimuthDegrees = null, SignalStrength = null }]));

        Assert.Equal(ReadoutFormatter.NoValue, model.Tracked[0].ElevationText);
        Assert.Equal(ReadoutFormatter.NoValue, model.Tracked[0].AzimuthText);
        Assert.Contains("not reported", model.Tracked[0].Description);
    }

    /// <remarks>
    /// Derived, not reported. The receiver's Not Tracking table prints only PRN, elevation and
    /// azimuth — §10.5's wireframe shows a status column that is not on the wire. Below-mask is the
    /// one of its three values that follows from what is printed; the others are not invented.
    /// </remarks>
    [Fact]
    public void BelowMaskIsDerivedFromTheElevationMask()
    {
        SatellitesViewModel model = Connected(Screen(
            notTracked:
            [
                new() { Prn = 3, ElevationDegrees = 4, AzimuthDegrees = 172 },
                new() { Prn = 4, ElevationDegrees = 61, AzimuthDegrees = 109 },
            ],
            mask: 10));

        Assert.True(model.NotTracked[0].IsBelowMask);
        Assert.Equal("below mask", model.NotTracked[0].StatusText);
        Assert.False(model.NotTracked[1].IsBelowMask);
        Assert.Equal(string.Empty, model.NotTracked[1].StatusText);
    }

    /// <remarks>
    /// With no mask reported there is nothing to compare against, and claiming "below mask" would
    /// be a judgement made from a number that was never read.
    /// </remarks>
    [Fact]
    public void WithNoMaskNothingIsBelowIt()
    {
        SatellitesViewModel model = Connected(Screen(
            notTracked: [new() { Prn = 3, ElevationDegrees = 4, AzimuthDegrees = 172 }],
            mask: null));

        Assert.False(model.NotTracked[0].IsBelowMask);
    }

    // ---- Counts and empty states -------------------------------------------------------------

    /// <remarks>
    /// The wireframe says "Tracking 6 · Visible 12", but the receiver's second table is headed
    /// "Not Tracking" — summing the two would claim a visibility count the device never made.
    /// </remarks>
    [Fact]
    public void TheHeaderCountsWhatTheReceiverActuallyReports()
    {
        SatellitesViewModel model = Connected(Screen(
            tracked: [new() { Prn = 18 }],
            notTracked: [new() { Prn = 5 }, new() { Prn = 10 }]));

        Assert.Equal("Tracking 1 · not tracking 2", model.CountSummary);
        Assert.Equal(1, model.TrackedCount);
        Assert.Equal(2, model.NotTrackedCount);
    }

    /// <summary>
    /// The three reasons a table is empty are not the same problem, and only one of them is a fault.
    /// </summary>
    [Fact]
    public void EachEmptyReasonSaysSomethingDifferent()
    {
        FakeTimeProvider clock = new(Captured);
        ReceiverStateStore store = new(clock);

        SatellitesViewModel disconnected = new(store);
        Assert.Contains("Connect to a receiver", disconnected.EmptyMessage);

        SatellitesViewModel waiting = new(store) { Connection = ConnectionStatus.Connected };
        Assert.Contains("first full status screen", waiting.EmptyMessage);

        store.UpdateFull(Screen());
        Assert.Contains("antenna", waiting.EmptyMessage);
    }

    /// <remarks>
    /// The satellite table only ever arrives on a full sweep (§7.3), so it is the full sweep's age
    /// that matters. Reporting the fast tier's would say "updated 1 second ago" about a table that
    /// is nine seconds old.
    /// </remarks>
    [Fact]
    public void TheAgeIsTheFullSweepsNotTheFastTiers()
    {
        FakeTimeProvider clock = new(Captured);
        ReceiverStateStore store = new(clock);
        store.UpdateFull(Screen());

        clock.Advance(TimeSpan.FromSeconds(30));
        store.UpdateFast("LOCK", 3, 0, -1.0, 1.0, 1);

        SatellitesViewModel model = new(store) { Connection = ConnectionStatus.Connected };

        Assert.Equal(TimeSpan.FromSeconds(30), model.Age);
    }

    [Fact]
    public void DisconnectedEmptiesTheTables()
    {
        SatellitesViewModel model = Connected(Screen(tracked: [new() { Prn = 18 }]));
        model.Connection = ConnectionStatus.Disconnected;

        Assert.Empty(model.Tracked);
        Assert.Empty(model.NotTracked);
        Assert.Equal("Not connected", model.CountSummary);
        Assert.Null(model.ElevationMaskDegrees);
    }
}
