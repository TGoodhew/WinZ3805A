namespace WinZ3805A.Device.Commands;

/// <summary>
/// How much ceremony a command needs before it runs (§8).
/// </summary>
/// <remarks>
/// <para>
/// This is the whole safety model in three values. It is worth being precise about what the third
/// one means, because it is not a tier the catalog uses.
/// </para>
/// <para>
/// <see cref="Safe"/> and <see cref="Confirm"/> classify entries that exist.
/// <see cref="Blocked"/> classifies nothing: no catalog entry ever carries it. §8.1 keeps the value
/// only so the Advanced Console's validator has a word for what it did when it rejected a string a
/// user typed by hand. A blocked command is absent from the catalog entirely — not an entry with a
/// flag set — which is what makes it unreachable rather than merely discouraged (goal G4).
/// </para>
/// </remarks>
public enum SafetyTier
{
    /// <summary>
    /// Runs on click with no confirmation: every query, plus the two recovery actions §8.2 classes
    /// safe because they move the receiver toward lock and cannot damage anything.
    /// </summary>
    Safe = 0,

    /// <summary>
    /// Runs only after a modal confirmation carrying the consequence in plain words (§8.3).
    /// </summary>
    Confirm,

    /// <summary>
    /// Never runs, and never appears. No entry in <see cref="CommandCatalog"/> has this tier —
    /// see the remarks on <see cref="SafetyTier"/> for why the value exists at all.
    /// </summary>
    Blocked,
}
