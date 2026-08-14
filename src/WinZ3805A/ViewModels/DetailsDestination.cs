namespace WinZ3805A.ViewModels;

/// <summary>
/// One entry in the §9.7.1 navigation pane.
/// </summary>
/// <remarks>
/// A record rather than nine hand-written <c>NavigationViewItem</c>s in XAML, because the rules
/// that matter about this list are countable ones — how many there are, what order they are in, and
/// which accelerator reaches each — and a XAML list can express none of them to a test.
/// </remarks>
public sealed record DetailsDestination
{
    /// <summary>Stable identifier, used as the item's tag and in the persisted pane state.</summary>
    public required string Tag { get; init; }

    /// <summary>What the pane shows. Always visible in <c>Left</c> mode; a tooltip in the rail.</summary>
    public required string Label { get; init; }

    /// <summary>A Segoe Fluent Icons code point.</summary>
    public required string Glyph { get; init; }

    /// <summary>One line naming what the page will hold, shown while it is a placeholder.</summary>
    public required string Summary { get; init; }
}

/// <summary>
/// The §9.7.1 pane contents, in the order the wireframe draws them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Eight, plus Settings, and no more.</b> §10.2 caps it there so <c>Ctrl+1</c>…<c>Ctrl+8</c>
/// stays complete, which is also why the Advanced Console sits below Settings and outside the
/// numbered set. A ninth destination silently makes one page unreachable by keyboard, so the count
/// is asserted rather than trusted.
/// </para>
/// <para>
/// The order here is §9.7.1's, which is what the user sees. §15 step 8 gives a different order —
/// Overview, Satellites, Position, Timing, Holdover, Diagnostics, Time — and that is the order the
/// pages are *built* in; it also omits Status Registers, which §9.7.1 and §10.2 both require as a
/// destination. Build order and pane order are not the same list.
/// </para>
/// </remarks>
public static class DetailsDestinations
{
    /// <summary>The §10.2 cap on numbered destinations.</summary>
    public const int MaxNumbered = 8;

    /// <summary>The destinations reached by <c>Ctrl+1</c>…<c>Ctrl+8</c>, in pane order.</summary>
    public static IReadOnlyList<DetailsDestination> Numbered { get; } =
    [
        new()
        {
            Tag = "overview",
            Label = "Overview",
            Glyph = "\uE80F", // Home
            Summary = "Synchronisation state, figures of merit, and the headline timing figures (§10.4).",
        },
        new()
        {
            Tag = "satellites",
            Label = "Satellites",
            Glyph = "\uE774", // Globe
            Summary = "Sky plot, the tracked-satellite table, and the elevation mask (§10.5).",
        },
        new()
        {
            Tag = "position",
            Label = "Position",
            Glyph = "\uE81D", // MapPin
            Summary = "Surveyed position, survey progress, and the position hold controls (§10.6).",
        },
        new()
        {
            Tag = "timing",
            Label = "Timing",
            Glyph = "\uE916", // Stopwatch
            Summary = "Antenna delay with the cable calculator, 1 PPS alignment, and outputs (§10.7).",
        },
        new()
        {
            Tag = "holdover",
            Label = "Holdover",
            Glyph = "\uE769", // Pause
            Summary = "Holdover state, duration, uncertainty, and the recovery thresholds (§10.8).",
        },
        new()
        {
            Tag = "time",
            Label = "Time",
            Glyph = "\uE823", // Clock
            Summary = "UTC and GPS time, the leap-second table, and the §7.4 rollover correction (§10.9).",
        },
        new()
        {
            Tag = "registers",
            Label = "Status Registers",
            Glyph = "\uE8A9", // ViewAll
            Summary = "Questionable and operation status registers, decoded bit by bit (§10.10).",
        },
        new()
        {
            Tag = "diagnostics",
            Label = "Diagnostics",
            Glyph = "\uE9D9", // Diagnostic
            Summary = "The receiver's own log, with filtering, CSV export, and self-test results (§10.11).",
        },
    ];

    /// <summary>
    /// Settings, which sits in the pane's footer and outside the numbered accelerators.
    /// </summary>
    /// <remarks>
    /// It has its own accelerator, <c>Ctrl+,</c> (§9.7.5), and <c>NavigationView</c> gives the
    /// footer position for free — hand-placing it in the main list would put it in the numbered set
    /// and push a real destination out of reach.
    /// </remarks>
    public static DetailsDestination Settings { get; } = new()
    {
        Tag = "settings",
        Label = "Settings",
        Glyph = "\uE713", // Settings
        Summary = "Poll cadences, display time zone, units, and the opt-in advanced features (§10.13).",
    };

    /// <summary>Every destination, numbered ones first.</summary>
    public static IReadOnlyList<DetailsDestination> All { get; } = [.. Numbered, Settings];

    /// <summary>Finds a destination by its tag, or <see langword="null"/>.</summary>
    public static DetailsDestination? ByTag(string? tag) =>
        All.FirstOrDefault(destination => destination.Tag == tag);

    /// <summary>
    /// The destination <c>Ctrl+</c><paramref name="number"/> reaches, or <see langword="null"/>.
    /// </summary>
    /// <param name="number">One-based, as the accelerator is written.</param>
    public static DetailsDestination? ByNumber(int number) =>
        number >= 1 && number <= Numbered.Count ? Numbered[number - 1] : null;
}
