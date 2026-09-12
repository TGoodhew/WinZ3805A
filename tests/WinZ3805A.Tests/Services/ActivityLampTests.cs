using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// The front-panel Active lamp, and above all what it gives back (#440).
/// </summary>
/// <remarks>
/// The rule these tests exist for is that <b>the receiver owns the lamp and this application
/// borrows it</b>. A flash that assumes the lamp was off turns "the user left it on" into "the
/// application decided it is off", which is the same class of mistake as #320's duration limit and
/// #430's slider: a default that is right by luck is a default nobody checks.
/// </remarks>
public sealed class ActivityLampTests
{
    private static readonly DateTimeOffset Whenever = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The bench receiver, so the session selects the SmartClock driver.</summary>
    private const string Identity = "SYMMETRICOM,Z3805A,3625A02931,1.01.03-A";

    /// <summary>A receiver that answers the lamp and records every write.</summary>
    private sealed class Lamp
    {
        public bool State { get; set; }

        public List<string> Sent { get; } = [];

        public string? Answer(string command)
        {
            Sent.Add(command);

            if (command.StartsWith("*IDN", StringComparison.OrdinalIgnoreCase))
            {
                return Identity;
            }

            if (command.StartsWith(":LED:ENAB?", StringComparison.OrdinalIgnoreCase))
            {
                return State ? "+1" : "+0";
            }

            if (command.StartsWith(":LED:ENAB", StringComparison.OrdinalIgnoreCase))
            {
                State = command.Contains("ON", StringComparison.OrdinalIgnoreCase);
                return string.Empty;
            }

            return string.Empty;
        }
    }

    private static async Task<(DeviceSessionService Session, ActivityLamp Under, Lamp Receiver)> ConnectedAsync()
    {
        Lamp receiver = new();
        DeviceSessionService session = new(
            (_, _) => new ControllableTransport(receiver.Answer) { Banner = Identity },
            new FakeTimeProvider(Whenever));

        await session.ConnectAsync("COM3", SerialSettings.Default);

        return (session, new ActivityLamp(session), receiver);
    }

    [Fact]
    public async Task ArmingLightsTheLamp()
    {
        (DeviceSessionService session, ActivityLamp lamp, Lamp receiver) = await ConnectedAsync();
        await using DeviceSessionService _ = session;

        Assert.True(await lamp.ArmAsync());

        Assert.True(receiver.State);
        Assert.True(lamp.IsLit);
    }

    /// <summary>The whole point: a lamp the user left on is given back on.</summary>
    [Fact]
    public async Task RestoringGivesBackWhatWasFoundRatherThanOff()
    {
        (DeviceSessionService session, ActivityLamp lamp, Lamp receiver) = await ConnectedAsync();
        await using DeviceSessionService _ = session;

        receiver.State = true;

        await lamp.ArmAsync();
        Assert.True(receiver.State);

        await lamp.RestoreAsync();

        Assert.True(receiver.State);
        Assert.False(lamp.IsLit);
    }

    [Fact]
    public async Task ALampFoundOffIsGivenBackOff()
    {
        (DeviceSessionService session, ActivityLamp lamp, Lamp receiver) = await ConnectedAsync();
        await using DeviceSessionService _ = session;

        receiver.State = false;

        await lamp.ArmAsync();
        Assert.True(receiver.State);

        await lamp.RestoreAsync();

        Assert.False(receiver.State);
    }

    /// <summary>
    /// Arming twice must not overwrite the borrowed value with our own.
    /// </summary>
    /// <remarks>
    /// The second arm reads a lamp this application lit, so taking that as the baseline would lose
    /// the user's <c>on</c> for ever — the failure is invisible until the restore, which is the
    /// worst time to find it.
    /// </remarks>
    [Fact]
    public async Task ArmingTwiceDoesNotForgetTheOriginalState()
    {
        (DeviceSessionService session, ActivityLamp lamp, Lamp receiver) = await ConnectedAsync();
        await using DeviceSessionService _ = session;

        receiver.State = true;

        Assert.True(await lamp.ArmAsync());
        Assert.False(await lamp.ArmAsync());

        await lamp.RestoreAsync();

        Assert.True(receiver.State);
    }

    /// <summary>Restoring when nothing was borrowed writes nothing at all.</summary>
    [Fact]
    public async Task RestoringWithoutArmingIsSilent()
    {
        (DeviceSessionService session, ActivityLamp lamp, Lamp receiver) = await ConnectedAsync();
        await using DeviceSessionService _ = session;

        receiver.Sent.Clear();
        await lamp.RestoreAsync();

        Assert.DoesNotContain(receiver.Sent, c => c.StartsWith(":LED:ENAB ", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A manual set hands ownership to the user, so a later restore does not undo it.
    /// </summary>
    [Fact]
    public async Task SettingItByHandEndsTheBorrowing()
    {
        (DeviceSessionService session, ActivityLamp lamp, Lamp receiver) = await ConnectedAsync();
        await using DeviceSessionService _ = session;

        receiver.State = false;
        await lamp.ArmAsync();

        Assert.True(await lamp.SetManuallyAsync(true));
        Assert.False(lamp.IsLit);

        // The user said on. Disconnecting must not decide otherwise.
        await lamp.RestoreAsync();

        Assert.True(receiver.State);
    }

    /// <summary>Both spellings the receiver answers with are understood (#440).</summary>
    [Theory]
    [InlineData("+1", true)]
    [InlineData("+0", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("ON", true)]
    [InlineData("OFF", false)]
    public async Task TheReplyIsNormalisedRatherThanEchoed(string reply, bool expected)
    {
        DeviceSessionService session = new(
            (_, _) => new ControllableTransport(c =>
                c.StartsWith("*IDN", StringComparison.OrdinalIgnoreCase) ? Identity
                : c.StartsWith(":LED:ENAB?", StringComparison.OrdinalIgnoreCase) ? reply
                : string.Empty) { Banner = Identity },
            new FakeTimeProvider(Whenever));
        await using DeviceSessionService _ = session;

        await session.ConnectAsync("COM3", SerialSettings.Default);

        Assert.Equal(expected, await new ActivityLamp(session).ReadAsync());
    }

    /// <summary>An unreadable lamp is left alone, because there would be nothing to give back.</summary>
    [Fact]
    public async Task AnUnreadableLampIsNotLit()
    {
        DeviceSessionService session = new(
            (_, _) => new ControllableTransport(c =>
                c.StartsWith("*IDN", StringComparison.OrdinalIgnoreCase) ? Identity
                : c.StartsWith(":LED:ENAB?", StringComparison.OrdinalIgnoreCase) ? "nonsense"
                : string.Empty) { Banner = Identity },
            new FakeTimeProvider(Whenever));
        await using DeviceSessionService _ = session;

        await session.ConnectAsync("COM3", SerialSettings.Default);
        ActivityLamp lamp = new(session);

        Assert.False(await lamp.ArmAsync());
        Assert.False(lamp.IsLit);
    }

    /// <summary>The command is catalogued, tier S, and carries no §8.3 ceremony.</summary>
    /// <remarks>
    /// A front-panel indicator changes no receiver behaviour, so §8.3's confirmation would be
    /// asking permission to change nothing — and §9.11 gives a safe setter no success toast either.
    /// </remarks>
    [Fact]
    public void TheLampCommandsAreTierSAndUnconfirmed()
    {
        ScpiCommand? write = CommandCatalog.Find(":LED:ENABled");
        ScpiCommand? read = CommandCatalog.Find(":LED:ENAB?");

        Assert.NotNull(write);
        Assert.NotNull(read);

        Assert.Equal(SafetyTier.Safe, write.Tier);
        Assert.False(write.IsQuery);
        Assert.Null(write.ConfirmationText);
        Assert.Null(write.SuccessText);

        Assert.Equal(SafetyTier.Safe, read.Tier);
        Assert.True(read.IsQuery);
    }
}
