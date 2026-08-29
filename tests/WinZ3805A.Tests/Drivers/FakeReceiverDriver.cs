using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Models;
using WinZ3805A.Device.Parsing;
using WinZ3805A.Device.Transport;

namespace WinZ3805A.Tests.Drivers;

/// <summary>
/// A second receiver family that exists only in the test project (#287, item 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>A seam with a single implementation is a guess about where the joint is.</b> This driver is
/// the check on the guess: a fictional "Acme" receiver with a different vocabulary, its own
/// exclusion, its own timeouts, cadence and serial preferences, and a sweep plan that shares not
/// one mnemonic with the SmartClock's. Everywhere the application still reached the SmartClock
/// statics directly, a test built over this driver fails by asking the wrong questions on the wire
/// — which is the loud version of the defect a grep can only hope to spot.
/// </para>
/// <para>
/// It stays test-side, like <c>ControllableTransport</c> and for the same reason: it models no real
/// hardware, and shipping it would put a family in the composition root that nothing can connect
/// to. It is deliberately <i>not</i> a mock with settable answers — the contract tests run against
/// it exactly as they run against <see cref="SmartClockDriver"/>, so it has to be a coherent
/// implementation rather than a bag of stubs.
/// </para>
/// <para>
/// Every mnemonic here is fictional (<c>:ACME:</c>…) except <c>:SYST:ERR?</c>, which is IEEE
/// 488.2's own error query and a documented requirement of the contract —
/// <c>CommandInvoker</c> drains the queue through it after every tier C command. The exclusion
/// pattern is fictional too: §8.4's real exclusions are SmartClock command names, live only in
/// <c>BlockedCommands.cs</c>, and are not restated here in any form.
/// </para>
/// </remarks>
public sealed class FakeReceiverDriver : IReceiverDriver
{
    /// <summary>The sweep's state tokens — this family says RUN or IDLE, never LOCK.</summary>
    private static readonly IReadOnlySet<string> States =
        new HashSet<string>(StringComparer.Ordinal) { "RUN", "IDLE" };

    /// <inheritdoc />
    public string Family => "Acme";

    /// <inheritdoc />
    public IReadOnlyList<ScpiCommand> Commands { get; } =
    [
        new(
            Mnemonic: ":ACME:STAT?",
            ShortForm: ":ACME:STAT?",
            Tier: SafetyTier.Safe,
            IsQuery: true,
            DisplayName: "Run state",
            Description: "Whether the fictional receiver is running or idle.",
            Parameters: [],
            ResponseFormat: ResponseFormat.Keyword),
        new(
            Mnemonic: ":ACME:LEVel?",
            ShortForm: ":ACME:LEV?",
            Tier: SafetyTier.Safe,
            IsQuery: true,
            DisplayName: "Control level",
            Description: "The fictional oscillator control level, in percent.",
            Parameters: [],
            ResponseFormat: ResponseFormat.Decimal),
        new(
            Mnemonic: ":ACME:DUMP?",
            ShortForm: ":ACME:DUMP?",
            Tier: SafetyTier.Safe,
            IsQuery: true,
            DisplayName: "Full status",
            Description: "The fictional receiver's whole status output.",
            Parameters: [],
            ResponseFormat: ResponseFormat.MultiLine),
        new(
            Mnemonic: ":SYST:ERR?",
            ShortForm: ":SYST:ERR?",
            Tier: SafetyTier.Safe,
            IsQuery: true,
            DisplayName: "Next error",
            Description: "The next queued error, per IEEE 488.2.",
            Parameters: [],
            ResponseFormat: ResponseFormat.Text),
        new(
            Mnemonic: ":ACME:MARK",
            ShortForm: ":ACME:MARK",
            Tier: SafetyTier.Confirm,
            IsQuery: false,
            DisplayName: "Drop a mark",
            Description: "Writes a mark into the fictional event log.",
            Parameters: [],
            ResponseFormat: ResponseFormat.None,
            ConfirmationText: "Drop a mark into the event log?",
            SuccessText: "Mark dropped."),
    ];

    /// <inheritdoc />
    public PollCadence Cadence { get; } = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(20));

    /// <inheritdoc />
    /// <remarks>
    /// One entry the SmartClock walk also probes and one it does not, deliberately: the session's
    /// union has both a duplicate to collapse and an addition to append, so the plan tests exercise
    /// each half of the merge.
    /// </remarks>
    public IReadOnlyList<SerialSettings> AutoDetectSequence { get; } =
    [
        new() { BaudRate = 9600, DataBits = 8, Parity = System.IO.Ports.Parity.None },
        new() { BaudRate = 2400, DataBits = 7, Parity = System.IO.Ports.Parity.Even },
    ];

    /// <inheritdoc />
    public bool Recognises(DeviceIdentity? identity) =>
        string.Equals(identity?.Manufacturer, "ACME", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public ScpiCommand? Find(string? mnemonic) =>
        Commands.FirstOrDefault(command =>
            string.Equals(command.Mnemonic, mnemonic?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    /// <remarks>
    /// A fictional exclusion for a fictional receiver. Its point is to be different from the
    /// SmartClock's: the contract says exclusions are per-device, and the test that proves it needs
    /// two drivers whose answers disagree about the same header.
    /// </remarks>
    public bool IsBlocked(string? header) =>
        header?.TrimStart().StartsWith(":ACME:ZAP", StringComparison.OrdinalIgnoreCase) == true;

    /// <inheritdoc />
    /// <remarks>
    /// Ten seconds, not something realistic: the clock-wind test helpers advance a second at a
    /// time, and a timeout inside the wind step is this repository's known flake family invited
    /// back in. A fake's timeout only needs to be positive, bounded, and safely clear of the wind.
    /// </remarks>
    public TimeSpan TimeoutFor(string? mnemonic) => TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public PollPlan Plan { get; } = new(
        FastTier: [":ACME:STAT?", ":ACME:LEVel?"],
        RefusableIndex: null,
        FullStatus: ":ACME:DUMP?");

    /// <inheritdoc />
    public ReceiverStatus Parse(string? response) => new()
    {
        // The common currency with nothing in it, which is the contract's own advice for a field
        // the receiver has no equivalent of — and this receiver has no equivalent of anything.
        ParseWarnings = [$"the Acme driver read nothing from {(string.IsNullOrWhiteSpace(response) ? "an empty response" : "the response")}"],
    };

    /// <inheritdoc />
    public SweepInterpretation InterpretSweep(IReadOnlyList<string?> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);

        string? state = ScalarParsers.ParseKeyword(answers.Count > 0 ? answers[0] : null);

        FastReadings readings = new(
            SyncState: state,
            Tfom: null,
            Ffom: null,
            TimeIntervalNanoseconds: null,
            EfcPercent: ScalarParsers.ParseDecimal(answers.Count > 1 ? answers[1] : null),
            SatellitesTracked: null);

        return States.Contains(state ?? string.Empty)
            ? new SweepInterpretation(readings, Rejection: null)
            : new SweepInterpretation(readings, "the run state was not RUN or IDLE, which is all this receiver says");
    }
}
