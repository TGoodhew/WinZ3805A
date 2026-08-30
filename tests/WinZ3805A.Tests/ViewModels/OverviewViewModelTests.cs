using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Controls;
using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// The §10.4 Overview page's judgements.
/// </summary>
public sealed class OverviewViewModelTests
{

    /// <summary>The shipped driver, which owns §10.3's token table since #304.</summary>
    private static SmartClockDriver SmartClock() => new(TimeProvider.System);
    private static readonly DateTimeOffset Captured = new(2026, 8, 13, 19, 0, 0, TimeSpan.Zero);

    private static (OverviewViewModel Model, ReceiverStateStore Store) Connected(
        ReceiverStatus? status = null,
        string syncState = "LOCK")
    {
        FakeTimeProvider clock = new(Captured);
        ReceiverStateStore store = new(clock);

        if (status is not null)
        {
            store.UpdateFull(status);
        }

        store.UpdateFast(syncState, tfom: 3, ffom: 0, onePpsTiNanoseconds: -33.1,
            oscillatorControl: 4.2, trackedCount: 6);

        return (new OverviewViewModel(store, SmartClock()) { Connection = ConnectionStatus.Connected }, store);
    }

    private static ReceiverStatus Status(Action<ReceiverStatusBuilder>? configure = null)
    {
        ReceiverStatusBuilder builder = new();
        configure?.Invoke(builder);
        return builder.Build();
    }

    private sealed class ReceiverStatusBuilder
    {
        public OutputValidity Outputs { get; set; } = OutputValidity.Valid;

        public double? Predicted { get; set; } = 2.7e-6;

        public double? Threshold { get; set; } = 1.0e-6;

        public double? Present { get; set; }

        public TimeSpan? Duration { get; set; }

        public IReadOnlyDictionary<string, bool> Health { get; set; } =
            new Dictionary<string, bool> { ["Self Test"] = true, ["Oven Pwr"] = true };

        public ReceiverStatus Build() => new()
        {
            Outputs = Outputs,
            HoldoverPredictedSeconds = Predicted,
            HoldThresholdSeconds = Threshold,
            HoldoverPresentSeconds = Present,
            HoldoverDuration = Duration,
            HealthItems = Health,
            HealthOk = Health.Values.All(ok => ok),
            CapturedAt = Captured,
        };
    }

    // ---- Outputs ---------------------------------------------------------------------------

    /// <remarks>
    /// "Valid, reduced accuracy" is a caution, never a success. The outputs are usable but the
    /// accuracy specification no longer holds, and a green badge over that is something a lab user
    /// would act on.
    /// </remarks>
    [Theory]
    [InlineData(OutputValidity.Valid, Severity.Success, "Outputs valid")]
    [InlineData(OutputValidity.ValidReduced, Severity.Caution, "Outputs valid, reduced accuracy")]
    [InlineData(OutputValidity.Invalid, Severity.Critical, "Outputs invalid")]
    [InlineData(OutputValidity.Unknown, Severity.Neutral, "Outputs unknown")]
    public void TheOutputsBadgeCarriesSeverityAndText(OutputValidity validity, Severity severity, string text)
    {
        (OverviewViewModel model, _) = Connected(Status(b => b.Outputs = validity));

        Assert.Equal(severity, model.OutputsSeverity);
        Assert.Equal(text, model.OutputsText);
    }

    // ---- Figures of merit ------------------------------------------------------------------

    /// <remarks>
    /// The §10.4 wireframe annotates TFOM 3 as "100ns-1µs", which the 58503A guide's own table
    /// confirms (#34). The number alone says nothing; this caption is why it is on the page.
    /// </remarks>
    [Fact]
    public void TheTfomCaptionIsTheDocumentedTimeError()
    {
        (OverviewViewModel model, _) = Connected();

        Assert.Equal(3, model.Tfom);
        Assert.Equal("100 ns – 1 µs", model.TfomDetail);
    }

    [Fact]
    public void TheFfomCaptionIsTheDocumentedPllState()
    {
        (OverviewViewModel model, _) = Connected();

        Assert.Equal("PLL stabilized", model.FfomDetail);
        Assert.Contains("within specification", model.FfomTooltip);
    }

    /// <remarks>
    /// FFOM 2 and 3 are both "PLL unlocked" and are not interchangeable — 3 is the one the guide
    /// answers with "do not use the output". Collapsing them would hide the only FFOM value that
    /// tells a user to stop trusting their measurement.
    /// </remarks>
    [Fact]
    public void TheTwoUnlockedFfomValuesAreNotTheSame()
    {
        Assert.NotEqual(FiguresOfMerit.PllState(2), FiguresOfMerit.PllState(3));
        Assert.Contains("holdover", FiguresOfMerit.PllState(2));
        Assert.Contains("do not use", FiguresOfMerit.PllState(3), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(null)]
    public void AnUnknownMeritHasNoCaption(int? ffom) =>
        Assert.True(ffom is 0 ? FiguresOfMerit.PllState(ffom) is not null : FiguresOfMerit.PllState(ffom) is null);

    // ---- Holdover --------------------------------------------------------------------------

    /// <remarks>
    /// The receiver reports both in seconds — 2.7E-006 beside 1.0E-006 — and nobody reads those and
    /// sees that one is nearly three times the other.
    /// </remarks>
    [Fact]
    public void HoldoverFiguresAreShownInEngineeringUnits()
    {
        (OverviewViewModel model, _) = Connected(Status());

        Assert.Equal(("2.7", "µs"), model.HoldoverPredicted);
        Assert.Equal(("1.000", "µs"), model.HoldoverThreshold);
    }

    /// <remarks>
    /// "Not in holdover" and "—" are different statements. The first is the answer for a locked
    /// receiver; the second means the field could not be read (§11.1).
    /// </remarks>
    [Fact]
    public void ALockedReceiverSaysItIsNotInHoldoverRatherThanShowingADash()
    {
        (OverviewViewModel model, _) = Connected(Status());

        Assert.Equal("Not in holdover", model.HoldoverDuration);
    }

    /// <remarks>
    /// This asserted the present <i>uncertainty</i> until #319 — 4.2 µs under a row labelled
    /// "Duration", a different quantity in a different unit from the one the label names, while the
    /// Holdover page beside it showed the real duration from the same field.
    /// </remarks>
    [Fact]
    public void InHoldoverTheDurationRowCarriesHowLongItHasLasted()
    {
        (OverviewViewModel model, _) = Connected(
            Status(b =>
            {
                b.Present = 4.2e-6;
                b.Duration = TimeSpan.FromSeconds(694);
            }),
            syncState: "HOLD");

        Assert.Equal(ReceiverMode.Holdover, model.Mode);
        Assert.Equal(Staleness.Describe(TimeSpan.FromSeconds(694)), model.HoldoverDuration);

        // Emphatically not the uncertainty, which is what it used to show.
        Assert.DoesNotContain("4.2", model.HoldoverDuration, StringComparison.Ordinal);
        Assert.DoesNotContain("µs", model.HoldoverDuration, StringComparison.Ordinal);
    }

    /// <summary>A receiver in holdover that does not print the duration shows the §11.1 dash.</summary>
    [Fact]
    public void InHoldoverWithNoDurationReportedTheRowIsADash()
    {
        (OverviewViewModel model, _) = Connected(Status(b => b.Present = 4.2e-6), syncState: "HOLD");

        Assert.Equal(ReadoutFormatter.NoValue, model.HoldoverDuration);
    }

    // ---- Health ----------------------------------------------------------------------------

    [Fact]
    public void HealthItemsComeFromTheReceiverInItsOwnOrder()
    {
        (OverviewViewModel model, _) = Connected(Status(b => b.Health = new Dictionary<string, bool>
        {
            ["Self Test"] = true,
            ["Internal Pwr"] = false,
            ["Oven Pwr"] = true,
        }));

        Assert.Equal(["Self Test", "Internal Pwr", "Oven Pwr"], model.Health.Select(item => item.Name));
        Assert.Equal(Severity.Critical, model.Health[1].Severity);
        Assert.Equal("1 check failing", model.HealthSummary);
        Assert.False(model.HealthOk);
    }

    [Fact]
    public void AllPassingSaysSo()
    {
        (OverviewViewModel model, _) = Connected(Status());

        Assert.Equal("All checks passing", model.HealthSummary);
        Assert.True(model.HealthOk);
    }

    // ---- Disconnected ----------------------------------------------------------------------

    /// <remarks>
    /// Disconnected is not "everything is zero". Every reading becomes null and the page shows the
    /// §11.1 dash, because the last thing a receiver said is not what it is saying now once the
    /// link is gone.
    /// </remarks>
    [Fact]
    public void DisconnectedEmptiesEveryReading()
    {
        (OverviewViewModel model, _) = Connected(Status());
        model.Connection = ConnectionStatus.Disconnected;

        Assert.Equal(ReceiverMode.Disconnected, model.Mode);
        Assert.Null(model.Tfom);
        Assert.Null(model.Ffom);
        Assert.Null(model.TimeIntervalNanoseconds);
        Assert.Null(model.OscillatorControl);
        Assert.Equal(Severity.Neutral, model.OutputsSeverity);
        Assert.Equal(ReadoutFormatter.NoValue, model.HoldoverPredicted.Value);
        Assert.Equal(ReadoutFormatter.NoValue, model.HoldoverDuration);
        Assert.Empty(model.Health);
        Assert.Equal("No health data", model.HealthSummary);
    }

    /// <remarks>
    /// The §10.3 diagnostic, repeated on this page because it is the one condition a user most
    /// needs to see and the Overview page is where they will be looking.
    /// </remarks>
    [Fact]
    public void LockedWithNoSatellitesIsCoasting()
    {
        FakeTimeProvider clock = new(Captured);
        ReceiverStateStore store = new(clock);
        store.UpdateFast("LOCK", 3, 0, -33.1, 4.2, trackedCount: 0);

        OverviewViewModel model = new(store, SmartClock()) { Connection = ConnectionStatus.Connected };

        Assert.True(model.IsCoasting);
    }

    [Fact]
    public void TheStoreDrivesTheViewModel()
    {
        FakeTimeProvider clock = new(Captured);
        ReceiverStateStore store = new(clock);
        OverviewViewModel model = new(store, SmartClock()) { Connection = ConnectionStatus.Connected };

        int raised = 0;
        model.PropertyChanged += (_, _) => raised++;

        store.UpdateFast("LOCK", 4, 1, -12.0, 1.0, 5);

        Assert.True(raised > 0);
        Assert.Equal(4, model.Tfom);
    }

    // ---- Identity (P0-1) ---------------------------------------------------------------------

    /// <remarks>
    /// The live receiver's own answer, which is also the string every fixture's provenance is
    /// recorded against.
    /// </remarks>
    private const string LiveIdentity = "SYMMETRICOM,Z3805A,3625A02931,1.01.03-A";

    /// <summary>P0-1: the four fields the receiver answered are on screen, not only in the log.</summary>
    [Fact]
    public void TheIdentityIsBrokenIntoItsFourFields()
    {
        (OverviewViewModel model, _) = Connected(Status());
        model.Identity = DeviceIdentity.Parse(LiveIdentity);
        model.RawIdentity = LiveIdentity;

        Assert.Equal("Z3805A", model.IdentityModel);
        Assert.Equal("SYMMETRICOM", model.IdentityManufacturer);
        Assert.Equal("3625A02931", model.IdentitySerialNumber);
        Assert.Equal("1.01.03-A", model.IdentityFirmware);

        // Parsed, so the raw line is not shown as well — that would be the same text twice.
        Assert.Null(model.UnparsedIdentity);
    }

    /// <summary>
    /// A receiver whose answer is not four fields shows what it did say, rather than four dashes.
    /// </summary>
    /// <remarks>
    /// Four dashes read as "nothing is connected", which is a different statement from "a model
    /// this build has not seen before". §11.1's rule is that an unreadable field is absent, not
    /// that the evidence is discarded — and the raw string still identifies the unit to a person.
    /// </remarks>
    [Fact]
    public void AnIdentityThatDoesNotParseIsStillShown()
    {
        (OverviewViewModel model, _) = Connected(Status());
        model.Identity = null;
        model.RawIdentity = "ACME GPSDO v2";

        Assert.Equal(ReadoutFormatter.NoValue, model.IdentityModel);
        Assert.Equal("ACME GPSDO v2", model.UnparsedIdentity);
    }

    /// <summary>Disconnected, the identity is a claim about a link that is not there.</summary>
    [Fact]
    public void ADisconnectedSessionShowsNoIdentity()
    {
        (OverviewViewModel model, _) = Connected(Status());
        model.Identity = DeviceIdentity.Parse(LiveIdentity);
        model.RawIdentity = LiveIdentity;

        model.Connection = ConnectionStatus.Disconnected;

        Assert.Equal(ReadoutFormatter.NoValue, model.IdentityModel);
        Assert.Equal(ReadoutFormatter.NoValue, model.IdentitySerialNumber);
        Assert.Null(model.UnparsedIdentity);
    }

    /// <summary>Setting it raises, so the card repaints when a reconnect finds another receiver.</summary>
    [Fact]
    public void ChangingTheIdentityRaises()
    {
        (OverviewViewModel model, _) = Connected(Status());
        int raised = 0;
        model.PropertyChanged += (_, _) => raised++;

        model.Identity = DeviceIdentity.Parse(LiveIdentity);

        Assert.True(raised > 0);
        Assert.Equal("Z3805A", model.IdentityModel);
    }
}
