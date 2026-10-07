namespace WinZ3805A.ViewModels;

/// <summary>
/// Joins the readings a family cannot supply into the noun phrase one sentence names (#751).
/// </summary>
/// <remarks>
/// A card that loses several rows says so once, naming only what is actually absent — the Position
/// page's rule for its fix-quality rows. A list built from what is absent needs English joining
/// rather than a fixed phrase, or a family missing two of three would be described as missing all
/// three.
/// </remarks>
public static class AbsentReadings
{
    /// <summary>
    /// "a", "a or b", "a, b or c" — or <see langword="null"/> when nothing is absent.
    /// </summary>
    /// <param name="readings">The absent readings, each as a noun phrase, in the card's order.</param>
    public static string? Join(IReadOnlyList<string> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);

        return readings.Count switch
        {
            0 => null,
            1 => readings[0],
            _ => $"{string.Join(", ", readings.Take(readings.Count - 1))} or {readings[^1]}",
        };
    }
}
