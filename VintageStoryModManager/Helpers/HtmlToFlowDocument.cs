using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using HtmlAgilityPack;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using HtmlDocument = HtmlAgilityPack.HtmlDocument;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace VintageStoryModManager.Helpers;

/// <summary>
/// Converts an HTML fragment (such as a mod database description) into a WPF
/// <see cref="FlowDocument"/> so it can be rendered with rich formatting
/// (headings, lists, links, images, bold/italic, etc.) using only built-in WPF.
/// </summary>
public static class HtmlToFlowDocument
{
    private const string BaseUrl = "https://mods.vintagestory.at";
    private const double MaxImageWidth = 640;

    private static readonly SolidColorBrush LinkBrush = CreateFrozenBrush(Color.FromRgb(0x4E, 0xA1, 0xF7));
    private static readonly SolidColorBrush CodeBackground = CreateFrozenBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80));
    private static readonly SolidColorBrush QuoteBorder = CreateFrozenBrush(Color.FromArgb(0x80, 0x80, 0x80, 0x80));
    private static readonly SolidColorBrush RuleBrush = CreateFrozenBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x80));

    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "header", "footer", "main", "aside",
        "figure", "figcaption", "h1", "h2", "h3", "h4", "h5", "h6",
        "ul", "ol", "li", "blockquote", "pre", "hr", "table",
    };

    /// <summary>
    /// Builds a <see cref="FlowDocument"/> from the supplied HTML.
    /// </summary>
    /// <param name="html">The HTML fragment to render.</param>
    /// <param name="foreground">Foreground brush used for the document text.</param>
    /// <param name="fontFamily">Base font family.</param>
    /// <param name="fontSize">Base font size.</param>
    public static FlowDocument Convert(string? html, Brush? foreground, FontFamily? fontFamily, double fontSize)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(24, 16, 24, 20),
            FontSize = fontSize > 0 ? fontSize : 14,
        };

        if (foreground != null)
            document.Foreground = foreground;

        if (fontFamily != null)
            document.FontFamily = fontFamily;

        if (string.IsNullOrWhiteSpace(html))
            return document;

        try
        {
            var htmlDocument = new HtmlDocument { OptionFixNestedTags = true };
            htmlDocument.LoadHtml(html);

            foreach (var block in ParseBlocks(htmlDocument.DocumentNode.ChildNodes))
                document.Blocks.Add(block);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HtmlToFlowDocument] Failed to render HTML: {ex.Message}");
            document.Blocks.Add(new Paragraph(new Run(StripTags(html))));
        }

        return document;
    }

    private static List<Block> ParseBlocks(HtmlNodeCollection? nodes)
    {
        var blocks = new List<Block>();
        if (nodes == null)
            return blocks;

        List<Inline>? pending = null;

        void FlushPending()
        {
            if (pending == null)
                return;

            if (InlinesHaveContent(pending))
            {
                var paragraph = new Paragraph();
                foreach (var inline in pending)
                    paragraph.Inlines.Add(inline);
                blocks.Add(paragraph);
            }

            pending = null;
        }

        foreach (var node in nodes)
        {
            if (node.NodeType == HtmlNodeType.Element && BlockElements.Contains(node.Name))
            {
                FlushPending();
                blocks.AddRange(ParseBlockElement(node));
            }
            else
            {
                pending ??= new List<Inline>();
                pending.AddRange(ParseInlines(node));
            }
        }

        FlushPending();
        return blocks;
    }

    private static IEnumerable<Block> ParseBlockElement(HtmlNode node)
    {
        var name = node.Name.ToLowerInvariant();

        switch (name)
        {
            case "h1": return new[] { CreateHeading(node, 24) };
            case "h2": return new[] { CreateHeading(node, 21) };
            case "h3": return new[] { CreateHeading(node, 18) };
            case "h4": return new[] { CreateHeading(node, 16) };
            case "h5": return new[] { CreateHeading(node, 14) };
            case "h6": return new[] { CreateHeading(node, 13) };

            case "ul": return new Block[] { CreateList(node, TextMarkerStyle.Disc) };
            case "ol": return new Block[] { CreateList(node, TextMarkerStyle.Decimal) };

            case "li":
                // A stray <li> outside a list – render as a plain paragraph.
                return ParseBlocks(node.ChildNodes);

            case "blockquote": return new Block[] { CreateBlockquote(node) };
            case "pre": return new[] { CreatePreformatted(node) };
            case "hr": return new Block[] { CreateHorizontalRule() };
            case "table": return new Block[] { CreateTable(node) };

            default:
                // Structural containers (div, section, p, figure, ...) – recurse.
                return ParseBlocks(node.ChildNodes);
        }
    }

    private static Paragraph CreateHeading(HtmlNode node, double fontSize)
    {
        var paragraph = new Paragraph
        {
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 6),
        };

        foreach (var inline in ParseInlines(node))
            paragraph.Inlines.Add(inline);

        return paragraph;
    }

    private static List CreateList(HtmlNode node, TextMarkerStyle markerStyle)
    {
        var list = new List
        {
            MarkerStyle = markerStyle,
            Margin = new Thickness(0, 4, 0, 8),
            Padding = new Thickness(20, 0, 0, 0),
        };

        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType != HtmlNodeType.Element || !string.Equals(child.Name, "li", StringComparison.OrdinalIgnoreCase))
                continue;

            var listItem = new ListItem();
            var blocks = ParseBlocks(child.ChildNodes);

            if (blocks.Count == 0)
                blocks.Add(new Paragraph());

            foreach (var block in blocks)
                listItem.Blocks.Add(block);

            list.ListItems.Add(listItem);
        }

        return list;
    }

    private static Section CreateBlockquote(HtmlNode node)
    {
        var section = new Section
        {
            Margin = new Thickness(2, 4, 0, 8),
            Padding = new Thickness(12, 0, 0, 0),
            BorderBrush = QuoteBorder,
            BorderThickness = new Thickness(3, 0, 0, 0),
        };

        foreach (var block in ParseBlocks(node.ChildNodes))
            section.Blocks.Add(block);

        return section;
    }

    private static Paragraph CreatePreformatted(HtmlNode node)
    {
        var paragraph = new Paragraph
        {
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            Background = CodeBackground,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 4, 0, 8),
        };

        var text = HtmlEntity.DeEntitize(node.InnerText) ?? string.Empty;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            paragraph.Inlines.Add(new Run(lines[i]));
            if (i < lines.Length - 1)
                paragraph.Inlines.Add(new LineBreak());
        }

        return paragraph;
    }

    private static BlockUIContainer CreateHorizontalRule()
    {
        var rule = new System.Windows.Controls.Border
        {
            Height = 1,
            Background = RuleBrush,
            Margin = new Thickness(0, 8, 0, 8),
        };

        return new BlockUIContainer(rule);
    }

    private static Table CreateTable(HtmlNode node)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 8) };
        var rowGroup = new TableRowGroup();
        var columnCount = 0;

        foreach (var rowNode in node.Descendants("tr"))
        {
            var row = new TableRow();
            var cellNodes = rowNode.ChildNodes
                .Where(c => c.NodeType == HtmlNodeType.Element
                    && (string.Equals(c.Name, "td", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c.Name, "th", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var cellNode in cellNodes)
            {
                var isHeader = string.Equals(cellNode.Name, "th", StringComparison.OrdinalIgnoreCase);
                var cell = new TableCell
                {
                    Padding = new Thickness(8, 4, 8, 4),
                    BorderBrush = QuoteBorder,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                };

                foreach (var block in ParseBlocks(cellNode.ChildNodes))
                {
                    if (isHeader)
                        block.FontWeight = FontWeights.SemiBold;
                    cell.Blocks.Add(block);
                }

                if (cell.Blocks.Count == 0)
                    cell.Blocks.Add(new Paragraph());

                row.Cells.Add(cell);
            }

            columnCount = Math.Max(columnCount, cellNodes.Count);
            if (row.Cells.Count > 0)
                rowGroup.Rows.Add(row);
        }

        for (var i = 0; i < columnCount; i++)
            table.Columns.Add(new TableColumn());

        table.RowGroups.Add(rowGroup);
        return table;
    }

    private static IEnumerable<Inline> ParseInlines(HtmlNode node)
    {
        if (node.NodeType == HtmlNodeType.Text)
        {
            var text = NormalizeText(HtmlEntity.DeEntitize(node.InnerText));
            if (text.Length > 0)
                yield return new Run(text);

            yield break;
        }

        if (node.NodeType != HtmlNodeType.Element)
            yield break;

        var name = node.Name.ToLowerInvariant();

        switch (name)
        {
            case "br":
                yield return new LineBreak();
                break;

            case "b":
            case "strong":
                yield return WrapChildren(node, new Bold());
                break;

            case "i":
            case "em":
                yield return WrapChildren(node, new Italic());
                break;

            case "u":
            case "ins":
                yield return WrapChildren(node, new Underline());
                break;

            case "s":
            case "strike":
            case "del":
            {
                var span = new Span { TextDecorations = TextDecorations.Strikethrough };
                yield return WrapChildren(node, span);
                break;
            }

            case "code":
            case "tt":
            case "kbd":
            case "samp":
            {
                var span = new Span
                {
                    FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                    Background = CodeBackground,
                };
                yield return WrapChildren(node, span);
                break;
            }

            case "a":
                yield return CreateHyperlink(node);
                break;

            case "img":
            {
                var image = CreateImage(node);
                if (image != null)
                    yield return image;
                break;
            }

            case "script":
            case "style":
                break;

            default:
                // span, font, small, sup, sub, mark, label, abbr and unknown
                // tags: render their children inline.
                yield return WrapChildren(node, new Span());
                break;
        }
    }

    private static Span WrapChildren(HtmlNode node, Span span)
    {
        foreach (var inline in ParseChildInlines(node))
            span.Inlines.Add(inline);

        return span;
    }

    private static IEnumerable<Inline> ParseChildInlines(HtmlNode node)
    {
        foreach (var child in node.ChildNodes)
        {
            foreach (var inline in ParseInlines(child))
                yield return inline;
        }
    }

    private static Inline CreateHyperlink(HtmlNode node)
    {
        var hyperlink = new Hyperlink { Foreground = LinkBrush };

        foreach (var inline in ParseChildInlines(node))
            hyperlink.Inlines.Add(inline);

        if (hyperlink.Inlines.Count == 0)
            hyperlink.Inlines.Add(new Run(node.GetAttributeValue("href", string.Empty)));

        var target = ResolveUri(node.Attributes["href"]?.Value);
        if (target != null)
        {
            hyperlink.NavigateUri = target;
            hyperlink.ToolTip = target.ToString();
            hyperlink.RequestNavigate += OnRequestNavigate;
        }

        return hyperlink;
    }

    private static Inline? CreateImage(HtmlNode node)
    {
        var uri = ResolveUri(node.Attributes["src"]?.Value);
        if (uri == null)
            return null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.EndInit();

            var image = new System.Windows.Controls.Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            var width = ReadImageDimension(node, "width");
            var height = ReadImageDimension(node, "height");

            if (width.HasValue || height.HasValue)
            {
                // Honor the HTML-specified size so table icons stay small, just
                // like they render on the mod DB page.
                if (width.HasValue)
                    image.Width = Math.Min(width.Value, MaxImageWidth);
                if (height.HasValue)
                    image.Height = height.Value;
                image.StretchDirection = System.Windows.Controls.StretchDirection.Both;
            }
            else
            {
                // No explicit size: cap large images but never upscale small ones.
                image.MaxWidth = MaxImageWidth;
                image.StretchDirection = System.Windows.Controls.StretchDirection.DownOnly;
            }

            return new InlineUIContainer(image);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HtmlToFlowDocument] Failed to load image '{uri}': {ex.Message}");
            return null;
        }
    }

    private static double? ReadImageDimension(HtmlNode node, string name)
    {
        // Prefer an inline style (e.g. style="width: 32px") then fall back to the attribute.
        var style = node.Attributes["style"]?.Value;
        if (!string.IsNullOrEmpty(style))
        {
            var match = Regex.Match(
                style,
                $@"(?:^|;)\s*{name}\s*:\s*(\d+(?:\.\d+)?)\s*px",
                RegexOptions.IgnoreCase);
            if (match.Success &&
                double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var styleValue) &&
                styleValue > 0)
                return styleValue;
        }

        var attribute = node.Attributes[name]?.Value?.Trim();
        if (string.IsNullOrEmpty(attribute) || attribute.EndsWith("%", StringComparison.Ordinal))
            return null;

        attribute = attribute.Replace("px", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        if (double.TryParse(attribute, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0)
            return value;

        return null;
    }

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HtmlToFlowDocument] Failed to open link '{e.Uri}': {ex.Message}");
        }

        e.Handled = true;
    }

    private static bool InlinesHaveContent(IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run when !string.IsNullOrWhiteSpace(run.Text):
                case LineBreak:
                case InlineUIContainer:
                    return true;
                case Span span when InlinesHaveContent(span.Inlines):
                    return true;
            }
        }

        return false;
    }

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        var previousWasWhitespace = false;

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWasWhitespace)
                    builder.Append(' ');
                previousWasWhitespace = true;
            }
            else
            {
                builder.Append(ch);
                previousWasWhitespace = false;
            }
        }

        return builder.ToString();
    }

    private static Uri? ResolveUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();

        if (trimmed.StartsWith("//", StringComparison.Ordinal))
            trimmed = "https:" + trimmed;
        else if (trimmed.StartsWith("/", StringComparison.Ordinal))
            trimmed = BaseUrl + trimmed;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute;
        }

        return null;
    }

    private static string StripTags(string html)
    {
        try
        {
            var document = new HtmlDocument();
            document.LoadHtml(html);
            return HtmlEntity.DeEntitize(document.DocumentNode.InnerText)?.Trim() ?? string.Empty;
        }
        catch
        {
            return html;
        }
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
