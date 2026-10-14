using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdTable = Markdig.Extensions.Tables.Table;
using MdRow = Markdig.Extensions.Tables.TableRow;
using WpfList = System.Windows.Documents.List;

namespace SemanticAssistant.Desktop;

internal static class MarkdownFlowDocument
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().DisableHtml().Build();
    private static readonly Brush Ink = Brush("#263D50"), Muted = Brush("#667487"), CodeBackground = Brush("#EAF0F7");
    private static Brush Brush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }

    public static FlowDocument Empty() => new()
    {
        PagePadding = new Thickness(0), FontFamily = new FontFamily("Microsoft JhengHei UI"), FontSize = 14,
        LineHeight = 25, Foreground = Ink, IsHyphenationEnabled = false
    };

    public static FlowDocument Answer(string markdown)
    {
        var document = Empty(); Append(document.Blocks, markdown); return document;
    }

    public static void Append(BlockCollection destination, string markdown)
    {
        // Parse Markdown into document objects. Never load AI-authored HTML, XAML or remote images.
        var parsed = Markdown.Parse(markdown, Pipeline);
        var blocks = new Section();
        foreach (var block in parsed) RenderBlock(blocks.Blocks, block, markdown, 0);
        if (blocks.Blocks.Count == 0) blocks.Blocks.Add(Plain(markdown));
        destination.Add(blocks);
    }

    private static Paragraph Plain(string text) => new(new Run(text)) { Margin = new Thickness(0, 0, 0, 12) };

    private static void RenderBlock(BlockCollection output, MdBlock block, string source, int depth)
    {
        if (depth > 32) { output.Add(Plain(Source(block, source))); return; }
        switch (block)
        {
            case HeadingBlock heading:
                var title = new Paragraph
                {
                    FontSize = heading.Level switch { 1 => 28, 2 => 24, 3 => 20, _ => 17 },
                    FontWeight = FontWeights.Bold, Foreground = Ink, LineHeight = double.NaN,
                    Margin = new Thickness(0, heading.Level == 1 ? 2 : 20, 0, 12), KeepWithNext = true
                };
                RenderInlines(title.Inlines, heading.Inline, depth + 1); output.Add(title); break;
            case ParagraphBlock paragraph:
                var body = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
                RenderInlines(body.Inlines, paragraph.Inline, depth + 1); output.Add(body); break;
            case ListBlock list:
                var items = new WpfList
                {
                    MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Padding = new Thickness(26, 0, 0, 0), Margin = new Thickness(0, 0, 0, 14)
                };
                if (list.IsOrdered && int.TryParse(list.OrderedStart, NumberStyles.None, CultureInfo.InvariantCulture, out var start) && start > 0)
                    items.StartIndex = start;
                foreach (var child in list.OfType<ListItemBlock>())
                {
                    var item = new ListItem { Margin = new Thickness(0, 0, 0, 3) };
                    foreach (var nested in child) RenderBlock(item.Blocks, nested, source, depth + 1);
                    if (item.Blocks.Count == 0) item.Blocks.Add(new Paragraph());
                    foreach (var p in item.Blocks.OfType<Paragraph>()) p.Margin = new Thickness(0, 0, 0, list.IsLoose ? 8 : 2);
                    items.ListItems.Add(item);
                }
                output.Add(items); break;
            case CodeBlock code:
                output.Add(new Paragraph(new Run(code.Lines.ToString()))
                {
                    FontFamily = new FontFamily("Consolas"), FontSize = 13, LineHeight = 22,
                    Background = CodeBackground, BorderBrush = Brush("#D8E1ED"), BorderThickness = new Thickness(1),
                    Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 5, 0, 16)
                }); break;
            case QuoteBlock quote:
                var quoted = new Section
                {
                    BorderBrush = Brush("#A9BBD1"), BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(14, 5, 0, 2), Margin = new Thickness(0, 5, 0, 14), Foreground = Muted
                };
                foreach (var nested in quote) RenderBlock(quoted.Blocks, nested, source, depth + 1);
                output.Add(quoted); break;
            case MdTable table:
                var grid = new Table { CellSpacing = 0, FontSize = 13, Margin = new Thickness(0, 4, 0, 18) };
                var rowGroup = new TableRowGroup(); grid.RowGroups.Add(rowGroup);
                foreach (var row in table.OfType<MdRow>())
                {
                    var renderedRow = new TableRow(); rowGroup.Rows.Add(renderedRow);
                    foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
                    {
                        var renderedCell = new TableCell
                        {
                            Padding = new Thickness(10, 7, 10, 7), BorderBrush = Brush("#D8E1ED"),
                            BorderThickness = new Thickness(0, 0, 0, 1),
                            FontWeight = row.IsHeader ? FontWeights.Bold : FontWeights.Normal,
                            Background = row.IsHeader ? CodeBackground : Brushes.Transparent
                        };
                        foreach (var nested in cell) RenderBlock(renderedCell.Blocks, nested, source, depth + 1);
                        if (renderedCell.Blocks.Count == 0) renderedCell.Blocks.Add(new Paragraph());
                        foreach (var p in renderedCell.Blocks.OfType<Paragraph>()) p.Margin = new Thickness(0);
                        renderedRow.Cells.Add(renderedCell);
                    }
                }
                output.Add(grid); break;
            case ThematicBreakBlock:
                output.Add(new Paragraph { BorderBrush = Brush("#D8E1ED"), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 14, 0, 18) }); break;
            case LinkReferenceDefinitionGroup:
                break;
            case ContainerBlock container:
                foreach (var nested in container) RenderBlock(output, nested, source, depth + 1); break;
            default:
                output.Add(Plain(Source(block, source))); break;
        }
    }

    private static void RenderInlines(InlineCollection destination, ContainerInline? container, int depth)
    {
        if (container is null) return;
        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            if (depth > 32) { destination.Add(new Run(inline.ToString())); continue; }
            switch (inline)
            {
                case LiteralInline literal: destination.Add(new Run(literal.Content.ToString())); break;
                case LineBreakInline line:
                    if (line.IsHard) destination.Add(new LineBreak()); else destination.Add(new Run(" "));
                    break;
                case CodeInline code:
                    destination.Add(new Run(code.Content) { FontFamily = new FontFamily("Consolas"), Background = CodeBackground }); break;
                case AutolinkInline autolink:
                    destination.Add(new Run(autolink.Url) { Foreground = Brush("#0078D4") }); break;
                case EmphasisInline emphasis:
                    Span style = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                    RenderInlines(style.Inlines, emphasis, depth + 1); destination.Add(style); break;
                case LinkInline link:
                    if (link.IsImage)
                    {
                        var alternative = new Italic(); alternative.Inlines.Add(new Run("[圖片："));
                        RenderInlines(alternative.Inlines, link, depth + 1); alternative.Inlines.Add(new Run("]"));
                        destination.Add(alternative);
                    }
                    else if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                    {
                        var hyperlink = new Hyperlink { NavigateUri = uri, Foreground = Brush("#0078D4") };
                        RenderInlines(hyperlink.Inlines, link, depth + 1);
                        hyperlink.RequestNavigate += (_, e) =>
                        {
                            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                            catch { /* A broken link must not discard the answer. */ }
                            e.Handled = true;
                        };
                        destination.Add(hyperlink);
                    }
                    else RenderInlines(destination, link, depth + 1);
                    break;
                case ContainerInline nested: RenderInlines(destination, nested, depth + 1); break;
                default: destination.Add(new Run(inline.ToString())); break;
            }
        }
    }

    private static string Source(MdBlock block, string source)
    {
        var start = Math.Clamp(block.Span.Start, 0, source.Length);
        var end = Math.Clamp(block.Span.End + 1, start, source.Length);
        return source[start..end];
    }
}
