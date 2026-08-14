using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Transport;

namespace WinZ3805A.Services;

/// <summary>How a tier C command ended.</summary>
public enum CommandOutcomeKind
{
    /// <summary>The receiver ran it and its error queue was empty afterwards.</summary>
    Succeeded = 0,

    /// <summary>The receiver answered, and then reported an error against the command (§7.2).</summary>
    Rejected,

    /// <summary>The command never got an answer — a timeout, a dropped link, or no connection.</summary>
    Failed,
}

/// <summary>
/// The result of running one tier C command, in the terms §9.11 asks the interface to report it.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="Transaction"/> because they answer different questions. A transaction
/// says what happened on the wire; this says what to tell the user, which for a tier C command
/// includes the error-queue read that §7.2 requires afterwards and that the transaction knows
/// nothing about. A command can complete perfectly at the transport layer and still be
/// <see cref="CommandOutcomeKind.Rejected"/>.
/// </para>
/// </remarks>
public sealed record CommandOutcome
{
    /// <summary>How it ended.</summary>
    public required CommandOutcomeKind Kind { get; init; }

    /// <summary>The command that was run.</summary>
    public required ScpiCommand Command { get; init; }

    /// <summary>
    /// The sentence to show — §9.11's success line, or its "what happened and what to do next"
    /// error text. Never null, and never starts with an apology (§9.11 copy rules).
    /// </summary>
    public required string Message { get; init; }

    /// <summary>The error the receiver reported, when <see cref="Kind"/> is <see cref="CommandOutcomeKind.Rejected"/>.</summary>
    public ScpiError? Error { get; init; }

    /// <summary>The response lines, for the queries in tier C — the self-test and the diagnostics.</summary>
    public IReadOnlyList<string> Lines { get; init; } = [];

    /// <summary>True only when the receiver ran the command and reported nothing against it.</summary>
    public bool Succeeded => Kind == CommandOutcomeKind.Succeeded;
}
