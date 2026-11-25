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
                        ConfirmCopy((Button)sender);
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
            /// A toggle that is on stays drawn (<paramref name="keepWhen"/>), so the card
            /// still shows which view it is in.
            /// </summary>
            private static void RevealOnHover(FrameworkElement block, Button button, Func<bool>? keepWhen = null)
            {
                button.Opacity = 0;
                bool pointerOver = false;
                void Show(bool visible) => Services.UiMotion.FadeOpacity(button, visible || keepWhen?.Invoke() == true ? 1 : 0);
                button.Click += (_, _) => Show(pointerOver || button.FocusState == FocusState.Keyboard);
                block.PointerEntered += (_, _) => { pointerOver = true; Show(true); };
                block.PointerExited += (_, _) => { pointerOver = false; Show(button.FocusState == FocusState.Keyboard); };
                block.PointerCanceled += (_, _) => { pointerOver = false; Show(false); };
                button.GotFocus += (_, _) => Show(pointerOver || button.FocusState == FocusState.Keyboard);
                button.LostFocus += (_, _) => Show(pointerOver);
            }

            /// <summary>
            /// The copy glyph turns into a check for a moment, so a copy that worked says so
            /// where the pointer already is. A second click during that moment still copies.
            /// </summary>
            private static async void ConfirmCopy(Button button)
            {
                if (button.Content is not FontIcon icon || icon.Glyph == CopiedGlyph) return;
                string original = icon.Glyph;
                var originalBrush = icon.Foreground;
                icon.Glyph = CopiedGlyph;
                icon.Foreground = ThemeBrush("RuSuccessBrush", 52, 211, 153);
                Services.UiMotion.Enter(icon);
                try
                {
                    await System.Threading.Tasks.Task.Delay(CopiedHoldMilliseconds);
                }
                finally
                {
                    icon.Glyph = original;
                    icon.Foreground = originalBrush;
                }
            }

            private const string CopiedGlyph = "\uE73E";
            private const int CopiedHoldMilliseconds = 1200;

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
                            Background = header ? (Brush?)Application.Current?.Resources["RuBackdropSurfaceBrush"] : null,
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
                copy.Background = (Brush?)Application.Current?.Resources["RuBackdropSurfaceBrush"];

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
