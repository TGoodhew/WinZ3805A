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
    /// <remarks>
    /// Still required where <see cref="IconGeometryKey"/> is set. §9.9's baseline is the stock font
    /// and the custom set is the exception, so a destination always has a stock glyph to fall back
    /// to — and a geometry key that does not resolve leaves an item with a label and no icon rather
    /// than one that cannot be drawn.
    /// </remarks>
    public required string Glyph { get; init; }

    /// <summary>
    /// The <c>Themes/Shapes.xaml</c> key for §9.9's custom icon, where this concept has one.
    /// </summary>
    /// <remarks>
    /// A resource key rather than a <c>Geometry</c>, because this record is compiled into the
    /// headless test assembly where no XAML type exists — the same reason <c>Severity</c> names
    /// <c>SeverityPill</c> in prose rather than with a cref.
    /// </remarks>
    public string? IconGeometryKey { get; init; }

    /// <summary>One line naming what the page will hold, shown while it is a placeholder.</summary>
    public required string Summary { get; init; }
}

/// <summary>
/// The §9.7.1 pane contents, in the order the wireframe draws them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Numbered, plus Settings, plus the opt-in console.</b> §10.2 capped the numbered set at eight
/// so <c>Ctrl+1</c>…<c>Ctrl+8</c> stayed complete; <see cref="DetailsDestinations.MaxNumbered"/> has
/// since been raised and <see cref="DetailsDestinations.MaxAccelerated"/> now carries the keyboard
/// rule alone. What has not changed is why the Advanced Console sits below Settings and outside the
/// numbered set: a destination that exists only for some users must never shift which page a
/// numbered accelerator reaches. The counts are asserted rather than trusted.
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
    /// <summary>The §10.2 cap on numbered destinations, raised from eight on 19 Aug 2026.</summary>
    /// <remarks>
    /// Provisional. Eight was chosen so <c>Ctrl+1</c>…<c>Ctrl+8</c> addressed every destination;
    /// twelve makes room for surfaces §10.x had not described — #111's Time &amp; Leap Seconds page
    /// and #137's EFC drift analysis both arrived with nowhere to live — and is to be revisited
    /// once it is clear how many the application actually wants.
    /// </remarks>
    public const int MaxNumbered = 12;

    /// <summary>
    /// How many destinations can carry a <c>Ctrl+</c>number accelerator.
    /// </summary>
    /// <remarks>
    /// <b>Nine, and it cannot stretch to match <see cref="MaxNumbered"/>, because there is no
    /// <c>Ctrl+10</c>.</b> Destinations past the ninth are reachable by pointer, by <c>Tab</c> and
    /// by the pane's arrow keys — A11Y-1 wants every destination keyboard *reachable*, not every
    /// destination accelerated — but they are second-class for a keyboard user, so the pane's order
    /// decides which are one keystroke away. Keep the most-used first.
    /// </remarks>
    public const int MaxAccelerated = 9;

    /// <summary>The numbered destinations, in pane order. The first nine carry accelerators.</summary>
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
            Glyph = "\uE774", // Globe, the fallback behind §9.9's custom satellite
            IconGeometryKey = "WzIconSatellite",
            Summary = "Sky plot, the tracked-satellite table, and the elevation mask (§10.5).",
        },
        new()
        {
            Tag = "position",
            Label = "Position",
            Glyph = "\uE81D", // MapPin, the fallback behind §9.9's custom earth
            IconGeometryKey = "WzIconEarth",
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
            Glyph = "\uE769", // Pause, the fallback behind §9.9's custom holdover
            IconGeometryKey = "WzIconHoldover",
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
        Summary = "The opt-in advanced features, and what is deliberately not a setting (§10.13).",
    };

    /// <summary>
    /// The §10.11 Advanced Console, shown only when Settings → Advanced enables it.
    /// </summary>
    /// <remarks>
    /// <b>Below Settings, in the footer, and never numbered.</b> §10.2's accelerator cap is a rule
    /// about the numbered set, and a destination that only exists for some users must not be able
    /// to shift which page <c>Ctrl+3</c> reaches — a shortcut whose meaning depends on a preference
    /// is worse than no shortcut. Keeping it out of <see cref="Numbered"/> makes that structural.
    /// </remarks>
    public static DetailsDestination AdvancedConsole { get; } = new()
    {
        Tag = "console",
        Label = "Advanced Console",
        Glyph = "\uE756", // CommandPrompt
        Summary = "A picker over the command catalog, with a transcript of everything on the wire (§10.11).",
    };

    /// <summary>Every destination, numbered ones first.</summary>
    /// <remarks>
    /// The console is included even when it is switched off. This list is what §9.8.2's transition
    /// direction and the persisted pane state are read from, and a list whose length depended on a
    /// preference would make a remembered selection mean a different page after the switch moved.
    /// Whether it is <i>shown</i> is the Details window's business.
    /// </remarks>
    public static IReadOnlyList<DetailsDestination> All { get; } = [.. Numbered, Settings, AdvancedConsole];

    /// <summary>Finds a destination by its tag, or <see langword="null"/>.</summary>
    public static DetailsDestination? ByTag(string? tag) =>
        All.FirstOrDefault(destination => destination.Tag == tag);

    /// <summary>
    /// Where a destination sits in the pane, counting from the top, or -1 if it is not one.
    /// </summary>
    /// <remarks>
    /// §9.8.2 takes the direction of the page transition from travel through this list, so the
    /// position of an entry is not merely presentational and the ordering above is not free to
    /// change without the transitions changing with it. Settings is included because it is a
    /// destination the user navigates to, footer or not — it is simply the last one.
    /// </remarks>
    public static int IndexOf(string? tag)
    {
        for (int index = 0; index < All.Count; index++)
        {
            if (All[index].Tag == tag)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// The destination <c>Ctrl+</c><paramref name="number"/> reaches, or <see langword="null"/>.
    /// </summary>
    /// <param name="number">One-based, as the accelerator is written.</param>
    /// <remarks>
    /// Bounded by <see cref="MaxAccelerated"/> rather than by how many destinations exist. Past the
    /// ninth there is no keystroke to ask with, and returning a destination for <c>Ctrl+10</c>
    /// would describe a shortcut nobody can press.
    /// </remarks>
    public static DetailsDestination? ByNumber(int number) =>
        number >= 1 && number <= Math.Min(Numbered.Count, MaxAccelerated) ? Numbered[number - 1] : null;
}
