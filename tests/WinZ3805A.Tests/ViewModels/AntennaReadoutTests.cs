using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Drivers.Nmea;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// The antenna line on §10.4's health card (#515) — the one health fact a talker has.
/// </summary>
/// <remarks>
/// #435's audit said the health card "has nothing to show" for a talker, which was true: no
/// oscillator, no self test, no health monitor. A u-blox module does supervise its antenna, and a
/// shorted or missing one is the most useful health fact a timing receiver has.
/// </remarks>
public sealed class AntennaReadoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 2, 0, 0, TimeSpan.Zero);

    private static string Sentence(string body)
    {
        byte checksum = 0;
        foreach (char c in body)
        {
            checksum ^= (byte)c;
        }

        return $"${body}*{checksum:X2}";
    }

    private static OverviewViewModel Showing(string? antennaWord)
    {
        ReceiverStateStore store = new(new FakeTimeProvider(Now));

        if (antennaWord is not null)
        {
            string cycle = string.Join('\n',
            [
                Sentence($"GNTXT,01,01,02,ANTSTATUS={antennaWord}"),
                Sentence("GNRMC,015954.00,A,4731.31483,N,12212.36635,W,0.02,,080926,,,A"),
            ]);
            store.UpdateFull(NmeaStatusParser.Parse(cycle, Now), FastFields.None);
        }
        else
        {
            store.UpdateFull(new ReceiverStatus { CapturedAt = Now }, FastFields.None);
        }

        return new OverviewViewModel(store, new NmeaDriver(new FakeTimeProvider(Now))) { Connection = ConnectionStatus.Connected };
    }

    [Theory]
    [InlineData("OK", "Antenna OK", true)]
    [InlineData("INIT", "Antenna starting up", true)]
    [InlineData("SHORT", "Antenna short circuit", false)]
    [InlineData("OPEN", "Antenna open circuit — none detected", false)]
    public void EachStateReadsAsWordsAndCarriesItsSeverity(string word, string expected, bool ok)
    {
        OverviewViewModel model = Showing(word);

        Assert.Equal(expected, model.AntennaText);
        Assert.Equal(ok, model.AntennaOk);
    }

    /// <summary>
    /// A receiver that says it cannot tell is shown saying so. That is a different statement from
    /// never having mentioned an antenna, and not conflating the two is §9.11's whole subject.
    /// </summary>
    [Fact]
    public void AModuleThatCannotTellSaysSoRatherThanVanishing()
    {
        OverviewViewModel model = Showing("DONTKNOW");

        Assert.Equal("Antenna not supervised", model.AntennaText);
        Assert.True(model.AntennaOk);
    }

    /// <summary>A receiver that never mentioned an antenna shows no line at all.</summary>
    [Fact]
    public void AReceiverThatSaidNothingShowsNothing()
    {
        Assert.Null(Showing(null).AntennaText);
    }

    /// <summary>
    /// Disconnected shows nothing, because the reading belongs to a link that is no longer there.
    /// </summary>
    [Fact]
    public void NothingIsShownWhileDisconnected()
    {
        ReceiverStateStore store = new(new FakeTimeProvider(Now));
        string cycle = string.Join('\n',
        [
            Sentence("GNTXT,01,01,02,ANTSTATUS=OK"),
            Sentence("GNRMC,015954.00,A,4731.31483,N,12212.36635,W,0.02,,080926,,,A"),
        ]);
        store.UpdateFull(NmeaStatusParser.Parse(cycle, Now), FastFields.None);

        OverviewViewModel model = new(store, new NmeaDriver(new FakeTimeProvider(Now))) { Connection = ConnectionStatus.Disconnected };

        Assert.Null(model.AntennaText);
    }

    /// <summary>
    /// It survives the cycles that carry no banner — which is all but one of them, and the reason
    /// the store remembers it at all (#515).
    /// </summary>
    [Fact]
    public void ItSurvivesTheSilentCyclesThatFollow()
    {
        ReceiverStateStore store = new(new FakeTimeProvider(Now));
        string cycle = string.Join('\n',
        [
            Sentence("GNTXT,01,01,02,ANTSTATUS=SHORT"),
            Sentence("GNRMC,015954.00,A,4731.31483,N,12212.36635,W,0.02,,080926,,,A"),
        ]);
        store.UpdateFull(NmeaStatusParser.Parse(cycle, Now), FastFields.None);

        for (int i = 0; i < 5; i++)
        {
            store.UpdateFull(new ReceiverStatus { CapturedAt = Now }, FastFields.None);
        }

        OverviewViewModel model = new(store, new NmeaDriver(new FakeTimeProvider(Now))) { Connection = ConnectionStatus.Connected };

        Assert.Equal("Antenna short circuit", model.AntennaText);
        Assert.False(model.AntennaOk);
    }
}
