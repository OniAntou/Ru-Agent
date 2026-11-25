using Markdig.Syntax;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using RuApp.Services;

namespace RuApp.Controls
{
    public sealed partial class MarkdownBlock
    {
        private sealed partial class BlockRenderer
        {
            private Paragraph Code(CodeBlock code, int indent)
            {
                string language = code is FencedCodeBlock fenced ? fenced.Info?.Trim() ?? string.Empty : string.Empty;
                // An unclosed fence is still streaming (or was never finished); it never folds.
                bool complete = code is not FencedCodeBlock open || open.ClosingFencedCharCount > 0;
                if (complete && MarkdownPictures.IsMermaid(language) && CanDraw(SandboxRenderKind.Mermaid, code.Lines.ToString()))
                {
                    return PictureCard(code.Lines.ToString(), SandboxRenderKind.Mermaid, "mermaid", indent);
                }

                return CodeCard(code.Lines.ToString(), language, indent, complete);
            }

            /// <summary>
            /// Renders raw source in the code card. Math blocks use this too:
            /// RichTextBlock has no math layout, and a labeled source card is
            /// clearer than dropping the block into ordinary prose. A long finished
            /// block folds to its first lines; wrap and line numbers are per-card
            /// toggles in the header. Copy always takes the whole source.
            /// </summary>
            private Paragraph CodeCard(string source, string? language, int indent, bool complete)
            {
                var card = CodeCardElement(source, language, complete);
                fitBlock(card, indent * 20);
                var paragraph = new Paragraph { Margin = new Thickness(indent * 20, 6, 0, 14) };
                paragraph.Inlines.Add(new InlineUIContainer { Child = card });
                return paragraph;
            }

            private Border CodeCardElement(string source, string? language, bool complete)
            {
                string text = source.TrimEnd('\r', '\n');
                if (string.IsNullOrWhiteSpace(language))
                {
                    language = "code";
                }

                var lines = CodeHighlighter.HighlightLines(text, language);
                bool folds = CodeCardLayout.Folds(lines.Count, complete);
                bool expanded = false, wrap = false, numbers = false;

                var label = new TextBlock
                {
                    Text = language,
                    FontFamily = MonoFont,
                    FontSize = 11,
                    Foreground = MutedOrDefault(),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var copy = HeaderButton(new FontIcon { Glyph = "", FontSize = 12 }, "Copy code");
                copy.Tag = text;
                copy.Click += OnCopyCodeClick;
                var wrapToggle = HeaderButton(MonoGlyph("↵"), "Wrap lines");
                var numberToggle = HeaderButton(MonoGlyph("#"), "Show line numbers");

                var header = new Grid { Margin = new Thickness(12, 8, 12, 0) };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                tools.Children.Add(numberToggle);
                tools.Children.Add(wrapToggle);
                tools.Children.Add(copy);
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(label, 0);
                Grid.SetColumn(tools, 1);
                header.Children.Add(label);
                header.Children.Add(tools);

                double lineHeight = Math.Round(MonoFontSize * 1.6);
                var body = new TextBlock
                {
                    FontFamily = MonoFont,
                    FontSize = MonoFontSize,
                    TextWrapping = TextWrapping.NoWrap,
                    IsTextSelectionEnabled = true,
                    LineHeight = lineHeight,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Padding = new Thickness(16, 10, 16, 16),
                };
                // The gutter sits outside the horizontal scroller and outside the
                // selectable text, so numbers never scroll away or enter a copy.
                var gutter = new TextBlock
                {
                    FontFamily = MonoFont,
                    FontSize = MonoFontSize,
                    LineHeight = lineHeight,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    TextAlignment = TextAlignment.Right,
                    Foreground = MutedOrDefault(),
                    Opacity = 0.7,
                    Padding = new Thickness(16, 10, 0, 16),
                    Visibility = Visibility.Collapsed,
                };
                gutter.SetValue(AutomationProperties.AccessibilityViewProperty, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                var scroller = new ScrollViewer
                {
                    Content = body,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollMode = ScrollMode.Enabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollMode = ScrollMode.Disabled,
                };
                var code = new Grid();
                code.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                code.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(scroller, 1);
                code.Children.Add(gutter);
                code.Children.Add(scroller);

                var fold = new HyperlinkButton
                {
                    FontSize = 12,
                    Padding = new Thickness(16, 2, 16, 10),
                    Visibility = folds ? Visibility.Visible : Visibility.Collapsed,
                };

                void Fill()
                {
                    int shown = folds && !expanded ? CodeCardLayout.FoldedLines : lines.Count;
                    body.Inlines.Clear();
                    for (int index = 0; index < shown; index++)
                    {
                        if (index > 0) body.Inlines.Add(new LineBreak());
                        foreach (var span in lines[index]) body.Inlines.Add(CodeTheme.Run(span));
                    }

                    bool showNumbers = CodeCardLayout.ShowsLineNumbers(numbers, wrap);
                    gutter.Text = showNumbers ? CodeCardLayout.Gutter(shown) : string.Empty;
                    gutter.Visibility = showNumbers ? Visibility.Visible : Visibility.Collapsed;
                    body.Padding = new Thickness(showNumbers ? 12 : 16, 10, 16, folds && !expanded ? 4 : 16);
                    body.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
                    scroller.HorizontalScrollMode = wrap ? ScrollMode.Disabled : ScrollMode.Enabled;
                    scroller.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
                    fold.Content = CodeCardLayout.FoldLabel(lines.Count, expanded);
                    SetToggle(wrapToggle, wrap, wrap ? "Don't wrap lines" : "Wrap lines");
                    SetToggle(numberToggle, showNumbers, wrap
                        ? "Line numbers show while lines don't wrap"
                        : numbers ? "Hide line numbers" : "Show line numbers");
                    numberToggle.IsEnabled = !wrap;
                }

                fold.Click += (_, _) => { expanded = !expanded; Fill(); };
                wrapToggle.Click += (_, _) => { wrap = !wrap; Fill(); };
                numberToggle.Click += (_, _) => { numbers = !numbers; Fill(); };
                Fill();

                var frame = new StackPanel();
                frame.Children.Add(header);
                frame.Children.Add(code);
                frame.Children.Add(fold);
                var outer = new Border
                {
                    Background = (Brush?)Application.Current?.Resources["RuBackdropSurfaceBrush"],
                    BorderBrush = ThemeBrush("RuLineBrush", 203, 213, 225),
                    BorderThickness = new Thickness(1),
                    CornerRadius = Services.UiCornerRadius.Inset,
                    Child = frame,
                };
                RevealOnHover(outer, copy);
                RevealOnHover(outer, wrapToggle, keepWhen: () => wrap);
                RevealOnHover(outer, numberToggle, keepWhen: () => numbers && !wrap);
                return outer;
            }

            private static Button HeaderButton(object content, string name)
            {
                var button = new Button
                {
                    Content = content,
                    Style = (Style?)Application.Current?.Resources["RuToolbarButtonStyle"],
                    Padding = new Thickness(6, 4, 6, 4),
                };
                ToolTipService.SetToolTip(button, name);
                button.SetValue(AutomationProperties.NameProperty, name);
                return button;
            }

            private static TextBlock MonoGlyph(string glyph) => new()
            {
                Text = glyph,
                FontFamily = MonoFont,
                FontSize = 12,
                LineHeight = 14,
                VerticalAlignment = VerticalAlignment.Center,
            };

            /// <summary>An on toggle reads in the accent color; its name says what a click does.</summary>
            private static void SetToggle(Button button, bool on, string name)
            {
                ToolTipService.SetToolTip(button, name);
                button.SetValue(AutomationProperties.NameProperty, name);
                button.SetValue(AutomationProperties.ItemStatusProperty, on ? "On" : "Off");
                if (button.Content is TextBlock glyph)
                {
                    if (on) glyph.Foreground = ThemeBrush("RuAccentBrush", 59, 130, 246);
                    else glyph.ClearValue(TextBlock.ForegroundProperty);
                }
            }
        }
    }
}
