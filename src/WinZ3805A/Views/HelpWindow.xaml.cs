using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using Windows.ApplicationModel;
using Windows.Graphics;

using WinZ3805A.Services;

namespace WinZ3805A.Views;

/// <summary>
/// The user's guide — <c>docs/how-to-use.md</c>, shipped in the package — as a window (#312).
/// </summary>
/// <remarks>
/// <para>
/// §9.7.5 bound <c>F1</c> to an About that was never built. What a person pressing <c>F1</c>
/// wants is the guide, so that is what opens: the same document a visitor reads on github.com,
/// carried in the package so it works on a bench machine with no internet, laid out as a Fluent
/// surface rather than handed to a browser control — see <see cref="HelpDocument"/> for why.
/// </para>
/// <para>
/// One window, owned by <see cref="MainWindow"/> the way the Details window is, reachable from
/// either. It keeps no placement of its own: it opens at a reading size beside whatever the user
/// was doing and closes when the main window does.
/// </para>
/// </remarks>
public sealed partial class HelpWindow : Window
{
    /// <summary>Where the packaged guide lives, relative to the install folder.</summary>
    private const string GuideFolder = "Help";
    private const string GuideFile = "how-to-use.md";

    /// <summary>Content sizes, in effective pixels; see <see cref="WindowSizing"/>.</summary>
    private const int MinimumContentWidth = 480;
    private const int MinimumContentHeight = 400;
    private const int DefaultContentWidth = 840;
    private const int DefaultContentHeight = 900;

    private readonly Dictionary<string, FrameworkElement> _anchors = new(StringComparer.Ordinal);
    private readonly ScalingWatch _scaling;
    private SizeInt32 _minimum;

    /// <summary>Creates the window and lays the guide out.</summary>
    /// <param name="services">The §12 composition root, for the backdrop's logger.</param>
    public HelpWindow(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        InitializeComponent();

        WindowBackdrop.Apply(
            this,
            (Panel)Content,
            services.GetService<ILoggerFactory>()?.CreateLogger("Backdrop"));

        // §6.3: the display name is read from the manifest, never hard-coded.
        string displayName = Package.Current.DisplayName;
        Title = $"Help - {displayName}";
        AppTitleBar.Title = displayName;
        AppTitleBar.Subtitle = "Help";

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        Render(displayName);

        _scaling = new ScalingWatch(ApplyMinimumSize);
        ApplyMinimumSize();
        OpenAtReadingSize();

        if (Content is FrameworkElement root)
        {
            root.Loaded += (_, _) => _scaling.Watch(root.XamlRoot);
        }

        AppWindow.Changed += (_, args) =>
        {
            if (args.DidPositionChange)
            {
                ApplyMinimumSize();
            }
        };
    }

    private OverlappedPresenter? Presenter => AppWindow.Presenter as OverlappedPresenter;

    /// <summary>Brings the window to the user, restoring it if it was minimised (#46's lesson).</summary>
    public void BringToFront()
    {
        if (Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }

        Activate();
    }

    // ---------------------------------------------------------------------------------------
    // Layout
    // ---------------------------------------------------------------------------------------

    private void Render(string displayName)
    {
        Body.Children.Clear();
        _anchors.Clear();

        string path = Path.Combine(AppContext.BaseDirectory, GuideFolder, GuideFile);
        IReadOnlyList<HelpBlock> blocks = File.Exists(path)
            ? HelpDocument.Parse(File.ReadAllText(path))
            : HelpDocument.Parse("# How to use\n\nThis build does not carry the guide. It is `docs/how-to-use.md` in the repository.");

        foreach (HelpBlock block in blocks)
        {
            Body.Children.Add(Build(block));
        }

        // The About that §9.7.5 once promised, reduced to the line of it a person actually wants.
        PackageVersion version = Package.Current.Id.Version;
        Body.Children.Add(new TextBlock
        {
            Style = Style("HelpFooterStyle"),
            Text = $"{displayName} {version.Major}.{version.Minor}.{version.Build}.{version.Revision}",
            IsTextSelectionEnabled = true,
        });
    }

    private UIElement Build(HelpBlock block) => block switch
    {
        HelpHeading heading => Heading(heading),
        HelpParagraph paragraph => Paragraph(paragraph.Inlines),
        HelpImage image => Figure(image.Source, image.AltText),
        HelpList list => List(list),
        HelpTable table => Table(table),
        HelpQuote quote => Quote(quote),
        HelpCodeBlock code => CodeBlock(code.Text),
        HelpRule => new Border { Style = Style("HelpRuleStyle") },
        _ => new Border(),
    };

    private TextBlock Heading(HelpHeading heading)
    {
        TextBlock text = new()
        {
            Style = Style(heading.Level switch
            {
                1 => "HelpHeading1Style",
                2 => "HelpHeading2Style",
                3 => "HelpHeading3Style",
                _ => "HelpHeading4Style",
            }),
            Text = heading.Text,
        };

        AutomationProperties.SetHeadingLevel(text, heading.Level switch
        {
            1 => AutomationHeadingLevel.Level1,
            2 => AutomationHeadingLevel.Level2,
            3 => AutomationHeadingLevel.Level3,
            _ => AutomationHeadingLevel.Level4,
        });

        _anchors[heading.Anchor] = text;
        return text;
    }

    private RichTextBlock Paragraph(IReadOnlyList<HelpInline> inlines)
    {
        RichTextBlock text = new() { Style = Style("HelpBodyStyle") };
        Paragraph paragraph = new();
        AddInlines(paragraph.Inlines, inlines);
        text.Blocks.Add(paragraph);
        return text;
    }

    private void AddInlines(InlineCollection target, IReadOnlyList<HelpInline> inlines)
    {
        foreach (HelpInline inline in inlines)
        {
            switch (inline)
            {
                case HelpText text:
                    target.Add(Run(text));
                    break;

                case HelpLink link:
                    target.Add(Link(link));
                    break;

                case HelpInlineImage image:
                    target.Add(new InlineUIContainer { Child = Picture(image.Source, image.AltText) });
                    break;

                case HelpLineBreak:
                    target.Add(new LineBreak());
                    break;

                default:
                    break;
            }
        }
    }

    private Run Run(HelpText text)
    {
        Run run = new()
        {
            Text = text.Text,
            FontWeight = text.Bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            FontStyle = text.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
        };

        if (text.Code && Resource("WzMonoFontFamily") is FontFamily mono)
        {
            run.FontFamily = mono;
        }

        return run;
    }

    /// <remarks>
    /// An in-document link scrolls to its heading; anything with a scheme goes to the default
    /// browser through <c>NavigateUri</c>, which hands it to the shell rather than fetching it.
    /// A relative link to another repository document has nowhere to go from inside the package
    /// and is shown as text.
    /// </remarks>
    private Inline Link(HelpLink link)
    {
        Hyperlink hyperlink = new();
        AddInlines(hyperlink.Inlines, link.Content);

        if (link.Target.StartsWith('#'))
        {
            string anchor = link.Target[1..];
            hyperlink.Click += (_, _) =>
            {
                if (_anchors.TryGetValue(anchor, out FrameworkElement? element))
                {
                    element.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
                }
            };

            return hyperlink;
        }

        if (Uri.TryCreate(link.Target, UriKind.Absolute, out Uri? uri))
        {
            hyperlink.NavigateUri = uri;
            return hyperlink;
        }

        Span plain = new();
        AddInlines(plain.Inlines, link.Content);
        return plain;
    }

    private Image Figure(string source, string altText)
    {
        Image image = Picture(source, altText);
        image.Margin = (Thickness)Resource("WzSpaceXsThickness");
        return image;
    }

    /// <remarks>
    /// Never upscaled. The image is measured at whatever width the body has and capped at its
    /// own pixel width once that is known, so a 32 px badge stays 32 px and a page capture
    /// shrinks to fit a narrow window rather than the window growing to fit it.
    /// </remarks>
    private static Image Picture(string source, string altText)
    {
        BitmapImage bitmap = new(new Uri($"ms-appx:///{GuideFolder}/{source}"));
        Image image = new()
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 1,
        };

        image.ImageOpened += (_, _) => image.MaxWidth = Math.Max(1, bitmap.PixelWidth);
        image.ImageFailed += (_, _) => image.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(image, altText);
        return image;
    }

    private StackPanel List(HelpList list)
    {
        StackPanel panel = new() { Spacing = (double)Resource("WzSpaceXxs") };

        for (int index = 0; index < list.Items.Count; index++)
        {
            Grid row = new() { ColumnSpacing = (double)Resource("WzSpaceXs") };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            RichTextBlock marker = Paragraph([new HelpText(HelpDocument.ItemLabel(list.Ordered, index))]);
            marker.MinWidth = (double)Resource("WzSpaceMd");
            row.Children.Add(marker);

            StackPanel content = new() { Spacing = (double)Resource("WzSpaceXs") };
            foreach (HelpBlock block in list.Items[index])
            {
                content.Children.Add(Build(block));
            }

            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            panel.Children.Add(row);
        }

        return panel;
    }

    /// <remarks>
    /// Every column but the last sizes to its content and the last takes the rest, which is the
    /// shape of every table in the guide: short keys on the left, the explanation on the right.
    /// </remarks>
    private Border Table(HelpTable table)
    {
        int columns = Math.Max(table.Header.Count, table.Rows.Count == 0 ? 0 : table.Rows.Max(row => row.Count));
        Grid grid = new();
        for (int column = 0; column < columns; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = column == columns - 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
            });
        }

        int rowIndex = 0;
        if (table.Header.Count > 0)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddRow(grid, rowIndex++, table.Header, header: true, columns);
        }

        foreach (IReadOnlyList<IReadOnlyList<HelpInline>> row in table.Rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddRow(grid, rowIndex++, row, header: false, columns);
        }

        return new Border { Style = Style("HelpTableStyle"), Child = grid };
    }

    private void AddRow(Grid grid, int rowIndex, IReadOnlyList<IReadOnlyList<HelpInline>> cells, bool header, int columns)
    {
        for (int column = 0; column < columns; column++)
        {
            IReadOnlyList<HelpInline> content = column < cells.Count ? cells[column] : [];
            IReadOnlyList<HelpInline> shown = header
                ? content.Select(inline => inline is HelpText text ? text with { Bold = true } : inline).ToList()
                : content;

            RichTextBlock text = Paragraph(shown);
            if (column < columns - 1)
            {
                // An auto column must still wrap eventually, or one long cell widens the table
                // past the window.
                text.MaxWidth = 320;
            }

            Border cell = new()
            {
                Style = Style(header ? "HelpHeaderCellStyle" : "HelpCellStyle"),
                Child = text,
            };

            Grid.SetRow(cell, rowIndex);
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }
    }

    private Border Quote(HelpQuote quote)
    {
        StackPanel content = new() { Spacing = (double)Resource("WzSpaceXs") };
        foreach (HelpBlock block in quote.Blocks)
        {
            content.Children.Add(Build(block));
        }

        return new Border { Style = Style("HelpQuoteStyle"), Child = content };
    }

    private Border CodeBlock(string text) => new()
    {
        Style = Style("HelpCodeBlockStyle"),
        Child = new TextBlock
        {
            Style = Style("HelpCodeTextStyle"),
            Text = text,
            IsTextSelectionEnabled = true,
        },
    };

    private Style Style(string key) => (Style)((FrameworkElement)Content).Resources[key];

    private object Resource(string key) =>
        ((FrameworkElement)Content).Resources.TryGetValue(key, out object? local)
            ? local
            : Application.Current.Resources[key];

    // ---------------------------------------------------------------------------------------
    // Size
    // ---------------------------------------------------------------------------------------

    /// <remarks>
    /// The same three steps as the Details window: read the content size, convert it to a window
    /// size at this window's scaling and chrome, cap it at the display (#27, #101).
    /// </remarks>
    private void ApplyMinimumSize()
    {
        double scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
        int chromeWidth = Math.Max(0, AppWindow.Size.Width - AppWindow.ClientSize.Width);
        int chromeHeight = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);

        (int width, int height) = WindowSizing.PhysicalMinimum(
            MinimumContentWidth, MinimumContentHeight, scale, chromeWidth, chromeHeight);

        (width, height) = WindowSizing.ClampToWorkArea(
            width, height, DisplayWorkAreas.ForWindow(AppWindow));

        _minimum = new SizeInt32(width, height);

        if (Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = width;
            presenter.PreferredMinimumHeight = height;
        }

        if (AppWindow.Size.Width < width || AppWindow.Size.Height < height)
        {
            AppWindow.Resize(new SizeInt32(
                Math.Max(AppWindow.Size.Width, width),
                Math.Max(AppWindow.Size.Height, height)));
        }
    }

    /// <summary>A reading size: the body's width plus its margins, and most of a display's height.</summary>
    private void OpenAtReadingSize()
    {
        double scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
        int chromeWidth = Math.Max(0, AppWindow.Size.Width - AppWindow.ClientSize.Width);
        int chromeHeight = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);

        (int width, int height) = WindowSizing.PhysicalMinimum(
            DefaultContentWidth, DefaultContentHeight, scale, chromeWidth, chromeHeight);

        (width, height) = WindowSizing.ClampToWorkArea(
            width, height, DisplayWorkAreas.ForWindow(AppWindow));

        AppWindow.Resize(new SizeInt32(Math.Max(width, _minimum.Width), Math.Max(height, _minimum.Height)));
    }
}
