using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Tables;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Footnotes;
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
    public sealed partial class MarkdownBlock
    {
        private sealed partial class BlockRenderer
        {
            private Paragraph Code(CodeBlock code, int indent)
            {
                string language = code is FencedCodeBlock fenced ? fenced.Info?.Trim() ?? string.Empty : string.Empty;
                return CodeCard(code.Lines.ToString(), language, indent);
            }

            /// <summary>
            /// Renders raw source in the code card. Math blocks use this too:
            /// RichTextBlock has no math layout, and a labeled source card is
            /// clearer than dropping the block into ordinary prose.
            /// </summary>
            private Paragraph CodeCard(string source, string? language, int indent)
            {
                string text = source.TrimEnd('\r', '\n');
                if (string.IsNullOrWhiteSpace(language))
                {
                    language = "code";
                }

                var label = new TextBlock
                {
                    Text = language,
                    FontFamily = MonoFont,
                    FontSize = 11,
                    Foreground = MutedOrDefault(),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var copy = new Button
                {
                    Content = new FontIcon { Glyph = "\uE8C8", FontSize = 12 },
                    Style = (Style?)Application.Current?.Resources["RuToolbarButtonStyle"],
                    Padding = new Thickness(6, 4, 6, 4),
                    Tag = text,
                };
                ToolTipService.SetToolTip(copy, "Copy code");
                copy.SetValue(AutomationProperties.NameProperty, "Copy code");
                copy.Click += OnCopyCodeClick;
                var header = new Grid { Margin = new Thickness(12, 8, 12, 0) };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(label, 0);
                Grid.SetColumn(copy, 1);
                header.Children.Add(label);
                header.Children.Add(copy);

                var body = new TextBlock
                {
                    FontFamily = MonoFont,
                    FontSize = MonoFontSize,
                    TextWrapping = TextWrapping.NoWrap,
                    IsTextSelectionEnabled = true,
                    LineHeight = Math.Round(MonoFontSize * 1.6),
                    Padding = new Thickness(16, 10, 16, 16),
                };
                foreach (var span in CodeHighlighter.Highlight(text, language))
                {
                    body.Inlines.Add(CodeTheme.Run(span));
                }

                var frame = new StackPanel();
                frame.Children.Add(header);
                frame.Children.Add(new ScrollViewer
                {
                    Content = body,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollMode = ScrollMode.Enabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollMode = ScrollMode.Disabled,
                });
                var outer = new Border
                {
                    Background = (Brush?)Application.Current?.Resources["RuSurfaceBrush"],
                    BorderBrush = ThemeBrush("RuLineBrush", 203, 213, 225),
                    BorderThickness = new Thickness(1),
                    CornerRadius = Services.UiCornerRadius.Inset,
                    Child = frame,
                };
                RevealOnHover(outer, copy);
                fitBlock(outer, indent * 20);
                var paragraph = new Paragraph { Margin = new Thickness(indent * 20, 6, 0, 14) };
                paragraph.Inlines.Add(new InlineUIContainer { Child = outer });
                return paragraph;
            }

            /// <summary>
            /// Footnote definitions as a separated section. The prose reference
            /// renders in place; the body keeps its label and drops the structural
            /// backlink, which AddInline already refuses to print.
            /// </summary>
            private List<Paragraph> FootnoteSection(FootnoteGroup group, int indent)
            {
                var rows = new List<Paragraph>();
                var heading = new Paragraph
                {
                    Margin = new Thickness(indent * 20, 12, 0, 6),
                    LineHeight = TextLineHeight,
                };
                heading.Inlines.Add(new Run
                {
                    Text = "Footnotes",
                    FontFamily = DisplayFont,
                    FontSize = bodySize,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = MutedOrDefault(),
                });
                rows.Add(heading);

                foreach (var footnote in group.OfType<Footnote>())
                {
                    var blocks = BuildBlocks(footnote, indent);
                    if (blocks.Count == 0)
                    {
                        continue;
                    }

                    string label = string.IsNullOrWhiteSpace(footnote.Label) ? "note" : footnote.Label;
                    blocks[0].Inlines.Insert(0, new Run
                    {
                        Text = "[" + label + "] ",
                        FontFamily = UiFont,
                        FontSize = bodySize,
                        Foreground = MutedOrDefault(),
                    });
                    rows.AddRange(blocks);
                }

                return rows.Count > 1 ? rows : new List<Paragraph>();
            }

            private void OnCopyCodeClick(object sender, RoutedEventArgs e)
            {
                if (sender is Button { Tag: string text } && text.Length > 0)
                {
                    try
                    {
                        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                        package.SetText(text);
                        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                    }
                    catch (Exception error)
                    {
                        Services.GuiDiagnosticWriter.AppendTraceLine($"[MarkdownBlock] Copy failed: {error.Message}");
                    }
                }
            }

            /// <summary>
            /// Draws a block's copy button only while the pointer is over the block or the
            /// keyboard is on the button. It stays in the tab order and the automation tree.
            /// </summary>
            private static void RevealOnHover(FrameworkElement block, Button button)
            {
                button.Opacity = 0;
                bool pointerOver = false;
                void Show(bool visible) => Services.UiMotion.FadeOpacity(button, visible ? 1 : 0);
                block.PointerEntered += (_, _) => { pointerOver = true; Show(true); };
                block.PointerExited += (_, _) => { pointerOver = false; Show(button.FocusState == FocusState.Keyboard); };
                block.PointerCanceled += (_, _) => { pointerOver = false; Show(false); };
                button.GotFocus += (_, _) => Show(pointerOver || button.FocusState == FocusState.Keyboard);
                button.LostFocus += (_, _) => Show(pointerOver);
            }

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

            private void AddCellInlines(InlineCollection target, TableCell cell, bool header)
            {
                var paragraphs = cell.Descendants().OfType<ParagraphBlock>().ToArray();
                if (paragraphs.Length == 0)
                {
                    target.Add(CreateRun(MarkdownEngine.TableCellText(cell), bodySize,
                        header ? FontWeights.SemiBold : null, UiFont));
                    return;
                }

                for (int index = 0; index < paragraphs.Length; index++)
                {
                    if (index > 0)
                    {
                        target.Add(new LineBreak());
                    }

                    AddInlines(target, paragraphs[index].Inline, bodySize,
                        header ? FontWeights.SemiBold : null, UiFont);
                }
            }

            private Paragraph Table(Markdig.Extensions.Tables.Table table, int indent)
            {
                var presentation = MarkdownTablePresentation.Create(table);
                var rows = presentation.Rows;
                var cellRows = table.OfType<TableRow>()
                    .Select(row => row.OfType<TableCell>().ToArray())
                    .ToArray();
                int columns = presentation.Columns;
                var line = ThemeBrush("RuLineBrush", 203, 213, 225);
                var grid = new Grid { MinWidth = columns * 120 };
                for (int index = 0; index < columns; index++)
                {
                    // Keep short identifier columns compact, but cap prose's
                    // weight so one long cell cannot consume the entire table.
                    int longest = rows.Max(row => index < row.Count ? row[index].Length : 0);
                    grid.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = new GridLength(Math.Clamp(longest, 12, 48), GridUnitType.Star),
                    });
                }
                // A horizontal ScrollViewer measures with infinite width. Give
                // the grid a finite width so star cells wrap instead of clipping.
                // MinWidth still permits scrolling genuinely wide multi-column tables.
                fitBlock(grid, indent * 20 + 2);

                for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    bool header = rowIndex == 0;
                    for (int column = 0; column < columns; column++)
                    {
                        string cell = column < rows[rowIndex].Count ? rows[rowIndex][column] : string.Empty;
                        var text = new TextBlock
                        {
                            FontFamily = UiFont,
                            FontSize = bodySize,
                            LineHeight = LineHeightFor(bodySize),
                            TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = true,
                            // GFM's :---:/:---:/---: alignment is table metadata,
                            // not cell content; plain left for an undeclared column.
                            TextAlignment = column < table.ColumnDefinitions.Count
                                ? MarkdownBlock.TableColumnAlignment(table.ColumnDefinitions[column])
                                : Microsoft.UI.Xaml.TextAlignment.Left,
                        };
                        if (header)
                        {
                            text.FontWeight = FontWeights.SemiBold;
                        }

                        var cellNode = rowIndex < cellRows.Length && column < cellRows[rowIndex].Length
                            ? cellRows[rowIndex][column]
                            : null;
                        if (cellNode is not null)
                        {
                            // Rebuild the cell's inline runs so emphasis, links and
                            // code spans survive instead of flattening to plain text.
                            AddCellInlines(text.Inlines, cellNode, header);
                        }
                        else
                        {
                            text.Text = cell;
                        }

                        var frame = new Border
                        {
                            BorderBrush = line,
                            BorderThickness = new Thickness(0, 0, 0, rowIndex < rows.Count - 1 ? 1 : 0),
                            Background = header ? (Brush?)Application.Current?.Resources["RuSurfaceBrush"] : null,
                            Padding = new Thickness(12, 9, 12, 9),
                            Child = text,
                        };
                        Grid.SetRow(frame, rowIndex);
                        Grid.SetColumn(frame, column);
                        grid.Children.Add(frame);
                    }
                }

                var copy = new Button
                {
                    Content = new FontIcon { Glyph = "\uE8C8", FontSize = 12 },
                    Style = (Style?)Application.Current?.Resources["RuToolbarButtonStyle"],
                    Padding = new Thickness(6, 4, 6, 4),
                    Tag = presentation.FullText,
                };
                ToolTipService.SetToolTip(copy, "Copy table");
                copy.SetValue(AutomationProperties.NameProperty, "Copy table");
                copy.Click += OnCopyCodeClick;
                // A table needs no caption saying it is a table. The copy button sits over
                // the header row's far corner on the row's own surface, so it never adds
                // a strip above the data.
                copy.HorizontalAlignment = HorizontalAlignment.Right;
                copy.VerticalAlignment = VerticalAlignment.Top;
                copy.Margin = new Thickness(0, 5, 6, 0);
                copy.Background = (Brush?)Application.Current?.Resources["RuSurfaceBrush"];

                var tableFrame = new Grid();
                tableFrame.Children.Add(new ScrollViewer
                {
                    Content = grid,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollMode = ScrollMode.Enabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollMode = ScrollMode.Enabled,
                    MaxHeight = 640,
                });
                var outer = new Border
                {
                    BorderBrush = line,
                    BorderThickness = new Thickness(1),
                    // Same frame as a code card: the two block kinds read as one family.
                    CornerRadius = Services.UiCornerRadius.Inset,
                    Child = tableFrame,
                };
                tableFrame.Children.Add(copy);
                RevealOnHover(outer, copy);
                fitBlock(outer, indent * 20);
                var paragraph = new Paragraph { Margin = new Thickness(indent * 20, 6, 0, 14) };
                paragraph.Inlines.Add(new InlineUIContainer { Child = outer });
                return paragraph;
            }
        }
    }
}
