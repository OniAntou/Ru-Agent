using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace RuApp.Controls
{
    public sealed partial class MarkdownBlock : UserControl
    {
        private const double MonoFontSize = 13;
        private const int MaxTableRows = 50;

        private static readonly FontFamily UiFont = new FontFamily("ms-appx:///Assets/Fonts/Inter-Variable.ttf#Inter, Segoe UI Variable, Segoe UI");
        private static readonly FontFamily DisplayFont = new FontFamily("ms-appx:///Assets/Fonts/Inter-Variable.ttf#Inter, Segoe UI Variable, Segoe UI");
        private static readonly FontFamily MonoFont = new FontFamily("Cascadia Code, Consolas");
        private readonly RichTextBlock _richTextBlock;

        public static readonly DependencyProperty MarkdownTextProperty =
            DependencyProperty.Register(
                nameof(MarkdownText),
                typeof(string),
                typeof(MarkdownBlock),
                new PropertyMetadata(string.Empty, OnMarkdownTextChanged));

        public string MarkdownText
        {
            get => (string)GetValue(MarkdownTextProperty);
            set => SetValue(MarkdownTextProperty, value);
        }

        public FontWeight FontWeightProperties => FontWeights.Normal;

        public MarkdownBlock()
        {
            _richTextBlock = new RichTextBlock
            {
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
                FontFamily = UiFont
            };
            RegisterPropertyChangedCallback(Control.ForegroundProperty, (_, _) =>
            {
                _richTextBlock.Foreground = Foreground;
            });
            RegisterPropertyChangedCallback(Control.FontSizeProperty, (_, _) => RenderMarkdown());
            Content = _richTextBlock;
        }

        private static void OnMarkdownTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((MarkdownBlock)d).RenderMarkdown();
        }

        private string? _lastRenderedText;
        private double _lastRenderedSize;

        private void RenderMarkdown()
        {
            string source = (MarkdownText ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            double bodySize = FontSize > 0 ? FontSize : 14;

            if (string.Equals(source, _lastRenderedText, StringComparison.Ordinal) && Math.Abs(bodySize - _lastRenderedSize) < 0.01)
            {
                return;
            }
            _lastRenderedText = source;
            _lastRenderedSize = bodySize;

            _richTextBlock.Blocks.Clear();
            _richTextBlock.FontFamily = UiFont;
            _richTextBlock.FontSize = bodySize;

            // Collapsed pre-fix walls arrive as single overlong lines; recover
            // their section breaks first so the parser below sees real blocks.
            // Fences pass through untouched so recovery never splits code.
            var recovered = new List<string>();
            foreach (string rawLine in source.Split('\n'))
            {
                if (rawLine.Trim().StartsWith("```", StringComparison.Ordinal))
                {
                    recovered.Add(rawLine);
                    continue;
                }

                recovered.AddRange(Services.MarkdownRecovery.SplitLine(rawLine.Trim()));
            }

            var renderer = new BlockRenderer(bodySize, Foreground);
            foreach (var block in MarkdownEngine.Parse(string.Join('\n', recovered)))
            {
                foreach (var paragraph in renderer.BuildBlocks(block, indent: 0))
                {
                    _richTextBlock.Blocks.Add(paragraph);
                }
            }
        }

        /// <summary>
        /// Walks a Markdig document into RichText paragraphs. Unknown leaves fall
        /// back to their literal text so a new extension degrades to visible
        /// prose, never to a swallowed reply.
        /// </summary>
        private sealed class BlockRenderer(double bodySize, Brush? foreground)
        {
            /// <summary>
            /// DeepSeek-like air: line height tracks the reader's size choice
            /// instead of sitting on a fixed 22px that pinches at 17.
            /// </summary>
            private double TextLineHeight => Math.Round(bodySize * 1.7);

            public List<Paragraph> BuildBlocks(Markdig.Syntax.Block block, int indent)
            {
                return block switch
                {
                    HeadingBlock heading => new List<Paragraph> { Heading(heading, indent) },
                    ParagraphBlock paragraph => new List<Paragraph> { Prose(paragraph.Inline, indent, bottom: 12) },
                    QuoteBlock quote => Quote(quote, indent),
                    ListBlock list => BuildList(list, indent),
                    CodeBlock code => new List<Paragraph> { Code(code) },
                    ThematicBreakBlock => new List<Paragraph> { Rule(indent) },
                    HtmlBlock html => new List<Paragraph> { Html(html, indent) },
                    Markdig.Extensions.Tables.Table table => new List<Paragraph> { Table(table, indent) },
                    LeafBlock leaf => new List<Paragraph> { Literal(leaf, indent) },
                    ContainerBlock container => Children(container, indent),
                    _ => new List<Paragraph>(),
                };
            }

            public List<Paragraph> BuildList(ListBlock list, int indent)
            {
                var rows = new List<Paragraph>();
                int number = 1;
                foreach (var item in list)
                {
                    if (item is not Markdig.Syntax.ListItemBlock listItem)
                    {
                        continue;
                    }

                    string marker = list.IsOrdered ? $"{number}.  " : "\u2022  ";
                    number++;
                    rows.AddRange(BuildListItem(listItem, marker, indent));
                }

                return rows;
            }

            private List<Paragraph> BuildListItem(Markdig.Syntax.ListItemBlock listItem, string marker, int indent)
            {
                var rows = new List<Paragraph>();
                Paragraph? current = null;
                void Flush()
                {
                    if (current is not null)
                    {
                        rows.Add(current);
                        current = null;
                    }
                }

                Paragraph Current()
                {
                    if (current is null)
                    {
                        current = new Paragraph
                        {
                            Margin = new Thickness(16 + indent * 20, 0, 0, 8),
                            LineHeight = TextLineHeight,
                        };
                        if (rows.Count == 0)
                        {
                            current.Inlines.Add(new Run { Text = marker, FontFamily = UiFont, FontSize = bodySize });
                        }
                    }

                    return current;
                }

                foreach (var child in listItem)
                {
                    if (child is ParagraphBlock itemPara)
                    {
                        AddInlines(Current(), itemPara.Inline, bodySize, null, UiFont);
                    }
                    else if (child is ListBlock nested)
                    {
                        Flush();
                        rows.AddRange(BuildList(nested, indent + 1));
                    }
                    else
                    {
                        foreach (var extra in BuildBlocks(child, indent + 1))
                        {
                            foreach (var inline in extra.Inlines.ToArray())
                            {
                                Current().Inlines.Add(inline);
                            }
                        }
                    }
                }

                Flush();
                return rows;
            }

            private Paragraph Heading(HeadingBlock heading, int indent)
            {
                var paragraph = new Paragraph
                {
                    Margin = heading.Level switch
                    {
                        1 => new Thickness(0, 18, 0, 8),
                        2 => new Thickness(0, 16, 0, 6),
                        _ => new Thickness(0, 14, 0, 6),
                    },
                    LineHeight = TextLineHeight,
                };
                double size = heading.Level switch
                {
                    1 => bodySize + 1,
                    _ => bodySize,
                };
                var weight = heading.Level <= 2 ? FontWeights.Bold : FontWeights.SemiBold;
                AddInlines(paragraph, heading.Inline, size, weight, DisplayFont);
                return paragraph;
            }

            private Paragraph Prose(ContainerInline? inline, int indent, double bottom)
            {
                var paragraph = new Paragraph
                {
                    Margin = new Thickness(indent * 20, 0, 0, bottom),
                    LineHeight = TextLineHeight,
                };
                AddInlines(paragraph, inline, bodySize, null, UiFont);
                return paragraph;
            }

            private List<Paragraph> Quote(QuoteBlock quote, int indent)
            {
                var rows = new List<Paragraph>();
                foreach (var child in quote)
                {
                    foreach (var row in BuildBlocks(child, indent))
                    {
                        row.Margin = new Thickness(16 + indent * 20, row.Margin.Top, row.Margin.Right, row.Margin.Bottom);
                        row.Foreground = MutedOrDefault();
                        rows.Add(row);
                    }
                }

                return rows;
            }

            private Paragraph Code(CodeBlock code)
            {
                string text = code.Lines.ToString().TrimEnd('\r', '\n');
                string? language = code is FencedCodeBlock fenced ? fenced.Info?.Trim() : null;
                var paragraph = new Paragraph { Margin = new Thickness(10, 4, 10, 8) };
                foreach (var span in CodeHighlighter.Highlight(text, language))
                {
                    var run = new Run { Text = span.Text, FontFamily = MonoFont, FontSize = MonoFontSize };
                    var brush = SpanBrush(span.Kind);
                    if (brush is not null)
                    {
                        run.Foreground = brush;
                    }

                    paragraph.Inlines.Add(run);
                }

                return paragraph;
            }

            private Brush? SpanBrush(CodeHighlighter.SpanKind kind) => kind switch
            {
                CodeHighlighter.SpanKind.Keyword => ThemeBrush("RuVioletBrush", 126, 34, 206),
                CodeHighlighter.SpanKind.String => ThemeBrush("RuSuccessBrush", 4, 120, 87),
                CodeHighlighter.SpanKind.Added => ThemeBrush("RuSuccessBrush", 4, 120, 87),
                CodeHighlighter.SpanKind.Comment => MutedOrDefault(),
                CodeHighlighter.SpanKind.Number => ThemeBrush("RuInfoBrush", 7, 89, 133),
                CodeHighlighter.SpanKind.HunkHeader => ThemeBrush("RuInfoBrush", 7, 89, 133),
                CodeHighlighter.SpanKind.Removed => ThemeBrush("RuDangerBrush", 185, 28, 28),
                _ => null,
            };

            private static SolidColorBrush ThemeBrush(string key, byte r, byte g, byte b) =>
                Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is SolidColorBrush brush
                    ? brush
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));

            private Paragraph Rule(int indent)
            {
                var paragraph = new Paragraph { Margin = new Thickness(indent * 20, 4, 0, 10) };
                paragraph.Inlines.Add(new Run { Text = "\u2500\u2500\u2500", FontFamily = UiFont, FontSize = bodySize, Foreground = MutedOrDefault() });
                return paragraph;
            }

            private Paragraph Html(HtmlBlock html, int indent)
            {
                var paragraph = new Paragraph { Margin = new Thickness(indent * 20, 0, 0, 10), LineHeight = TextLineHeight };
                paragraph.Inlines.Add(new Run { Text = html.Lines.ToString().Trim(), FontFamily = MonoFont, FontSize = MonoFontSize });
                return paragraph;
            }

            private Paragraph Literal(LeafBlock leaf, int indent)
            {
                var paragraph = new Paragraph { Margin = new Thickness(indent * 20, 0, 0, 10), LineHeight = TextLineHeight };
                paragraph.Inlines.Add(new Run { Text = leaf.Lines.ToString().Trim(), FontFamily = UiFont, FontSize = bodySize });
                return paragraph;
            }

            private List<Paragraph> Children(ContainerBlock container, int indent)
            {
                var rows = new List<Paragraph>();
                foreach (var child in container)
                {
                    rows.AddRange(BuildBlocks(child, indent));
                }

                return rows;
            }

            private const int MaxTableColumns = 8;

            private Paragraph Table(Markdig.Extensions.Tables.Table table, int indent)
            {
                var rows = new List<IReadOnlyList<string>>();
                foreach (var rowBlock in table)
                {
                    if (rowBlock is not Markdig.Extensions.Tables.TableRow row)
                    {
                        continue;
                    }

                    var cells = new List<string>();
                    foreach (var cellBlock in row)
                    {
                        if (cellBlock is Markdig.Extensions.Tables.TableCell cell)
                        {
                            cells.Add(CellText(cell).Replace('\n', ' ').Trim());
                        }
                    }

                    rows.Add(cells);
                    if (rows.Count >= MaxTableRows)
                    {
                        break;
                    }
                }

                int columns = 0;
                foreach (var row in rows)
                {
                    columns = Math.Max(columns, row.Count);
                }

                columns = Math.Min(columns, MaxTableColumns);
                var line = ThemeBrush("RuLineBrush", 203, 213, 225);
                var grid = new Grid();
                for (int index = 0; index < columns; index++)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                }

                for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    bool header = rowIndex == 0;
                    for (int column = 0; column < columns; column++)
                    {
                        string cell = column < rows[rowIndex].Count ? rows[rowIndex][column] : string.Empty;
                        var text = new TextBlock
                        {
                            Text = cell,
                            FontFamily = UiFont,
                            FontSize = bodySize,
                            TextWrapping = TextWrapping.Wrap,
                        };
                        if (header)
                        {
                            text.FontWeight = FontWeights.SemiBold;
                        }

                        var frame = new Border
                        {
                            BorderBrush = line,
                            BorderThickness = new Thickness(0, 0, column < columns - 1 ? 1 : 0, header || rowIndex < rows.Count - 1 ? 1 : 0),
                            Padding = new Thickness(8, 4, 8, 4),
                            Child = text,
                        };
                        Grid.SetRow(frame, rowIndex);
                        Grid.SetColumn(frame, column);
                        grid.Children.Add(frame);
                    }
                }

                var outer = new Border
                {
                    BorderBrush = line,
                    BorderThickness = new Thickness(1),
                    CornerRadius = Services.UiCornerRadius.Control,
                    Child = grid,
                };
                var paragraph = new Paragraph { Margin = new Thickness(indent * 20, 4, 0, 10) };
                paragraph.Inlines.Add(new InlineUIContainer { Child = outer });
                return paragraph;
            }

            private static string CellText(Markdig.Extensions.Tables.TableCell cell)
            {
                var builder = new System.Text.StringBuilder();
                foreach (var descendant in cell.Descendants())
                {
                    if (descendant is ParagraphBlock cellPara && cellPara.Inline is not null)
                    {
                        foreach (var inline in cellPara.Inline)
                        {
                            switch (inline)
                            {
                                case LiteralInline literal:
                                    builder.Append(literal.Content.ToString());
                                    break;
                                case CodeInline code:
                                    builder.Append(code.Content);
                                    break;
                                case LineBreakInline:
                                    builder.Append(' ');
                                    break;
                            }
                        }
                    }
                }

                return builder.ToString();
            }

            private Brush MutedOrDefault() =>
                Application.Current?.Resources.TryGetValue("RuMutedBrush", out object? muted) == true && muted is Brush brush
                    ? brush
                    : foreground ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 148, 163, 184));

            private void AddInlines(Paragraph paragraph, ContainerInline? parent, double size, FontWeight? weight, FontFamily fontFamily)
            {
                if (parent is null)
                {
                    return;
                }

                foreach (var inline in parent)
                {
                    AddInline(paragraph, inline, size, weight, fontFamily);
                }
            }

            private void AddInline(Paragraph paragraph, Markdig.Syntax.Inlines.Inline inline, double size, FontWeight? weight, FontFamily fontFamily)
            {
                switch (inline)
                {
                    case TaskList task:
                        paragraph.Inlines.Add(new Run
                        {
                            Text = task.Checked ? "\u2612  " : "\u2610  ",
                            FontFamily = UiFont,
                            FontSize = size,
                        });
                        break;
                    case LiteralInline literal:
                        paragraph.Inlines.Add(CreateRun(literal.Content.ToString(), size, weight, fontFamily));
                        break;
                    case LineBreakInline lineBreak:
                        if (lineBreak.IsHard)
                        {
                            paragraph.Inlines.Add(new LineBreak());
                        }
                        else
                        {
                            paragraph.Inlines.Add(CreateRun(" ", size, weight, fontFamily));
                        }

                        break;
                    case CodeInline code:
                        paragraph.Inlines.Add(new Run { Text = code.Content, FontSize = MonoFontSize, FontFamily = MonoFont });
                        break;
                    case AutolinkInline auto:
                        AddAutolink(paragraph, auto, size, weight, fontFamily);
                        break;
                    case LinkInline link:
                        AddLink(paragraph, link, size, weight, fontFamily);
                        break;
                    case EmphasisInline emphasis:
                        AddEmphasis(paragraph, emphasis, size, weight, fontFamily);
                        break;
                    case ContainerInline container:
                        AddInlines(paragraph, container, size, weight, fontFamily);
                        break;
                }
            }

            private void AddAutolink(Paragraph paragraph, AutolinkInline auto, double size, FontWeight? weight, FontFamily fontFamily)
            {
                string target = auto.Url ?? string.Empty;
                var hyperlink = new Hyperlink();
                if (Uri.TryCreate(target, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    hyperlink.NavigateUri = uri;
                }

                hyperlink.Inlines.Add(new Run { Text = target, FontSize = size, FontFamily = fontFamily });
                if (weight.HasValue)
                {
                    hyperlink.FontWeight = weight.Value;
                }

                paragraph.Inlines.Add(hyperlink);
            }

            private void AddLink(Paragraph paragraph, LinkInline link, double size, FontWeight? weight, FontFamily fontFamily)
            {
                if (link.IsImage)
                {
                    string alt = LinkLabel(link).Trim();
                    string label = alt.Length > 0
                        ? $"[image: {(alt.Length > 64 ? alt.Substring(0, 64) : alt)}]"
                        : "[image]";
                    // Remote images never auto-load (tracking pixels leak the
                    // reader's IP); the label links out so opening is a choice.
                    if (!string.IsNullOrEmpty(link.Url)
                        && Uri.TryCreate(link.Url, UriKind.Absolute, out var imageUri)
                        && (imageUri.Scheme == Uri.UriSchemeHttp || imageUri.Scheme == Uri.UriSchemeHttps))
                    {
                        var imageLink = new Hyperlink { NavigateUri = imageUri };
                        imageLink.Inlines.Add(new Run { Text = label, FontSize = size, FontFamily = fontFamily });
                        paragraph.Inlines.Add(imageLink);
                    }
                    else
                    {
                        paragraph.Inlines.Add(CreateRun(label, size, weight, fontFamily));
                    }

                    return;
                }

                string labelText = LinkLabel(link);
                if (labelText.Length == 0 && !string.IsNullOrEmpty(link.Url))
                {
                    labelText = link.Url;
                }

                var hyperlink = new Hyperlink();
                string? target = !string.IsNullOrEmpty(link.Url) ? link.Url : labelText;
                if (Uri.TryCreate(target, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    hyperlink.NavigateUri = uri;
                }

                hyperlink.Inlines.Add(new Run
                {
                    Text = labelText.Length > 0 ? labelText : target,
                    FontSize = size,
                    FontFamily = fontFamily,
                });
                if (weight.HasValue)
                {
                    hyperlink.FontWeight = weight.Value;
                }

                paragraph.Inlines.Add(hyperlink);
            }

            private static string LinkLabel(LinkInline link) => link.Label ?? string.Empty;

            private void AddEmphasis(Paragraph paragraph, EmphasisInline emphasis, double size, FontWeight? weight, FontFamily fontFamily)
            {
                if (emphasis.DelimiterChar == '~')
                {
                    var struck = new Span();
                    foreach (var child in emphasis)
                    {
                        if (child is LiteralInline literal)
                        {
                            struck.Inlines.Add(CreateRun(literal.Content.ToString(), size, weight, fontFamily));
                        }
                    }

                    struck.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
                    paragraph.Inlines.Add(struck);
                    return;
                }

                bool bold = emphasis.DelimiterCount >= 2;
                foreach (var child in emphasis)
                {
                    switch (child)
                    {
                        case LiteralInline literal:
                            var run = CreateRun(literal.Content.ToString(), size, weight, fontFamily);
                            if (bold)
                            {
                                run.FontWeight = FontWeights.Bold;
                            }
                            else
                            {
                                run.FontStyle = Windows.UI.Text.FontStyle.Italic;
                            }

                            paragraph.Inlines.Add(run);
                            break;
                        case ContainerInline nested:
                            AddInlines(paragraph, nested, size, bold ? FontWeights.Bold : weight, fontFamily);
                            break;
                        default:
                            var fallback = new Run { Text = child.ToString(), FontSize = size, FontFamily = fontFamily };
                            if (bold)
                            {
                                fallback.FontWeight = FontWeights.Bold;
                            }

                            paragraph.Inlines.Add(fallback);
                            break;
                    }
                }
            }

            private static Run CreateRun(string text, double size, FontWeight? weight, FontFamily fontFamily)
            {
                var run = new Run { Text = text, FontSize = size, FontFamily = fontFamily };
                if (weight.HasValue)
                {
                    run.FontWeight = weight.Value;
                }

                return run;
            }
        }
    }
}
