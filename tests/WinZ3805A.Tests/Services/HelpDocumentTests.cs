using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// The user's guide, parsed as the Help window parses it, held against the package layout (#312).
/// </summary>
/// <remarks>
/// The document under test is the real <c>docs/how-to-use.md</c>, copied into this project's
/// output under <c>Help\</c> exactly as the application's project links it into the package, with
/// its images beside it. So "every image the guide names exists" is asserted against the same
/// relative paths the window will resolve — an image renamed on disk but not in the document
/// fails here rather than rendering as a gap.
/// </remarks>
public sealed class HelpDocumentTests
{
    private static readonly Lazy<IReadOnlyList<HelpBlock>> Guide = new(() =>
        HelpDocument.Parse(File.ReadAllText(GuidePath)));

    private static string HelpRoot => Path.Combine(AppContext.BaseDirectory, "Help");

    private static string GuidePath => Path.Combine(HelpRoot, "how-to-use.md");

    // -------------------------------------------------------------------------------------
    // The real document
    // -------------------------------------------------------------------------------------

    [Fact]
    public void TheGuideIsLaidOutAsThePackageLaysItOut() =>
        Assert.True(File.Exists(GuidePath), $"{GuidePath} is missing: the test project must link docs/how-to-use.md as Help/how-to-use.md.");

    [Fact]
    public void TheGuideOpensWithItsTitle()
    {
        HelpBlock first = Guide.Value[0];

        HelpHeading heading = Assert.IsType<HelpHeading>(first);
        Assert.Equal(1, heading.Level);
        Assert.Equal("How to use", heading.Text);
    }

    /// <summary>The sections #308 asked for, by their headings, so a section cannot quietly go.</summary>
    [Theory]
    [InlineData("The main window")]
    [InlineData("Connecting")]
    [InlineData("Receiver Details")]
    [InlineData("When the window has gone")]
    [InlineData("Keyboard shortcuts")]
    [InlineData("Where things are kept")]
    public void EverySectionTheUserNeedsIsThere(string heading) =>
        Assert.Contains(Guide.Value, block => block is HelpHeading { Level: 2 } h && h.Text == heading);

    /// <summary>
    /// The one that matters. The images are linked into the package from docs/images; a name that
    /// drifts between the document and the folder is a blank in the window and nothing in the log.
    /// </summary>
    [Fact]
    public void EveryImageTheGuideNamesIsOneThePackageCarries()
    {
        IReadOnlyList<string> sources = HelpDocument.ImageSources(Guide.Value);

        Assert.True(sources.Count >= 30, $"only {sources.Count} images — the guide is meant to show every control");

        List<string> missing = sources
            .Where(source => !File.Exists(Path.Combine(HelpRoot, source.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();

        Assert.True(missing.Count == 0, "not in the package layout: " + string.Join(", ", missing));
    }

    /// <summary>§9.12: an image without alt text is a gap for a screen-reader user.</summary>
    [Fact]
    public void EveryImageHasAltText()
    {
        List<string> silent = [];
        Collect(Guide.Value, block =>
        {
            if (block is HelpImage { AltText: "" } image)
            {
                silent.Add(image.Source);
            }
        }, inline =>
        {
            if (inline is HelpInlineImage { AltText: "" } image)
            {
                silent.Add(image.Source);
            }
        });

        Assert.True(silent.Count == 0, "no alt text: " + string.Join(", ", silent));
    }

    [Fact]
    public void EveryInDocumentLinkResolvesToAHeading()
    {
        HashSet<string> anchors = Guide.Value.OfType<HelpHeading>().Select(h => h.Anchor).ToHashSet(StringComparer.Ordinal);

        List<string> dangling = HelpDocument.LinkTargets(Guide.Value)
            .Where(target => target.StartsWith('#') && !anchors.Contains(target[1..]))
            .ToList();

        Assert.True(dangling.Count == 0, "dangling: " + string.Join(", ", dangling));
    }

    [Fact]
    public void HeadingAnchorsAreUnique()
    {
        List<string> anchors = Guide.Value.OfType<HelpHeading>().Select(h => h.Anchor).ToList();

        Assert.Equal(anchors.Count, anchors.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>The Settings table carries the switch image in every row, as #308 asked.</summary>
    [Fact]
    public void TheSettingsTableShowsEverySwitch()
    {
        HelpTable table = Assert.Single(Tables(), t => HelpDocument.PlainText(t.Header[0]) == "Setting");

        Assert.Equal(3, table.Header.Count);
        Assert.Equal(6, table.Rows.Count);
        Assert.All(table.Rows, row => Assert.Contains(row[0], inline => inline is HelpInlineImage));
    }

    /// <summary>The shortcut table lists what the two windows register — the keys the code binds.</summary>
    [Theory]
    [InlineData("Ctrl+D")]
    [InlineData("Ctrl+Shift+C")]
    [InlineData("Ctrl+Shift+M")]
    [InlineData("F5")]
    [InlineData("Ctrl+E")]
    [InlineData("Ctrl+,")]
    [InlineData("F1")]
    public void TheShortcutTableListsTheKeyTheCodeBinds(string key)
    {
        HelpTable table = Assert.Single(Tables(), t => HelpDocument.PlainText(t.Header[1]) == "Key");

        Assert.Contains(table.Rows, row => HelpDocument.PlainText(row[1]).Contains(key, StringComparison.Ordinal));
    }

    /// <summary>What the window would show if the parser missed something: markdown on the screen.</summary>
    [Fact]
    public void NoMarkdownSyntaxSurvivesParsing()
    {
        List<string> leaked = [];
        Collect(Guide.Value, _ => { }, inline =>
        {
            if (inline is HelpText text && (text.Text.Contains("**", StringComparison.Ordinal) || text.Text.Contains("](", StringComparison.Ordinal)))
            {
                leaked.Add(text.Text);
            }
        });

        Assert.True(leaked.Count == 0, "unparsed: " + string.Join(" | ", leaked));
    }

    // -------------------------------------------------------------------------------------
    // The parser on its own
    // -------------------------------------------------------------------------------------

    /// <summary>Anchors come out the way github.com derives them, so a link checked there works here.</summary>
    [Theory]
    [InlineData("The main window", "the-main-window")]
    [InlineData("When the window has gone", "when-the-window-has-gone")]
    [InlineData("Overview — Ctrl+1", "overview--ctrl1")]
    [InlineData("Timing & antenna — Ctrl+4", "timing--antenna--ctrl4")]
    public void AnchorsAreDerivedTheWayGitHubDerivesThem(string heading, string anchor) =>
        Assert.Equal(anchor, HelpDocument.AnchorFor(heading));

    [Fact]
    public void EmphasisAndCodeAreCarriedOnTheText()
    {
        IReadOnlyList<HelpBlock> blocks = HelpDocument.Parse("Press **Ctrl+D** to *open* the `Details` window.");

        HelpParagraph paragraph = Assert.IsType<HelpParagraph>(Assert.Single(blocks));
        Assert.Contains(paragraph.Inlines, i => i is HelpText { Text: "Ctrl+D", Bold: true, Italic: false, Code: false });
        Assert.Contains(paragraph.Inlines, i => i is HelpText { Text: "open", Italic: true, Bold: false });
        Assert.Contains(paragraph.Inlines, i => i is HelpText { Text: "Details", Code: true });
    }

    [Fact]
    public void AParagraphThatIsOnlyAnImageIsAFigure()
    {
        IReadOnlyList<HelpBlock> blocks = HelpDocument.Parse("![The main window](images/main.png)");

        HelpImage image = Assert.IsType<HelpImage>(Assert.Single(blocks));
        Assert.Equal("images/main.png", image.Source);
        Assert.Equal("The main window", image.AltText);
    }

    [Fact]
    public void AnImageInsideALineStaysInline()
    {
        IReadOnlyList<HelpBlock> blocks = HelpDocument.Parse("| A | B |\n|---|---|\n| ![switch](s.png) **On** | yes |");

        HelpTable table = Assert.IsType<HelpTable>(Assert.Single(blocks));
        Assert.Contains(table.Rows[0][0], i => i is HelpInlineImage { Source: "s.png" });
        Assert.Contains(table.Rows[0][0], i => i is HelpText { Text: "On", Bold: true });
    }

    /// <summary>The source is wrapped at a hundred columns; a wrap is a space, not a break.</summary>
    [Fact]
    public void ASoftLineBreakIsASpace()
    {
        IReadOnlyList<HelpBlock> blocks = HelpDocument.Parse("one\ntwo");

        HelpParagraph paragraph = Assert.IsType<HelpParagraph>(Assert.Single(blocks));
        Assert.Equal("one two", HelpDocument.PlainText(paragraph.Inlines));
    }

    [Fact]
    public void ListsNestBlocks()
    {
        IReadOnlyList<HelpBlock> blocks = HelpDocument.Parse("1. first\n2. second\n\n   ![a](a.png)\n");

        HelpList list = Assert.IsType<HelpList>(Assert.Single(blocks));
        Assert.True(list.Ordered);
        Assert.Equal(2, list.Items.Count);
        Assert.Contains(list.Items[1], block => block is HelpImage { Source: "a.png" });
    }

    [Fact]
    public void RulesQuotesAndCodeBlocksAreKept()
    {
        IReadOnlyList<HelpBlock> blocks = HelpDocument.Parse("---\n\n> quoted\n\n```\n:SYNC:STAT?\n```\n");

        Assert.Collection(
            blocks,
            block => Assert.IsType<HelpRule>(block),
            block => Assert.IsType<HelpQuote>(block),
            block => Assert.Equal(":SYNC:STAT?", Assert.IsType<HelpCodeBlock>(block).Text));
    }

    [Fact]
    public void AnEmptyDocumentIsEmptyRatherThanAnError() =>
        Assert.Empty(HelpDocument.Parse(string.Empty));

    // -------------------------------------------------------------------------------------

    private static IEnumerable<HelpTable> Tables() => Guide.Value.OfType<HelpTable>();

    private static void Collect(IReadOnlyList<HelpBlock> blocks, Action<HelpBlock> onBlock, Action<HelpInline> onInline)
    {
        foreach (HelpBlock block in blocks)
        {
            onBlock(block);
            switch (block)
            {
                case HelpParagraph paragraph:
                    foreach (HelpInline inline in paragraph.Inlines)
                    {
                        onInline(inline);
                    }

                    break;
                case HelpList list:
                    foreach (IReadOnlyList<HelpBlock> item in list.Items)
                    {
                        Collect(item, onBlock, onInline);
                    }

                    break;
                case HelpTable table:
                    foreach (IReadOnlyList<HelpInline> cell in table.Header.Concat(table.Rows.SelectMany(row => row)))
                    {
                        foreach (HelpInline inline in cell)
                        {
                            onInline(inline);
                        }
                    }

                    break;
                case HelpQuote quote:
                    Collect(quote.Blocks, onBlock, onInline);
                    break;
                default:
                    break;
            }
        }
    }
}
