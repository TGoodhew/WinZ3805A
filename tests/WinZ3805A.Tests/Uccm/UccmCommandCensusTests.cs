using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Drivers.Uccm;

namespace WinZ3805A.Tests.Uccm;

/// <summary>
/// The eight commands measured present on a Trimble UCCM-P, 13 Sep 2026 (#416).
/// </summary>
/// <remarks>
/// <para>
/// Every one is in Lady Heather's poll cycle and none was in this catalog, because nobody had asked
/// the module whether it had them. All eight answered with a value. The census also asked a node
/// known to exist (<c>SYNC:TINT?</c>) and one known not to (<c>:GPS:POS:SURV:STAT?</c>, which
/// answers <c>Undefined header</c>), and both behaved — so "this firmware answers anything" is
/// ruled out, which is what makes the eight positives mean something.
/// </para>
/// <para>
/// <b>Catalogued is not polled.</b> What a receiver answers and what the application asks every
/// second are different decisions, and none of these is in a poll plan. §8.1's allowlist governs
/// what may be <i>sent</i>; being in it makes them available to the Advanced Console's picker and
/// to <c>Capture-Uccm.ps1</c>, which reads this list since #482.
/// </para>
/// </remarks>
public sealed class UccmCommandCensusTests
{
    public static TheoryData<string> Measured =>
    [
        UccmCommands.Position,
        UccmCommands.IgnoredSatellites,
        UccmCommands.ElevationMask,
        UccmCommands.AntennaDelay,
        UccmCommands.PulseSelect,
        UccmCommands.OutputState,
        UccmCommands.EfcData,
        UccmCommands.PullInRange,
    ];

    [Theory]
    [MemberData(nameof(Measured))]
    public void EachIsInTheCatalogAsASafeQuery(string mnemonic)
    {
        ScpiCommand? command = UccmCommands.Find(mnemonic);

        Assert.NotNull(command);
        Assert.True(command.IsQuery, $"{mnemonic} is a read and must be catalogued as one.");
        Assert.Equal(SafetyTier.Safe, command.Tier);
    }

    [Theory]
    [MemberData(nameof(Measured))]
    public void NoneIsPolled(string mnemonic)
    {
        // Deliberate. Every one of these answers, and that is not a reason to ask it every second:
        // the sweep is already four queries and the readings these carry either duplicate the status
        // screen or have no home in the common currency yet.
        UccmDriver driver = new(TimeProvider.System);

        Assert.DoesNotContain(mnemonic, driver.Plan.FastTier);
        Assert.NotEqual(mnemonic, driver.Plan.FullStatus);
    }

    [Fact]
    public void ThePullInRangeSpellingIsPreservedExactly()
    {
        // Alone in this catalog it carries no subsystem and no leading colon, and the module answers
        // it. A tidy-up to `:DIAG:ROSC:PULLINRANGE?` would be an invention: the wire fact is what was
        // sent and what came back, and the same reasoning keeps the leading colons on the three
        // UCCM-P queries.
        Assert.Equal("PULLINRANGE?", UccmCommands.PullInRange);
        Assert.NotNull(UccmCommands.Find("PULLINRANGE?"));
    }

    [Fact]
    public void TheCatalogRemainsReadsOnly()
    {
        // The census added eight entries and none of them may have made this false. Heather sends
        // writes to these modules - output enable and disable, the pulse selector, an EFC setter
        // whose own comment says it "causes a recovery state" - and each needs its own §8 tier
        // ruling rather than arriving as a side effect of a catalogue sweep.
        Assert.All(UccmCommands.All, command =>
        {
            Assert.True(command.IsQuery, $"{command.Mnemonic} is not a query.");
            Assert.Equal(SafetyTier.Safe, command.Tier);
        });
    }
}
