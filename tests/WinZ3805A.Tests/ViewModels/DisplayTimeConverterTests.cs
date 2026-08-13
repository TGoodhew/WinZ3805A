using WinZ3805A.Device.Models;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// Rendering a device-reported instant in the user's zone (#95).
/// </summary>
/// <remarks>
/// The date boundary is the reason this is tested rather than eyeballed. An hour of difference is
/// obvious and easily discounted; a date a whole day out near local midnight is not, and is exactly
/// what someone glancing at a window on a second monitor will misread.
/// </remarks>
public class DisplayTimeConverterTests
{
    /// <summary>A fixed +2 zone, so the test does not depend on the machine it runs on.</summary>
    private static TimeZoneInfo PlusTwo => TimeZoneInfo.CreateCustomTimeZone(
        "Test/Plus2", TimeSpan.FromHours(2), "Test +2", "Test +2");

    private static TimeZoneInfo MinusFive => TimeZoneInfo.CreateCustomTimeZone(
        "Test/Minus5", TimeSpan.FromHours(-5), "Test -5", "Test -5");

    /// <summary>
    /// The acceptance case: 23:30 UTC is 01:30 the *next day* at +2. Getting the hour right and the
    /// date wrong would be worse than doing nothing.
    /// </summary>
    [Fact]
    public void ConvertingAcrossMidnightMovesTheDateAsWellAsTheTime()
    {
        DateTimeOffset reported = new(2026, 8, 12, 23, 30, 0, TimeSpan.Zero);

        DisplayTime? shown = DisplayTimeConverter.Convert(reported, TimeScale.Utc, PlusTwo);

        Assert.NotNull(shown);
        Assert.Equal(1, shown.Value.Value.Hour);
        Assert.Equal(30, shown.Value.Value.Minute);
        Assert.Equal(13, shown.Value.Value.Day);
        Assert.True(shown.Value.WasConverted);
    }

    [Fact]
    public void ConvertingBackwardsAcrossMidnightMovesTheDateToo()
    {
        DateTimeOffset reported = new(2026, 8, 13, 2, 15, 0, TimeSpan.Zero);

        DisplayTime? shown = DisplayTimeConverter.Convert(reported, TimeScale.Utc, MinusFive);

        Assert.NotNull(shown);
        Assert.Equal(21, shown.Value.Value.Hour);
        Assert.Equal(12, shown.Value.Value.Day);
    }

    [Fact]
    public void ShowingUtcInUtcIsNotReportedAsAConversion()
    {
        DateTimeOffset reported = new(2026, 8, 12, 23, 30, 0, TimeSpan.Zero);

        DisplayTime? shown = DisplayTimeConverter.Convert(reported, TimeScale.Utc, TimeZoneInfo.Utc);

        Assert.NotNull(shown);
        Assert.False(shown.Value.WasConverted);
        Assert.Equal("UTC", shown.Value.ZoneLabel);
        Assert.Equal(23, shown.Value.Value.Hour);
    }

    /// <summary>
    /// A receiver already set to a local scale applied an offset it does not report, so the instant
    /// behind the value is unknown. Converting would be arithmetic on a number whose meaning we do
    /// not have, and would land a second offset on top of the first.
    /// </summary>
    [Theory]
    [InlineData(TimeScale.Local)]
    [InlineData(TimeScale.LocalGps)]
    public void AReceiverOnItsOwnLocalScaleIsShownAsGivenRatherThanConvertedTwice(TimeScale scale)
    {
        DateTimeOffset reported = new(2026, 8, 12, 23, 30, 0, TimeSpan.Zero);

        DisplayTime? shown = DisplayTimeConverter.Convert(reported, scale, PlusTwo);

        Assert.NotNull(shown);
        Assert.False(shown.Value.WasConverted);
        Assert.Equal(23, shown.Value.Value.Hour);
        Assert.Equal(12, shown.Value.Value.Day);
        Assert.Contains("local", shown.Value.ZoneLabel, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An unparsed scale is labelled honestly rather than assumed to be UTC.</summary>
    [Fact]
    public void AnUnknownScaleIsNotAssumedToBeUtc()
    {
        DateTimeOffset reported = new(2026, 8, 12, 23, 30, 0, TimeSpan.Zero);

        DisplayTime? shown = DisplayTimeConverter.Convert(reported, TimeScale.Unknown, PlusTwo);

        Assert.NotNull(shown);
        Assert.False(shown.Value.WasConverted);
        Assert.Equal(23, shown.Value.Value.Hour);
    }

    /// <summary>GPS time is not UTC — it carries no leap seconds — so it keeps its own label.</summary>
    [Fact]
    public void GpsTimeKeepsItsOwnLabelWhenShownAtZeroOffset()
    {
        DateTimeOffset reported = new(2026, 8, 12, 23, 30, 0, TimeSpan.Zero);

        DisplayTime? shown = DisplayTimeConverter.Convert(reported, TimeScale.Gps, TimeZoneInfo.Utc);

        Assert.NotNull(shown);
        Assert.Equal("GPS", shown.Value.ZoneLabel);
    }

    [Fact]
    public void NothingReportedShowsNothing()
    {
        Assert.Null(DisplayTimeConverter.Convert(null, TimeScale.Utc, PlusTwo));
    }

    /// <summary>
    /// §7.4's correction settles which epoch the instant belongs to; the zone settles how to render
    /// it. Doing them in the other order would convert a date two decades out and then move it.
    /// </summary>
    [Fact]
    public void TheRolloverCorrectionSurvivesTheZoneConversion()
    {
        DateTimeOffset corrected = new(2026, 8, 12, 23, 30, 0, TimeSpan.Zero);

        DisplayTime? shown = DisplayTimeConverter.Convert(corrected, TimeScale.Utc, PlusTwo);

        Assert.NotNull(shown);
        Assert.Equal(2026, shown.Value.Value.Year);
        Assert.Equal(13, shown.Value.Value.Day);
    }
}
