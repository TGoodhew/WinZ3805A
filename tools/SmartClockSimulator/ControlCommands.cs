using System.Globalization;

namespace WinZ3805A.Simulation.SmartClock;

/// <summary>
/// The words the simulator understands on its control channel — standard input, or the control
/// pipe a test harness writes to.
/// </summary>
/// <remarks>
/// These change the simulated <i>world</i> — the antenna, the power, the cable — not the receiver's
/// settings, which the application changes over the wire like it would on real hardware. That
/// division is deliberate: a QA scenario that pulls the antenna this way exercises exactly the path
/// a person pulling the real antenna does.
/// </remarks>
public static class ControlCommands
{
    /// <summary>What <c>help</c> prints.</summary>
    public const string Help =
        """
        antenna off | on          pull or reconnect the antenna (holdover, then recovery)
        power-cycle               start again from power-up
        start locked              jump straight to a settled lock
        holdover | recover        force holdover, or start recovery, as the commands would
        health <item> fail | ok   item: selftest intpwr ovenpwr ocxo efc gpsrcv
        fault silent | garbage | truncate | latency <ms> | drop | none
        echo on | off             the receiver's FDUPLEX setting
        speed <factor>            run the timeline faster (the reported clock stays real)
        status                    one line describing the receiver and the link
        """;

    /// <summary>Applies one control line and returns what to tell whoever sent it.</summary>
    public static string Apply(string line, SimulatedReceiver receiver, ScpiEngine engine, SimulatorLink link)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(link);

        string[] words = line.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return string.Empty;
        }

        lock (link.Gate)
        {
            switch (words)
            {
                case ["help" or "?"]:
                    return Help;

                case ["antenna", "off"]:
                    receiver.AntennaConnected = false;
                    return "ok: antenna disconnected";

                case ["antenna", "on"]:
                    receiver.AntennaConnected = true;
                    return "ok: antenna connected";

                case ["power-cycle"]:
                    receiver.PowerCycle();
                    return "ok: power-up";

                case ["start", "locked"]:
                    receiver.StartLocked();
                    return "ok: locked";

                case ["holdover"]:
                    receiver.ForceHoldover();
                    return "ok: holdover forced";

                case ["recover"]:
                    receiver.Recover();
                    return "ok: recovering";

                case ["health", string item, "fail" or "ok"]:
                    return SetHealth(receiver, item, words[2] == "ok");

                case ["fault", "silent"]:
                    link.Faults.Silent = true;
                    return "ok: silent";

                case ["fault", "garbage"]:
                    link.Faults.Garbage = true;
                    return "ok: garbage";

                case ["fault", "truncate"]:
                    link.Faults.TruncateNext = true;
                    return "ok: the next reply will be cut off";

                case ["fault", "latency", string ms] when int.TryParse(ms, NumberStyles.Integer, CultureInfo.InvariantCulture, out int extra) && extra >= 0:
                    link.Faults.ExtraLatency = TimeSpan.FromMilliseconds(extra);
                    return $"ok: +{extra} ms";

                case ["fault", "drop"]:
                    link.Faults.DropRequested = true;
                    return "ok: dropping the connection";

                case ["fault", "none"]:
                    link.Faults.Clear();
                    return "ok: link healthy";

                case ["echo", "on" or "off"]:
                    engine.Echo = words[1] == "on";
                    return "ok: echo " + words[1];

                case ["speed", string factor] when double.TryParse(factor, NumberStyles.Float, CultureInfo.InvariantCulture, out double speed) && speed > 0:
                    receiver.Speed = speed;
                    return string.Create(CultureInfo.InvariantCulture, $"ok: speed {speed}");

                case ["status"]:
                    return $"{receiver}; faults: {link.Faults}; errors queued: {engine.QueuedErrors.Count}";

                default:
                    return "error: not understood. Try help.";
            }
        }
    }

    private static string SetHealth(SimulatedReceiver receiver, string item, bool ok)
    {
        HealthPanel h = receiver.Health;
        HealthPanel? next = item switch
        {
            "selftest" => h with { SelfTest = ok },
            "intpwr" => h with { InternalPower = ok },
            "ovenpwr" => h with { OvenPower = ok },
            "ocxo" => h with { Ocxo = ok },
            "efc" => h with { Efc = ok },
            "gpsrcv" => h with { GpsReceiver = ok },
            _ => null,
        };

        if (next is null)
        {
            return "error: no such health item. Items: selftest intpwr ovenpwr ocxo efc gpsrcv";
        }

        receiver.Health = next;
        return $"ok: {item} {(ok ? "OK" : "Err")}";
    }
}
