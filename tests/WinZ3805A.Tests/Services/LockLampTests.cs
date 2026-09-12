using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// The Active lamp following the receiver's lock state (#462).
/// </summary>
/// <remarks>
/// <para>
/// The panel has six indicators and exactly two belong to the host software. Until #462 both of
/// ours said the same thing — "the application is connected" — which wasted one of them. Now
/// <c>ActivityLamp</c> holds <b>Enabled</b> for the application and this holds <b>Active</b> for the
/// receiver, so a person in front of a rack can tell "software is attached to this one" from "this
/// one is locked".
/// </para>
/// <para>
/// <b>What makes it affordable is the cadence of the thing shown, not a cheaper write.</b> A
/// <c>:LED:</c> write still costs about a second on the receiver's 1 Hz tick, and §8's finding that
/// a flash per sweep is 194 % of the poll budget is untouched. A locked receiver stays locked for
/// hours, so this is one write per transition.
/// </para>
/// </remarks>
public sealed class LockLampTests
{
    private static readonly DateTimeOffset Whenever = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    private const string Identity = "SYMMETRICOM,Z3805A,3625A02931,1.01.03-A";

    /// <summary>A receiver that answers the Active lamp and records every write.</summary>
    private sealed class Panel
    {
        public bool State { get; set; }

        public List<string> Sent { get; } = [];

        public int Writes => Sent.Count(c =>
            c.StartsWith(":LED:ACT ", StringComparison.OrdinalIgnoreCase)
            || c.StartsWith(":LED:ACTive ", StringComparison.OrdinalIgnoreCase));

        public string? Answer(string command)
        {
            Sent.Add(command);

            if (command.StartsWith("*IDN", StringComparison.OrdinalIgnoreCase))
            {
                return Identity;
            }

            if (command.StartsWith(":LED:ACT?", StringComparison.OrdinalIgnoreCase))
            {
                return State ? "+1" : "+0";
            }

            if (command.StartsWith(":LED:ACT", StringComparison.OrdinalIgnoreCase))
            {
                State = command.Contains("ON", StringComparison.OrdinalIgnoreCase);
                return string.Empty;
            }

            return string.Empty;
        }
    }

    private static async Task<(DeviceSessionService Session, ReceiverStateStore Store, LockLamp Under, Panel Receiver)> ConnectedAsync()
    {
        Panel receiver = new();
        FakeTimeProvider clock = new(Whenever);
        DeviceSessionService session = new(
            (_, _) => new ControllableTransport(receiver.Answer) { Banner = Identity },
            clock);

        await session.ConnectAsync("COM3", SerialSettings.Default);

        ReceiverStateStore store = new(clock);
        return (session, store, new LockLamp(session, store), receiver);
    }

    /// <summary>
    /// Drives the store to a lock state and waits for the lamp to catch up.
    /// </summary>
    /// <remarks>
    /// The follow is fire-and-forget by design — a lamp must never hold the poll loop up — so a test
    /// has to wait for the write rather than assume it has landed. It <b>polls for the expected
    /// state</b> rather than sleeping a fixed time: a fixed wait is either too short on a loaded
    /// runner or too slow on every run, and this file learned that the second way at eighteen
    /// seconds for six tests.
    /// </remarks>
    private static async Task SetLockAsync(ReceiverStateStore store, bool locked, Panel receiver, bool expect)
    {
        store.UpdateFast(locked ? "LOCK" : "HOLD", 3, 0, 2.8, 47.5, 8, FastFields.All);

        for (int attempt = 0; attempt < 400 && receiver.State != expect; attempt++)
        {
            await Task.Delay(5);
        }

        Assert.Equal(expect, receiver.State);
    }

    /// <summary>
    /// Drives the store and waits long enough that a write would have been seen.
    /// </summary>
    /// <remarks>
    /// For the case that asserts <i>nothing</i> happens, where there is no state to poll for. Kept
    /// short because the assertion it serves is about a write that is not made at all rather than
    /// one that is late.
    /// </remarks>
    private static async Task SetLockExpectingNoWriteAsync(ReceiverStateStore store, bool locked)
    {
        store.UpdateFast(locked ? "LOCK" : "HOLD", 3, 0, 2.8, 47.5, 8, FastFields.All);
        await Task.Delay(40);
    }

    [Fact]
    public async Task ALockedReceiverLightsItAndLosingLockPutsItOut()
    {
        (DeviceSessionService session, ReceiverStateStore store, LockLamp lamp, Panel receiver) = await ConnectedAsync();
        await using DeviceSessionService _ = session;
        using LockLamp __ = lamp;

        Assert.True(await lamp.ArmAsync());

        await SetLockAsync(store, locked: true, receiver, expect: true);
        await SetLockAsync(store, locked: false, receiver, expect: false);
    }

    /// <summary>
    /// THE COST ARGUMENT, AS A TEST. A state that has not changed costs no wire time.
    /// </summary>
    /// <remarks>
    /// This is the whole difference between what #462 chose and the per-sweep flash #440 rejected on
    /// cost. If an unchanged state wrote, a locked receiver would spend a second of every second on
    /// a lamp saying the same thing, and the decision would have been the one already ruled out.
    /// </remarks>
    [Fact]
    public async Task AnUnchangedLockStateWritesNothing()
    {
        (DeviceSessionService session, ReceiverStateStore store, LockLamp lamp, Panel receiver) = await ConnectedAsync();
        await using DeviceSessionService _ = session;
        using LockLamp __ = lamp;

        await lamp.ArmAsync();
        await SetLockAsync(store, locked: true, receiver, expect: true);

        int after = receiver.Writes;

        for (int sweep = 0; sweep < 5; sweep++)
        {
            await SetLockExpectingNoWriteAsync(store, locked: true);
        }

        Assert.Equal(after, receiver.Writes);
    }

    /// <summary>
    /// #440's rule, and the wrinkle #462 adds to it: the baseline is what was READ, not "off".
    /// </summary>
    /// <remarks>
    /// A user who left Active on gets it back on, even though this session spent its life turning it
    /// off and on to follow the lock state. Extinguishing it instead would be the application
    /// deciding what the user's panel should look like.
    /// </remarks>
    [Fact]
    public async Task ALampLeftOnIsGivenBackOn()
    {
        (DeviceSessionService session, ReceiverStateStore store, LockLamp lamp, Panel receiver) = await ConnectedAsync();
        await using DeviceSessionService _ = session;
        using LockLamp __ = lamp;

        receiver.State = true;

        await lamp.ArmAsync();
        await SetLockAsync(store, locked: false, receiver, expect: false);

        await lamp.RestoreAsync();

        Assert.True(receiver.State);
        Assert.False(lamp.IsHeld);
    }

    /// <summary>Setting it by hand takes it over, and the next transition does not undo that.</summary>
    /// <remarks>
    /// §10.9's escape hatch has to stay set, or it would read as the control not working: a person
    /// puts the lamp out, and a lock transition a minute later turns it back on.
    /// </remarks>
    [Fact]
    public async Task SettingItByHandStopsItFollowing()
    {
        (DeviceSessionService session, ReceiverStateStore store, LockLamp lamp, Panel receiver) = await ConnectedAsync();
        await using DeviceSessionService _ = session;
        using LockLamp __ = lamp;

        await lamp.ArmAsync();
        await SetLockAsync(store, locked: true, receiver, expect: true);

        Assert.True(await lamp.SetManuallyAsync(false));
        Assert.False(receiver.State);

        // A transition that would have lit it must now do nothing.
        await SetLockExpectingNoWriteAsync(store, locked: false);
        await SetLockExpectingNoWriteAsync(store, locked: true);

        Assert.False(receiver.State);

        // And nothing is owed back, because theirs is the value now.
        Assert.False(lamp.IsHeld);
    }

    /// <summary>An unreadable lamp is left alone rather than taken.</summary>
    /// <remarks>
    /// §11.1's rule seen from the write side: never act on a value we do not have. Lighting it
    /// anyway would leave nothing to put back.
    /// </remarks>
    [Fact]
    public async Task AnUnreadableLampIsNotTaken()
    {
        Panel receiver = new();
        FakeTimeProvider clock = new(Whenever);
        await using DeviceSessionService session = new(
            (_, _) => new ControllableTransport(c =>
                c.StartsWith("*IDN", StringComparison.OrdinalIgnoreCase) ? Identity
                : c.StartsWith(":LED:ACT?", StringComparison.OrdinalIgnoreCase) ? "nonsense"
                : string.Empty)
            { Banner = Identity },
            clock);

        await session.ConnectAsync("COM3", SerialSettings.Default);

        ReceiverStateStore store = new(clock);
        using LockLamp lamp = new(session, store);

        Assert.False(await lamp.ArmAsync());
        Assert.False(lamp.IsHeld);
    }

    /// <summary>A family with no such lamp is not asked.</summary>
    [Fact]
    public async Task AFamilyWithNoLampIsNotAsked()
    {
        FakeTimeProvider clock = new(Whenever);

        // The talker driver has to be REGISTERED, not merely plausible: a session with no drivers
        // falls back to the SmartClock, which does have the lamp - so an unregistered NMEA driver
        // would make this test pass for the wrong reason.
        await using DeviceSessionService session = new(
            (_, _) => new ControllableTransport(_ => string.Empty) { Banner = "$GPRMC,,V,,,,,,,,,,N*53" },
            clock,
            logger: null,
            drivers: [new WinZ3805A.Device.Drivers.Nmea.NmeaDriver(clock)]);

        await session.ConnectAsync("COM3", SerialSettings.Default);

        ReceiverStateStore store = new(clock);
        using LockLamp lamp = new(session, store);

        Assert.False(lamp.IsSupported);
        Assert.False(await lamp.ArmAsync());
    }
}
