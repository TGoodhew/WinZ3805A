using System.Globalization;
using System.Text;

using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace WinZ3805A.Services;

/// <summary>One block of the user's guide, in the shape the Help window lays out (#312).</summary>
public abstract record HelpBlock;

/// <summary>A heading. <see cref="Anchor"/> is the GitHub-style slug in-document links use.</summary>
public sealed record HelpHeading(int Level, string Text, string Anchor) : HelpBlock;

/// <summary>A run of inline content.</summary>
public sealed record HelpParagraph(IReadOnlyList<HelpInline> Inlines) : HelpBlock;

/// <summary>A paragraph that is one image and nothing else — shown as a figure rather than inline.</summary>
public sealed record HelpImage(string Source, string AltText) : HelpBlock;

/// <summary>A bulleted or numbered list. Each item is itself a sequence of blocks.</summary>
public sealed record HelpList(bool Ordered, IReadOnlyList<IReadOnlyList<HelpBlock>> Items) : HelpBlock;

/// <summary>A pipe table: one header row and any number of body rows, each cell inline content.</summary>
public sealed record HelpTable(
    IReadOnlyList<IReadOnlyList<HelpInline>> Header,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<HelpInline>>> Rows) : HelpBlock;

/// <summary>A block quote.</summary>
public sealed record HelpQuote(IReadOnlyList<HelpBlock> Blocks) : HelpBlock;

/// <summary>A fenced or indented code block, verbatim.</summary>
public sealed record HelpCodeBlock(string Text) : HelpBlock;

/// <summary>A horizontal rule.</summary>
public sealed record HelpRule : HelpBlock;

/// <summary>One piece of inline content.</summary>
public abstract record HelpInline;

/// <summary>Text, with the formatting that applies to all of it.</summary>
public sealed record HelpText(string Text, bool Bold = false, bool Italic = false, bool Code = false) : HelpInline;

/// <summary>A link. <see cref="Target"/> is an in-document anchor when it starts with <c>#</c>.</summary>
public sealed record HelpLink(IReadOnlyList<HelpInline> Content, string Target) : HelpInline;

/// <summary>An image inside a line of text — a table cell's switch, say.</summary>
public sealed record HelpInlineImage(string Source, string AltText) : HelpInline;

/// <summary>A hard line break.</summary>
public sealed record HelpLineBreak : HelpInline;

/// <summary>
/// Turns the user's guide — <c>docs/how-to-use.md</c>, shipped in the package — into blocks the
/// Help window can lay out natively (#312).
/// </summary>
/// <remarks>
/// <para>
/// <b>Native, not a browser.</b> The guide could have been rendered by handing the Markdown to a
/// web view, and it is not, for two reasons that compound. §9 makes every surface in this
/// application a Fluent surface built from the token set, and a browser control is a rectangle
/// the tokens do not reach — text scaling, high contrast and the accent would all stop at its
/// edge. And the privacy policy says, truthfully, that the application contains no HTTP client;
/// a browser engine is one, whatever it is pointed at. So the Markdown is parsed here into a
/// block model, and <c>HelpWindow</c> builds XAML from the model with styles declared in XAML.
/// </para>
/// <para>
/// <b>This file speaks no WinUI</b>, and is linked into the test project for that reason: the
/// real document is parsed there and checked against the package layout, so an image the guide
/// names that the package does not carry fails a test rather than showing a blank in the window.
/// Markdig does the parsing — a hand-rolled parser for "just the subset we use" is how a guide
/// stops being editable.
/// </para>
/// <para>
/// The model keeps only what the guide uses: headings, paragraphs, emphasis, inline code, links,
/// images (as figures and inline), bulleted and numbered lists, pipe tables, block quotes, code
/// blocks and rules. Raw HTML is dropped rather than shown, since the window has nowhere to put
/// it and the guide has none.
/// </para>
/// </remarks>
public static class HelpDocument
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .Build();

    /// <summary>Parses Markdown into blocks. Never throws on content; an empty string is an empty document.</summary>
    public static IReadOnlyList<HelpBlock> Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        MarkdownDocument document = Markdown.Parse(markdown, Pipeline);
        return Blocks(document);
    }

    /// <summary>
    /// The anchor a heading gets, the way github.com derives it: lower-cased, punctuation removed,
    /// spaces to hyphens — so a link written against the rendered document on GitHub resolves in
    /// the window too.
    /// </summary>
    public static string AnchorFor(string headingText)
    {
        ArgumentNullException.ThrowIfNull(headingText);

        StringBuilder anchor = new(headingText.Length);
        foreach (char c in headingText.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                anchor.Append(c);
            }
            else if (c == ' ' || c == '-')
            {
                anchor.Append('-');
            }
        }

        return anchor.ToString();
    }

    /// <summary>Every image the document shows, as figures and inline, in order.</summary>
    public static IReadOnlyList<string> ImageSources(IReadOnlyList<HelpBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        List<string> sources = [];
        Walk(blocks, block =>
        {
            if (block is HelpImage image)
            {
                sources.Add(image.Source);
            }
        }, inline =>
        {
            if (inline is HelpInlineImage image)
            {
                sources.Add(image.Source);
            }
        });

        return sources;
    }

    /// <summary>Every link target in the document, in order.</summary>
    public static IReadOnlyList<string> LinkTargets(IReadOnlyList<HelpBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        List<string> targets = [];
        Walk(blocks, _ => { }, inline =>
        {
            if (inline is HelpLink link)
            {
                targets.Add(link.Target);
            }
        });

        return targets;
    }

    /// <summary>The text of a run of inlines with the formatting dropped — a heading's, say.</summary>
    public static string PlainText(IReadOnlyList<HelpInline> inlines)
    {
        ArgumentNullException.ThrowIfNull(inlines);

        StringBuilder text = new();
        foreach (HelpInline inline in inlines)
        {
            switch (inline)
            {
                case HelpText t:
                    text.Append(t.Text);
                    break;
                case HelpLink link:
                    text.Append(PlainText(link.Content));
                    break;
                case HelpInlineImage image:
                    text.Append(image.AltText);
                    break;
                case HelpLineBreak:
                    text.Append(' ');
                    break;
                default:
                    break;
            }
        }

        return text.ToString();
    }

    private static void Walk(IReadOnlyList<HelpBlock> blocks, Action<HelpBlock> onBlock, Action<HelpInline> onInline)
    {
        foreach (HelpBlock block in blocks)
        {
            onBlock(block);
            switch (block)
            {
                case HelpParagraph paragraph:
                    WalkInlines(paragraph.Inlines, onInline);
                    break;
                case HelpList list:
                    foreach (IReadOnlyList<HelpBlock> item in list.Items)
                    {
                        Walk(item, onBlock, onInline);
                    }

                    break;
                case HelpTable table:
                    foreach (IReadOnlyList<HelpInline> cell in table.Header)
                    {
                        WalkInlines(cell, onInline);
                    }

                    foreach (IReadOnlyList<IReadOnlyList<HelpInline>> row in table.Rows)
                    {
                        foreach (IReadOnlyList<HelpInline> cell in row)
                        {
                            WalkInlines(cell, onInline);
                        }
                    }

                    break;
                case HelpQuote quote:
                    Walk(quote.Blocks, onBlock, onInline);
                    break;
                default:
                    break;
            }
        }
    }

    private static void WalkInlines(IReadOnlyList<HelpInline> inlines, Action<HelpInline> onInline)
    {
        foreach (HelpInline inline in inlines)
        {
            onInline(inline);
            if (inline is HelpLink link)
            {
                WalkInlines(link.Content, onInline);
            }
        }
    }

    private static IReadOnlyList<HelpBlock> Blocks(ContainerBlock container)
    {
        List<HelpBlock> blocks = [];
        foreach (Block block in container)
        {
            HelpBlock? converted = Convert(block);
            if (converted is not null)
            {
                blocks.Add(converted);
            }
        }

        return blocks;
    }

    private static HelpBlock? Convert(Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                string text = PlainText(Inlines(heading.Inline));
                return new HelpHeading(heading.Level, text, AnchorFor(text));
            }

            case ParagraphBlock paragraph:
            {
                IReadOnlyList<HelpInline> inlines = Inlines(paragraph.Inline);
                return inlines.Count == 1 && inlines[0] is HelpInlineImage image
                    ? new HelpImage(image.Source, image.AltText)
                    : new HelpParagraph(inlines);
            }

            case ListBlock list:
            {
                List<IReadOnlyList<HelpBlock>> items = [];
                foreach (Block item in list)
                {
                    if (item is ListItemBlock listItem)
                    {
                        items.Add(Blocks(listItem));
                    }
                }

                return new HelpList(list.IsOrdered, items);
            }

            case Table table:
            {
                List<IReadOnlyList<HelpInline>> header = [];
                List<IReadOnlyList<IReadOnlyList<HelpInline>>> rows = [];
                foreach (Block rowBlock in table)
                {
                    if (rowBlock is not TableRow row)
                    {
                        continue;
                    }

                    List<IReadOnlyList<HelpInline>> cells = [];
                    foreach (Block cellBlock in row)
                    {
                        cells.Add(cellBlock is TableCell cell ? CellInlines(cell) : []);
                    }

                    if (row.IsHeader && header.Count == 0)
                    {
                        header = cells;
                    }
                    else
                    {
                        rows.Add(cells);
                    }
                }

                return new HelpTable(header, rows);
            }

            case QuoteBlock quote:
                return new HelpQuote(Blocks(quote));

            case CodeBlock code:
                return new HelpCodeBlock(code.Lines.ToString().TrimEnd('\r', '\n'));

            case ThematicBreakBlock:
                return new HelpRule();

            default:
                // Raw HTML, link reference definitions and anything else the guide does not use.
                return null;
        }
    }

    private static IReadOnlyList<HelpInline> CellInlines(TableCell cell)
    {
        List<HelpInline> inlines = [];
        foreach (Block block in cell)
        {
            if (block is ParagraphBlock paragraph)
            {
                inlines.AddRange(Inlines(paragraph.Inline));
            }
        }

        return inlines;
    }

    private static IReadOnlyList<HelpInline> Inlines(ContainerInline? container, bool bold = false, bool italic = false)
    {
        List<HelpInline> inlines = [];
        if (container is null)
        {
            return inlines;
        }

        foreach (Inline inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    inlines.Add(new HelpText(literal.Content.ToString(), bold, italic));
                    break;

                case EmphasisInline emphasis:
                    inlines.AddRange(Inlines(
                        emphasis,
                        bold || emphasis.DelimiterCount >= 2,
                        italic || emphasis.DelimiterCount == 1));
                    break;

                case CodeInline code:
                    inlines.Add(new HelpText(code.Content, bold, italic, Code: true));
                    break;

                case LinkInline link when link.IsImage:
                    inlines.Add(new HelpInlineImage(link.Url ?? string.Empty, PlainText(Inlines(link))));
                    break;

                case LinkInline link:
                    inlines.Add(new HelpLink(Inlines(link, bold, italic), link.Url ?? string.Empty));
                    break;

                case AutolinkInline autolink:
                    inlines.Add(new HelpLink([new HelpText(autolink.Url, bold, italic)], autolink.Url));
                    break;

                case LineBreakInline lineBreak:
                    // A soft break is where the author wrapped the source, which is a space; a
                    // hard one is two trailing spaces, which is a line the author wants kept.
                    if (lineBreak.IsHard)
                    {
                        inlines.Add(new HelpLineBreak());
                    }
                    else
                    {
                        inlines.Add(new HelpText(" ", bold, italic));
                    }

                    break;

                case HtmlEntityInline entity:
                    inlines.Add(new HelpText(entity.Transcoded.ToString(), bold, italic));
                    break;

                case HtmlInline:
                    break;

                case ContainerInline nested:
                    inlines.AddRange(Inlines(nested, bold, italic));
                    break;

                default:
                    inlines.Add(new HelpText(inline.ToString() ?? string.Empty, bold, italic));
                    break;
            }
        }

        return inlines;
    }

    /// <summary>A one-based item number as the list shows it.</summary>
    public static string ItemLabel(bool ordered, int index) =>
        ordered ? (index + 1).ToString(CultureInfo.CurrentCulture) + "." : "•";
}
