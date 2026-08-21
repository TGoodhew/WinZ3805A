using System.Globalization;
using System.Xml.Linq;

namespace WinZ3805A.Controls;

/// <summary>
/// Reads the §9 design tokens out of <c>Themes/Colors.xaml</c> itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists so that no colour is written down twice.</b> Three places used to restate values
/// from the token dictionary in C# — the accent guard's semantic colours, the brand ramp, and the
/// tray icon's palette — because none of them can resolve a <c>ThemeResource</c>: the guard and the
/// ramp are compiled into a headless test assembly with no XAML runtime, and the tray hands the
/// shell a block of pixels rather than a brush. Each copy was checked against the XAML by a test,
/// which caught drift but did not prevent it, and left §9.13's "colours live in one file" true only
/// by inspection.
/// </para>
/// <para>
/// <b>The dictionary is embedded, not read from disk.</b> A packaged application's XAML has been
/// compiled into the binary by then, and there is no <c>Themes/Colors.xaml</c> beside the executable
/// to open. Embedding the source file gives every assembly that needs it the same bytes, and the
/// same bytes the XAML compiler saw.
/// </para>
/// <para>
/// <b>Unresolvable values come back as null rather than as a guess.</b> HighContrast maps most tokens
/// to <c>SystemColor*</c>, which are the user's own colours arriving from Windows and are not
/// knowable from the file. A caller that needs a real colour there has to ask the system for it —
/// see <c>TrayIcon</c>, which does exactly that.
/// </para>
/// </remarks>
public static class ThemePalette
{
    /// <summary>The logical name both projects embed the dictionary under.</summary>
    /// <remarks>
    /// Fixed explicitly in each <c>.csproj</c> rather than left to the default, which derives from
    /// the project's root namespace and would therefore differ between the application and the test
    /// assembly for what is deliberately the same file.
    /// </remarks>
    public const string ResourceName = "WinZ3805A.Themes.Colors.xaml";

    /// <summary>The Light theme dictionary's key.</summary>
    public const string Light = "Light";

    /// <summary>The Dark theme dictionary's key.</summary>
    public const string Dark = "Dark";

    /// <summary>The HighContrast theme dictionary's key.</summary>
    public const string HighContrast = "HighContrast";

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> Parsed =
        new(Parse, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The themes the dictionary defines.</summary>
    public static IReadOnlyCollection<string> Themes => (IReadOnlyCollection<string>)Parsed.Value.Keys;

    /// <summary>
    /// The colour a token resolves to in one theme, or null if it does not resolve to a fixed one.
    /// </summary>
    /// <param name="theme"><see cref="Light"/>, <see cref="Dark"/> or <see cref="HighContrast"/>.</param>
    /// <param name="key">
    /// A resource key, either a <c>Color</c> such as <c>WzAccentBase</c> or a <c>SolidColorBrush</c>
    /// such as <c>WzCriticalBrush</c>.
    /// </param>
    /// <returns>
    /// The colour, or null when the key is absent, or when it resolves to a <c>ThemeResource</c> —
    /// a system colour this file cannot know.
    /// </returns>
    public static Rgb? Colour(string theme, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(theme);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!Parsed.Value.TryGetValue(theme, out IReadOnlyDictionary<string, string>? tokens))
        {
            return null;
        }

        // Bounded rather than a visited set: StaticResource chains in this file are one hop, and a
        // cycle would be a malformed dictionary rather than something to resolve carefully.
        for (int hop = 0; hop < 8; hop++)
        {
            if (!tokens.TryGetValue(key, out string? value))
            {
                return null;
            }

            if (TryParseHex(value, out Rgb colour))
            {
                return colour;
            }

            // Color="{StaticResource WzAccentDark1}" - follow it within the same theme.
            if (Reference(value, "StaticResource") is string next)
            {
                key = next;
                continue;
            }

            // {ThemeResource ...}, or something unrecognised. Not knowable from here.
            return null;
        }

        return null;
    }

    /// <summary>The §9.4.3 brush token that carries a severity's colour.</summary>
    /// <remarks>
    /// The one place the enum is mapped to a resource key. Every consumer that needs a severity's
    /// actual colour — currently only the tray, since everything on screen uses
    /// <c>SeverityPill</c> and a <c>ThemeResource</c> — goes through this rather than spelling the
    /// key out.
    /// </remarks>
    public static string BrushKey(Severity severity) => severity switch
    {
        Severity.Success => "WzSuccessBrush",
        Severity.Caution => "WzCautionBrush",
        Severity.Critical => "WzCriticalBrush",
        Severity.Info => "WzInfoBrush",
        _ => "WzNeutralBrush",
    };

    /// <summary>Reads every theme's raw key-to-value pairs out of the embedded dictionary.</summary>
    /// <remarks>
    /// Values are kept as written and resolved on demand, so that a <c>StaticResource</c> reference
    /// is followed within whichever theme asked — the same key names different colours in Light and
    /// Dark, which is the entire point of a theme dictionary.
    /// </remarks>
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Parse()
    {
        Dictionary<string, IReadOnlyDictionary<string, string>> themes = [];

        try
        {
            using Stream? stream = typeof(ThemePalette).Assembly
                .GetManifestResourceStream(ResourceName);

            if (stream is null)
            {
                return themes;
            }

            XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

            foreach (XElement dictionary in XDocument.Load(stream)
                .Descendants(p + "ResourceDictionary")
                .Where(d => d.Attribute(x + "Key") is not null))
            {
                Dictionary<string, string> tokens = new(StringComparer.Ordinal);

                // <Color x:Key="WzAccentBase">#RRGGBB</Color>
                foreach (XElement colour in dictionary.Elements(p + "Color"))
                {
                    if (colour.Attribute(x + "Key")?.Value is string key)
                    {
                        tokens[key] = colour.Value.Trim();
                    }
                }

                // <SolidColorBrush x:Key="WzCriticalBrush" Color="#RRGGBB" />
                foreach (XElement brush in dictionary.Elements(p + "SolidColorBrush"))
                {
                    if (brush.Attribute(x + "Key")?.Value is string key
                        && brush.Attribute("Color")?.Value is string value)
                    {
                        tokens[key] = value.Trim();
                    }
                }

                themes[dictionary.Attribute(x + "Key")!.Value] = tokens;
            }
        }
        catch (Exception)
        {
            // A malformed or missing dictionary yields no tokens rather than an exception at first
            // use. Every caller already has to handle "this token has no fixed colour" because of
            // HighContrast, so there is no path here that a null does not already exercise - and
            // the alternative is a colour lookup taking the application down at startup.
            return themes;
        }

        return themes;
    }

    /// <summary>The key inside a <c>{StaticResource Key}</c> style reference, or null.</summary>
    private static string? Reference(string value, string kind)
    {
        string opening = "{" + kind;

        if (!value.StartsWith(opening, StringComparison.Ordinal)
            || !value.EndsWith('}'))
        {
            return null;
        }

        string inner = value[opening.Length..^1].Trim();

        return inner.Length == 0 ? null : inner;
    }

    /// <summary>Parses <c>#RRGGBB</c> or <c>#AARRGGBB</c>.</summary>
    /// <remarks>
    /// The alpha of an eight-digit value is discarded. Every consumer of this type wants a colour to
    /// draw with rather than a composite, and none of the tokens it reads is translucent.
    /// </remarks>
    private static bool TryParseHex(string value, out Rgb colour)
    {
        colour = default;

        if (value.Length is not (7 or 9) || value[0] != '#')
        {
            return false;
        }

        ReadOnlySpan<char> digits = value.AsSpan(value.Length == 9 ? 3 : 1);

        if (!byte.TryParse(digits[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r)
            || !byte.TryParse(digits[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g)
            || !byte.TryParse(digits[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            return false;
        }

        colour = new Rgb(r, g, b);
        return true;
    }
}
