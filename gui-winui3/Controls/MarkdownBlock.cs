using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace RuApp.Controls
{
    public sealed partial class MarkdownBlock : UserControl
    {
        private const double MonoFontSize = 13;

        private static readonly FontFamily UiFont = new FontFamily("ms-appx:///Assets/Fonts/Inter-Variable.ttf#Inter, Segoe UI Variable, Segoe UI, Segoe UI Emoji");
        private static readonly FontFamily DisplayFont = new FontFamily("ms-appx:///Assets/Fonts/Inter-Variable.ttf#Inter, Segoe UI Variable, Segoe UI, Segoe UI Emoji");
        private static readonly FontFamily MonoFont = new FontFamily("Cascadia Code, Consolas");
        private readonly RichTextBlock _richTextBlock;
        private readonly List<(FrameworkElement Element, double Inset)> _embeddedBlocks = [];

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

        public static readonly DependencyProperty IsStreamingProperty =
            DependencyProperty.Register(
                nameof(IsStreaming),
                typeof(bool),
                typeof(MarkdownBlock),
                new PropertyMetadata(false));

        /// <summary>
        /// True while this block is the live surface of a streaming reply. The
        /// source always parses as Markdown — the streaming caller throttles how
        /// often it hands over the growing text — and this flag only decides
        /// whether the first text plays the arrival fade, so a transcript reopened
        /// from history never animates.
        /// </summary>
        public bool IsStreaming
        {
            get => (bool)GetValue(IsStreamingProperty);
            set => SetValue(IsStreamingProperty, value);
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
            _richTextBlock.SizeChanged += (_, _) => ResizeEmbeddedBlocks();
        }

        // InlineUIContainer measures its child to content, even in a stretched
        // paragraph. Supply the reading-column width and update it on resize
        // without reparsing Markdown or interrupting text selection.
        private void FitEmbeddedBlock(FrameworkElement element, double inset,
            List<(FrameworkElement Element, double Inset)> embedded)
        {
            embedded.Add((element, inset));
            if (_richTextBlock.ActualWidth > 0)
                element.Width = Math.Max(0, _richTextBlock.ActualWidth - inset);
        }

        private void ResizeEmbeddedBlocks()
        {
            foreach (var (element, inset) in _embeddedBlocks)
                element.Width = Math.Max(0, _richTextBlock.ActualWidth - inset);
        }

        private static void OnMarkdownTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((MarkdownBlock)d).RenderMarkdown();
        }

        private string? _lastRenderedText;
        private double _lastRenderedSize;

        /// <summary>
        /// One line-height rule for parsed and literal paragraphs alike, so the
        /// streaming-to-settled switch does not itself move the text.
        /// </summary>
        private static double LineHeightFor(double bodySize) => Math.Round(bodySize * 1.7);

        private void RenderMarkdown()
        {
            string source = MarkdownRenderPlan.PrepareSource(MarkdownText ?? string.Empty);
            double bodySize = FontSize > 0 ? FontSize : 14;

            if (string.Equals(source, _lastRenderedText, StringComparison.Ordinal)
                && Math.Abs(bodySize - _lastRenderedSize) < 0.01)
            {
                return;
            }

            // The arrival fade belongs to the first text this block ever shows:
            // later streaming renders only grow the block and must stand still.
            bool wasEmpty = string.IsNullOrEmpty(_lastRenderedText);
            _richTextBlock.FontFamily = UiFont;
            _richTextBlock.FontSize = bodySize;
            UpdateRenderedBlocks(source, bodySize);
            _lastRenderedText = source;
            _lastRenderedSize = bodySize;

            if (wasEmpty && source.Length > 0 && IsStreaming)
            {
                PlayArrival();
            }
        }

        /// <summary>
        /// Fades and lifts the block when a streaming reply shows its first text,
        /// so the reply arrives instead of appearing. Skipped when the reader has
        /// animations off, and when a transcript is reopened from history
        /// (<see cref="IsStreaming"/> stays false there), because neither should
        /// animate settled content.
        /// </summary>
        private void PlayArrival()
        {
            if (!Services.UiTiming.AnimationsEnabled)
            {
                return;
            }

            try
            {
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_richTextBlock);
                var compositor = visual.Compositor;
                var duration = TimeSpan.FromMilliseconds(Services.UiTiming.ReplyTextArrivalMilliseconds);

                var fade = compositor.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(0f, 0f);
                fade.InsertKeyFrame(1f, 1f);
                fade.Duration = duration;

                var rise = compositor.CreateVector3KeyFrameAnimation();
                rise.InsertKeyFrame(0f, new System.Numerics.Vector3(0f, 4f, 0f));
                rise.InsertKeyFrame(1f, System.Numerics.Vector3.Zero);
                rise.Duration = duration;

                // The base opacity hides the first frame before the animation
                // starts; the catch below restores it so a failure can never
                // leave the reply invisible.
                visual.Opacity = 0f;
                visual.StartAnimation("Opacity", fade);
                visual.StartAnimation("Offset", rise);
            }
            catch (Exception error)
            {
                Services.GuiDiagnosticWriter.AppendTraceLine($"[MarkdownBlock] Arrival animation failed: {error.Message}");
                try
                {
                    Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_richTextBlock).Opacity = 1f;
                }
                catch (Exception restoreError)
                {
                    Services.GuiDiagnosticWriter.AppendTraceLine($"[MarkdownBlock] Arrival restore failed: {restoreError.Message}");
                }
            }
        }

        /// <summary>
        /// Walks a Markdig document into RichText paragraphs. Unknown leaves fall
        /// back to their literal text so a new extension degrades to visible
        /// prose, never to a swallowed reply.
        /// </summary>
        /// <summary>
        /// One flat run of an emphasis span: plain text plus the styles the
        /// renderer applies. Pure so the emphasis contract is unit-testable
        /// without building XAML.
        /// </summary>
        internal sealed record EmphasisSegment(
            string Text,
            bool Bold,
            bool Italic,
            bool Mono,
            bool Strike,
            int? LinkGroup,
            string? LinkUrl,
            bool IsBreak);

        /// <summary>
        /// Flattens an emphasis span (bold, italic, strikethrough) into runs.
        /// Every Markdig leaf inside emphasis maps to visible text: code keeps
        /// its content in mono, links keep label and target, breaks stay
        /// breaks, entities decode, and nested markers combine. A genuinely
        /// unknown leaf is skipped. Nothing here may stringify a node: Markdig
        /// does not override ToString on inlines, so rendering a child with
        /// ToString leaks a .NET type name such as
        /// Markdig.Syntax.Inlines.CodeInline onto the screen.
        /// </summary>
        internal static IReadOnlyList<EmphasisSegment> FlattenEmphasis(EmphasisInline emphasis)
        {
            ArgumentNullException.ThrowIfNull(emphasis);
            var segments = new List<EmphasisSegment>();
            int linkGroups = 0;

            void AddText(string text, bool bold, bool italic, bool mono, bool strike, int? linkGroup, string? linkUrl)
            {
                if (text.Length == 0)
                {
                    return;
                }

                segments.Add(new EmphasisSegment(text, bold, italic, mono, strike, linkGroup, linkUrl, IsBreak: false));
            }

            void AppendLink(LinkInline link, bool bold, bool italic, bool strike)
            {
                int start = segments.Count;
                foreach (var child in link)
                {
                    Append(child, bold, italic, strike, linkGroup: null, linkUrl: null);
                }

                int group = linkGroups++;
                string? url = link.Url;
                if (segments.Count == start)
                {
                    string label = link.Label ?? string.Empty;
                    string text = label.Length > 0 ? label : url ?? string.Empty;
                    AddText(text, bold, italic, mono: false, strike, group, url);
                    return;
                }

                for (int index = start; index < segments.Count; index++)
                {
                    var stamped = segments[index];
                    segments[index] = stamped with { LinkGroup = group, LinkUrl = url };
                }
            }

            void Append(Markdig.Syntax.Inlines.Inline node, bool bold, bool italic, bool strike, int? linkGroup, string? linkUrl)
            {
                switch (node)
                {
                    case LiteralInline literal:
                        AddText(literal.Content.ToString(), bold, italic, mono: false, strike, linkGroup, linkUrl);
                        break;
                    case CodeInline code:
                        AddText(code.Content, bold, italic, mono: true, strike, linkGroup, linkUrl);
                        break;
                    case LineBreakInline lineBreak:
                        if (lineBreak.IsHard)
                        {
                            segments.Add(new EmphasisSegment(string.Empty, Bold: false, Italic: false, Mono: false, strike, linkGroup, linkUrl, IsBreak: true));
                        }
                        else
                        {
                            AddText(" ", bold, italic, mono: false, strike, linkGroup, linkUrl);
                        }

                        break;
                    case LinkInline link:
                        AppendLink(link, bold, italic, strike);
                        break;
                    case AutolinkInline auto:
                        AddText(auto.Url ?? string.Empty, bold, italic, mono: false, strike, linkGroups++, auto.Url);
                        break;
                    case HtmlEntityInline entity:
                    {
                        string decoded = entity.Transcoded.ToString();
                        AddText(decoded.Length > 0 ? decoded : entity.Original.ToString(), bold, italic, mono: false, strike, linkGroup, linkUrl);
                        break;
                    }
                    case HtmlInline html:
                        AddText(html.Tag ?? string.Empty, bold, italic, mono: false, strike, linkGroup, linkUrl);
                        break;
                    case EmphasisInline nested:
                        foreach (var child in nested)
                        {
                            Append(
                                child,
                                bold || nested.DelimiterCount >= 2,
                                italic || nested.DelimiterCount == 1,
                                strike || nested.DelimiterChar == '~',
                                linkGroup,
                                linkUrl);
                        }

                        break;
                    case ContainerInline container:
                        foreach (var child in container)
                        {
                            Append(child, bold, italic, strike, linkGroup, linkUrl);
                        }

                        break;
                    default:
                        break;
                }
            }

            foreach (var child in emphasis)
            {
                // A strikethrough root carries no bold or italic of its own: the
                // old renderer never styled `~~x~~` beyond the strike, and the
                // span supplies the decoration. Nested markers still combine.
                bool rootStrike = emphasis.DelimiterChar == '~';
                Append(
                    child,
                    bold: !rootStrike && emphasis.DelimiterCount >= 2,
                    italic: !rootStrike && emphasis.DelimiterCount == 1,
                    strike: rootStrike,
                    linkGroup: null,
                    linkUrl: null);
            }

            return segments;
        }

        /// <summary>
        /// Table cells as tab-separated text for the copy button. Pure so the
        /// shape is unit-testable without building XAML.
        /// </summary>
        internal static string TableCellsToTsv(IReadOnlyList<IReadOnlyList<string>> rows, int columns)
        {
            return string.Join("\n", rows.Select(row => string.Join("\t",
                Enumerable.Range(0, columns).Select(index =>
                    index < row.Count ? row[index].Replace('\t', ' ') : string.Empty))));
        }

        private sealed partial class BlockRenderer(double bodySize, Brush? foreground, Action<FrameworkElement, double> fitBlock)
        {
            /// <summary>
            /// DeepSeek-like air: line height tracks the reader's size choice
            /// instead of sitting on a fixed 22px that pinches at 17.
            /// </summary>
            private double TextLineHeight => LineHeightFor(bodySize);

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
                        Flush();
                        AddInlines(Current(), itemPara.Inline, bodySize, null, UiFont);
                    }
                    else if (child is ListBlock nested)
                    {
                        Flush();
                        rows.AddRange(BuildList(nested, indent + 1));
                    }
                    else
                    {
                        Flush();
                        rows.AddRange(BuildBlocks(child, indent + 1));
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
                        // RichText paragraphs carry no border, so the accent bar
                        // is a muted glyph run keeping the quote selectable.
                        row.Inlines.Insert(0, new Run { Text = "▌ ", FontFamily = UiFont, FontSize = bodySize, Foreground = MutedOrDefault() });
                        rows.Add(row);
                    }
                }

                return rows;
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
                    string domain = string.Empty;
                    if (!string.IsNullOrEmpty(link.Url)
                        && Uri.TryCreate(link.Url, UriKind.Absolute, out var imageUri)
                        && (imageUri.Scheme == Uri.UriSchemeHttp || imageUri.Scheme == Uri.UriSchemeHttps))
                    {
                        domain = imageUri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                            ? imageUri.Host.Substring(4)
                            : imageUri.Host;
                    }

                    string label = alt.Length > 0
                        ? $"[image: {(alt.Length > 64 ? alt.Substring(0, 64) : alt)}]"
                        : "[image]";
                    if (domain.Length > 0)
                    {
                        label += $" ({domain})";
                    }
                    // Remote images never auto-load (tracking pixels leak the
                    // reader's IP); the label links out so opening is a choice.
                    if (domain.Length > 0
                        && Uri.TryCreate(link.Url, UriKind.Absolute, out var targetUri)
                        && (targetUri.Scheme == Uri.UriSchemeHttp || targetUri.Scheme == Uri.UriSchemeHttps))
                    {
                        var imageLink = new Hyperlink { NavigateUri = targetUri };
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
                var segments = FlattenEmphasis(emphasis);
                if (segments.Count == 0)
                {
                    return;
                }

                if (emphasis.DelimiterChar == '~')
                {
                    // RichText paragraphs carry no border for quotes and no tag
                    // for strike: one span holds the decoration so every run in
                    // it inherits the strikethrough.
                    var struck = new Span { TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough };
                    AppendSegments(struck.Inlines, segments, size, weight, fontFamily);
                    if (struck.Inlines.Count > 0)
                    {
                        paragraph.Inlines.Add(struck);
                    }

                    return;
                }

                AppendSegments(paragraph.Inlines, segments, size, weight, fontFamily);
            }

            private void AppendSegments(InlineCollection target, IReadOnlyList<EmphasisSegment> segments, double size, FontWeight? weight, FontFamily fontFamily)
            {
                int index = 0;
                while (index < segments.Count)
                {
                    var segment = segments[index];
                    if (segment.IsBreak)
                    {
                        target.Add(new LineBreak());
                        index++;
                        continue;
                    }

                    if (segment.LinkGroup is null)
                    {
                        target.Add(MakeSegmentRun(segment, size, weight, fontFamily));
                        index++;
                        continue;
                    }

                    var link = new Hyperlink();
                    if (Uri.TryCreate(segment.LinkUrl, UriKind.Absolute, out var uri)
                        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    {
                        link.NavigateUri = uri;
                    }

                    int? group = segment.LinkGroup;
                    while (index < segments.Count
                        && !segments[index].IsBreak
                        && segments[index].LinkGroup == group)
                    {
                        link.Inlines.Add(MakeSegmentRun(segments[index], size, weight, fontFamily));
                        index++;
                    }

                    if (link.Inlines.Count > 0)
                    {
                        target.Add(link);
                    }
                }
            }

            private Run MakeSegmentRun(EmphasisSegment segment, double size, FontWeight? weight, FontFamily fontFamily)
            {
                var run = new Run
                {
                    Text = segment.Text,
                    FontSize = segment.Mono ? MonoFontSize : size,
                    FontFamily = segment.Mono ? MonoFont : fontFamily,
                };
                if (segment.Bold)
                {
                    run.FontWeight = FontWeights.Bold;
                }
                else if (weight.HasValue)
                {
                    run.FontWeight = weight.Value;
                }

                if (segment.Italic)
                {
                    run.FontStyle = Windows.UI.Text.FontStyle.Italic;
                }

                return run;
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
