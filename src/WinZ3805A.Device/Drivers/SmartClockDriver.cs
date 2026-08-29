using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Models;
using WinZ3805A.Device.Parsing;
using WinZ3805A.Device.Transport;

namespace WinZ3805A.Device.Drivers;

/// <summary>
/// The HP/Symmetricom SmartClock family — Z3805A, Z3801A, Z3816A, 58503A/B, 59551A (#122).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the worked example the README's walkthrough describes.</b> Every piece it implements
/// already existed and was static; the driver is where "which receiver" stopped being implied and
/// started being named. Nothing here changes behaviour — the acceptance criterion for #122 is that
/// the existing tests pass unmodified, because a refactor whose tests had to be rewritten has not
/// preserved anything.
/// </para>
/// <para>
/// It is one driver for the whole family rather than one per model. The models differ in hardware
/// rather than in dialect: they share the status screen, the command tree and the prompt, and where
/// they diverge it is over which optional subsystems exist, which is
/// <see cref="ModelProfile"/>'s job (#64). A Trimble Thunderbolt is a different <i>driver</i>; a
/// 59551A is the same driver with a different profile.
/// </para>
/// </remarks>
/// <param name="timeProvider">
/// Supplies "now" for the parser's capture stamp and §7.4's rollover comparison. Injected because
/// fixture tests pin the clock, and the rollover arithmetic is meaningless against a moving one.
/// </param>
public sealed class SmartClockDriver(TimeProvider timeProvider) : IReceiverDriver
{
    private readonly StatusScreenParser _parser = new(timeProvider);

    /// <inheritdoc />
    public string Family => "SmartClock";

    /// <inheritdoc />
    public IReadOnlyList<ScpiCommand> Commands => CommandCatalog.All;

    /// <inheritdoc />
    public PollCadence Cadence { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));

    /// <inheritdoc />
    public IReadOnlyList<SerialSettings> AutoDetectSequence => SerialSettings.AutoDetectSequence;

    /// <summary>
    /// Recognises any model whose identity this family covers, and an unread identity.
    /// </summary>
    /// <remarks>
    /// <b>Null counts as recognised, deliberately.</b> Auto-detect has to send <c>*IDN?</c> before
    /// anything can know what is attached, so the driver used for that first exchange is chosen
    /// before there is an identity to choose it by. This family's is the right default while it is
    /// the only driver; the moment a second exists, that choice belongs to the caller and this
    /// should be revisited — see the README's "Choosing a driver before the identity is known".
    /// </remarks>
    public bool Recognises(DeviceIdentity? identity) =>
        identity is null || identity.Receiver != ReceiverModel.Unknown;

    /// <inheritdoc />
    public ScpiCommand? Find(string? mnemonic) => CommandCatalog.Find(mnemonic);

    /// <inheritdoc />
    /// <remarks>
    /// Delegates to the one place in the repository permitted to hold those patterns. The verdict
    /// crosses this boundary; the patterns do not.
    /// </remarks>
    public bool IsBlocked(string? header) =>
        !string.IsNullOrWhiteSpace(header) && CommandCatalog.IsBlocked(header);

    /// <inheritdoc />
    public TimeSpan TimeoutFor(string? mnemonic) =>
        string.IsNullOrWhiteSpace(mnemonic) ? TransactionTimeouts.Default : TransactionTimeouts.For(mnemonic);

    /// <inheritdoc />
    public ReceiverStatus Parse(string? response) => _parser.Parse(response);
}
