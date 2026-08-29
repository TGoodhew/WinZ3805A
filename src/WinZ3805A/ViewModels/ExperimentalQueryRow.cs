using System.ComponentModel;

using WinZ3805A.Device.Commands;

namespace WinZ3805A.ViewModels;

/// <summary>
/// One of §8.5's undocumented queries, with whatever it last answered.
/// </summary>
/// <remarks>
/// <para>
/// <b>The list these come from is fixed and query-only.</b> §8.5 names six nodes; the catalog marks
/// exactly those six <c>IsExperimental</c>, and §8.4's rule that the <i>set</i> forms of undocumented
/// nodes are permanently excluded with no override is what makes an opt-in safe to offer at all. A
/// user turning this on gains six questions, not a mode.
/// </para>
/// <para>
/// <b>Whatever comes back is shown verbatim, including an error.</b> These nodes are absent from the
/// published manual and may answer with nonsense; a card that hid the nonsense would be claiming to
/// understand output it does not. §8.5 says results are raw text and any SCPI error is displayed
/// rather than swallowed, which is the same rule §11.1 applies to the parser seen from the other
/// side — never substitute, never guess.
/// </para>
/// </remarks>
public sealed class ExperimentalQueryRow : INotifyPropertyChanged
{
    private string? _result;
    private bool _isBusy;
    private bool _isError;

    /// <summary>Creates a row over a catalogued experimental query.</summary>
    /// <exception cref="ArgumentException">
    /// If the command is not experimental, or is not a query. Both are §8.5 invariants and both are
    /// cheap to assert here, where the mistake would otherwise reach a button.
    /// </exception>
    public ExperimentalQueryRow(ScpiCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!command.IsExperimental)
        {
            throw new ArgumentException(
                $"{command.Mnemonic} is not one of §8.5's opt-in queries.", nameof(command));
        }

        if (!command.IsQuery)
        {
            throw new ArgumentException(
                $"{command.Mnemonic} is not a query. §8.5 is query-only and §8.4 excludes the set forms.",
                nameof(command));
        }

        Command = command;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The catalogued command, which is the only source of any of this.</summary>
    public ScpiCommand Command { get; }

    /// <summary>The mnemonic, shown in <c>WzMonoTextStyle</c> because it is device-literal.</summary>
    public string Mnemonic => Command.Mnemonic;

    /// <summary>A short label.</summary>
    public string DisplayName => Command.DisplayName;

    /// <summary>What it is believed to do, which is less certain here than elsewhere.</summary>
    public string Description => Command.Description;

    /// <summary>What it last answered, or null if it has not been run.</summary>
    public string? Result
    {
        get => _result;
        set => Set(ref _result, value, nameof(Result), nameof(HasResult));
    }

    /// <summary>Whether <see cref="Result"/> is an error rather than an answer.</summary>
    /// <remarks>
    /// Shown as well as coloured. §9.4.3 and A11Y-12 forbid carrying the distinction in hue alone,
    /// and an error from an undocumented node reads exactly like a short answer otherwise.
    /// </remarks>
    public bool IsError
    {
        get => _isError;
        set => Set(ref _isError, value, nameof(IsError));
    }

    /// <summary>Whether it is running now.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set => Set(ref _isBusy, value, nameof(IsBusy), nameof(CanRun));
    }

    /// <summary>Whether there is anything to show.</summary>
    public bool HasResult => !string.IsNullOrEmpty(_result);

    /// <summary>Whether the button should be live.</summary>
    public bool CanRun => !_isBusy;

    private void Set<T>(ref T field, T value, params string[] names)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;

        foreach (string name in names)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}

/// <summary>§8.5's fixed list, as rows.</summary>
/// <remarks>
/// <para>
/// Built by filtering the driver's allowlist rather than restated, so the six exist in exactly
/// one place — <c>IsExperimental</c> is carried by the catalog entries themselves (#287 removed
/// the last static reach). A test asserts the SmartClock count and that every one is a query —
/// §8.5 calls the list fixed, and "fixed" is worth an assertion rather than a comment.
/// </para>
/// <para>
/// <b>Five of the six do not exist on this receiver.</b> Run against the bench Z3805A, firmware
/// <c>1.01.03-A</c>, on 20 Aug 2026:
/// </para>
/// <list type="table">
/// <item><term><c>:DIAG:ROSC:EFC:ABSolute?</c></term><description><c>+436061</c></description></item>
/// <item><term><c>:DIAG:ROSC:EFC:TCOefficient?</c></term><description><c>E-113</c></description></item>
/// <item><term><c>:SYST:STAT:SLOG?</c></term><description><c>E-113</c></description></item>
/// <item><term><c>:DIAG:STACk?</c></term><description><c>E-113</c></description></item>
/// <item><term><c>:DIAG:PROCess?</c></term><description><c>E-113</c></description></item>
/// <item><term><c>:DIAG:MEMory?</c></term><description><c>E-113</c></description></item>
/// </list>
/// <para>
/// <c>E-113</c> is "undefined header" — the node is not in this firmware's parser at all. §16 names
/// the source of these keywords as a <b>Z3801A</b> firmware string dump, which is a sibling model,
/// so five of them being absent here is a difference between models rather than a fault. §8.5 now
/// records both columns and says that <c>E-113</c> is an <b>answer rather than a failure</b> (#152).
/// The card is what made it discoverable; nobody had asked before.
/// </para>
/// <para>
/// The list is <b>not</b> filtered to the one that works, and §8.5 now gives the three reasons: the
/// application would have to probe all six to know which to drop, a list that changed shape by model
/// would make "exactly" untrue, and a user who opted into asking undocumented questions is owed the
/// answer rather than a shorter list.
/// </para>
/// </remarks>
public static class ExperimentalQueries
{
    /// <summary>Fresh rows over the driver's experimental queries, in catalog order.</summary>
    /// <remarks>
    /// <para>
    /// A factory rather than a static list: each row holds its own last result, and two Diagnostics
    /// pages sharing one set of rows would show each other's answers.
    /// </para>
    /// <para>
    /// The old <c>Count</c> constant of six is gone with the static (#287): six is a fact about the
    /// SmartClock catalog, asserted in its tests, not a cross-family constant for a view model to
    /// compile in. A family with no experimental queries gets an empty card, which is the truth.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ExperimentalQueryRow> Create(Device.Drivers.IReceiverDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);

        return driver.Commands
            .Where(command => command.IsExperimental)
            .Select(command => new ExperimentalQueryRow(command))
            .ToList()
            .AsReadOnly();
    }
}
