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
    public sealed partial class MarkdownBlock
    {
        private sealed partial class BlockRenderer
        {
            private Paragraph Code(CodeBlock code)
            {
                string text = code.Lines.ToString().TrimEnd('\r', '\n');
                string? language = code is FencedCodeBlock fenced ? fenced.Info?.Trim() : null;
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
                    var run = new Run { Text = span.Text };
                    var brush = SpanBrush(span.Kind);
                    if (brush is not null)
                    {
                        run.Foreground = brush;
                    }

                    body.Inlines.Add(run);
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
                fitBlock(outer, 0);
                var paragraph = new Paragraph { Margin = new Thickness(0, 6, 0, 14) };
                paragraph.Inlines.Add(new InlineUIContainer { Child = outer });
                return paragraph;
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

            private Paragraph Table(Markdig.Extensions.Tables.Table table, int indent)
            {
                var presentation = MarkdownTablePresentation.Create(table);
                var rows = presentation.PreviewRows;
                int columns = presentation.PreviewColumns;
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
                            Text = cell,
                            FontFamily = UiFont,
                            FontSize = bodySize,
                            LineHeight = LineHeightFor(bodySize),
                            TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = true,
                        };
                        if (header)
                        {
                            text.FontWeight = FontWeights.SemiBold;
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
                ToolTipService.SetToolTip(copy, presentation.IsTruncated ? "Copy full table" : "Copy table");
                copy.SetValue(AutomationProperties.NameProperty, presentation.IsTruncated ? "Copy full table" : "Copy table");
                copy.Click += OnCopyCodeClick;
                var tableHeader = new Grid { Margin = new Thickness(12, 8, 12, 0) };
                tableHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                tableHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var tableLabel = new TextBlock
                {
                    Text = "Table",
                    FontFamily = MonoFont,
                    FontSize = 11,
                    Foreground = MutedOrDefault(),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(tableLabel, 0);
                Grid.SetColumn(copy, 1);
                tableHeader.Children.Add(tableLabel);
                tableHeader.Children.Add(copy);

                var tableFrame = new StackPanel();
                tableFrame.Children.Add(tableHeader);
                if (presentation.IsTruncated)
                {
                    var notice = new TextBlock
                    {
                        Text = presentation.Notice, FontSize = 12,
                        Foreground = MutedOrDefault(), TextWrapping = TextWrapping.Wrap,
                    };
                    var showAll = new Button
                    {
                        Content = "View full table", Tag = presentation.FullText,
                        Padding = new Thickness(8, 4, 8, 4),
                    };
                    AutomationProperties.SetName(showAll, "View full table");
                    showAll.Click += OnViewFullTableClick;
                    var summary = new StackPanel { Spacing = 6, Margin = new Thickness(12, 8, 12, 8) };
                    summary.Children.Add(notice);
                    summary.Children.Add(showAll);
                    tableFrame.Children.Add(summary);
                }
                tableFrame.Children.Add(new ScrollViewer
                {
                    Content = grid,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollMode = ScrollMode.Enabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollMode = ScrollMode.Disabled,
                });
                var outer = new Border
                {
                    BorderBrush = line,
                    BorderThickness = new Thickness(1),
                    CornerRadius = Services.UiCornerRadius.Control,
                    Child = tableFrame,
                };
                fitBlock(outer, indent * 20);
                var paragraph = new Paragraph { Margin = new Thickness(indent * 20, 6, 0, 14) };
                paragraph.Inlines.Add(new InlineUIContainer { Child = outer });
                return paragraph;
            }

            private static async void OnViewFullTableClick(object sender, RoutedEventArgs e)
            {
                if (sender is not Button { Tag: string text, XamlRoot: { } root }) return;
                var body = new TextBox
                {
                    IsReadOnly = true, AcceptsReturn = true,
                    TextWrapping = TextWrapping.NoWrap, FontFamily = MonoFont,
                    FontSize = MonoFontSize, MinHeight = 180, MaxHeight = 420,
                };
                // WinUI discards subsequent lines if Text is assigned before AcceptsReturn.
                body.Text = text;
                ScrollViewer.SetHorizontalScrollBarVisibility(body, ScrollBarVisibility.Auto);
                ScrollViewer.SetVerticalScrollBarVisibility(body, ScrollBarVisibility.Auto);
                AutomationProperties.SetName(body, "Complete table as tab-separated text");
                var dialog = new ContentDialog
                {
                    Title = "Full table", Content = body, CloseButtonText = "Close",
                    XamlRoot = root, DefaultButton = ContentDialogButton.Close,
                };
                try { await Services.ContentDialogGate.ShowAsync(dialog); }
                catch (Exception error)
                {
                    Services.GuiDiagnosticWriter.AppendTraceLine($"[MarkdownBlock] Full table could not open: {error.GetType().Name}");
                }
            }
        }
    }
}
