using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Models;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// The §10.6 Position page's judgements about what may be commanded.
/// </summary>
public sealed class PositionViewModelTests
{
    private static readonly DateTimeOffset Captured = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static PositionViewModel Connected(
        PositionMode mode = PositionMode.Hold,
        HeightDatum datum = HeightDatum.Msl,
        GeoPosition? position = null)
    {
        FakeTimeProvider clock = new(Captured);
        ReceiverStateStore store = new(clock);

        store.UpdateFull(new ReceiverStatus
        {
            PositionMode = mode,
            HeightDatum = datum,
            Position = position ?? new GeoPosition
            {
                LatitudeDegrees = 47.5219,
                LongitudeDegrees = -122.2061,
                HeightMetres = 38.0,
            },
            CapturedAt = Captured,
        });

        return new PositionViewModel(store) { Connection = ConnectionStatus.Connected };
    }

    // -------------------------------------------------------------------------------------
    // §10.6's survey commands
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Starting and ending a survey are never both on offer. §8.3 words the two end commands for a
    /// survey in progress — "stop surveying and adopt", "cancel survey and restore" — and offers no
    /// restart for one that is not running.
    /// </summary>
    [Theory]
    [InlineData(PositionMode.Hold, true, false)]
    [InlineData(PositionMode.Survey, false, true)]
    public void TheSurveyButtonsFollowTheReceiversMode(PositionMode mode, bool canStart, bool canEnd)
    {
        PositionViewModel model = Connected(mode);

        Assert.Equal(canStart, model.CanStartSurvey);
        Assert.Equal(canEnd, model.CanEndSurvey);
    }

    /// <summary>Nothing is offered to a receiver that is not there.</summary>
    [Fact]
    public void NoCommandIsOfferedWhileDisconnected()
    {
        PositionViewModel model = Connected(PositionMode.Survey);
        model.Connection = ConnectionStatus.Disconnected;

        Assert.False(model.CanStartSurvey);
        Assert.False(model.CanEndSurvey);
        Assert.False(model.CanSetPosition);
    }

    /// <summary>
    /// The power-up setting starts unread, and unread is a third state rather than "off" — a
    /// cleared checkbox is an answer a user would act on, and the page does not have one yet.
    /// </summary>
    [Fact]
    public void TheSurveyPowerUpSettingStartsUnknown() =>
        Assert.Null(Connected().SurveyAtPowerUp);

    /// <summary>Once read, it says so, and says which way.</summary>
    [Fact]
    public void TheSurveyPowerUpSettingHoldsWhatWasRead()
    {
        PositionViewModel model = Connected();

        model.SurveyAtPowerUp = true;
        Assert.True(model.SurveyAtPowerUp);

        model.SurveyAtPowerUp = false;
        Assert.False(model.SurveyAtPowerUp);
    }

    // -------------------------------------------------------------------------------------
    // #114 — the height datum
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// §10.6 labels the entry field "WGS-84, GPS ellipsoid" while the manual's own syntax line for
    /// the same command says "height above mean sea level", two paragraphs from its prose saying
    /// WGS-84. The two differ by the geoid separation, which is tens of metres. So the page asserts
    /// neither — it repeats what the receiver said it was reporting.
    /// </summary>
    [Theory]
    [InlineData(HeightDatum.Msl, "mean sea level")]
    [InlineData(HeightDatum.GpsEllipsoid, "WGS-84 ellipsoid")]
    public void TheHeightNoteRepeatsTheReceiversOwnDatum(HeightDatum datum, string expected) =>
        Assert.Contains(expected, Connected(datum: datum).HeightEntryNote, StringComparison.Ordinal);

    /// <summary>And admits it plainly when the receiver has not said.</summary>
    [Fact]
    public void TheHeightNoteAdmitsWhenTheDatumIsUnknown() =>
        Assert.Contains(
            "has not said",
            Connected(datum: HeightDatum.Unknown).HeightEntryNote,
            StringComparison.Ordinal);

    /// <summary>
    /// The entry card copies from here rather than reaching past the view model for the parsed
    /// screen.
    /// </summary>
    [Fact]
    public void TheReceiversPositionIsAvailableToCopyFrom()
    {
        GeoPosition? position = Connected().ReceiverPosition;

        Assert.NotNull(position);
        Assert.Equal(47.5219, position.LatitudeDegrees);
        Assert.Equal(-122.2061, position.LongitudeDegrees);
        Assert.Equal(38.0, position.HeightMetres);
    }
}
