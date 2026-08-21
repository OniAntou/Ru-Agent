using System.Text.RegularExpressions;
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
        private static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable");
        private static readonly FontFamily DisplayFont = new FontFamily("Cambria");
        private static readonly FontFamily MonoFont = new FontFamily("Consolas");
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

        private void RenderMarkdown()
        {
            _richTextBlock.Blocks.Clear();
            string source = (MarkdownText ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = source.Split('\n');
            bool inCode = false;
            double bodySize = FontSize > 0 ? FontSize : 14;
            _richTextBlock.FontFamily = UiFont;
            _richTextBlock.FontSize = bodySize;

            foreach (string rawLine in lines)
            {
                string line = rawLine.TrimEnd();
                string trimmed = line.Trim();

                if (trimmed.StartsWith("```"))
                {
                    inCode = !inCode;
                    continue;
                }
                if (inCode)
                {
                    var codePara = new Paragraph
                    {
                        Margin = new Thickness(8, 2, 8, 6)
                    };
                    codePara.Inlines.Add(new Run { Text = line, FontFamily = MonoFont, FontSize = 13 });
                    _richTextBlock.Blocks.Add(codePara);
                    continue;
                }
                if (trimmed == string.Empty)
                {
                    continue;
                }

                Paragraph paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
                RenderLine(trimmed, paragraph, bodySize);
                _richTextBlock.Blocks.Add(paragraph);
            }
        }

        private void RenderLine(string line, Paragraph paragraph, double bodySize)
        {
            if (line.StartsWith("### "))
            {
                AddInlines(paragraph, line.Substring(4).Trim(), bodySize + 2, FontWeights.SemiBold, DisplayFont);
                return;
            }
            if (line.StartsWith("## "))
            {
                AddInlines(paragraph, line.Substring(3).Trim(), bodySize + 4, FontWeights.Bold, DisplayFont);
                return;
            }
            if (line.StartsWith("# "))
            {
                AddInlines(paragraph, line.Substring(2).Trim(), bodySize + 6, FontWeights.Bold, DisplayFont);
                return;
            }

            Match bullet = BulletRegex().Match(line);
            if (bullet.Success)
            {
                paragraph.Inlines.Add(new Run { Text = "\u2022  ", FontFamily = UiFont, FontSize = bodySize });
                AddInlines(paragraph, bullet.Groups[2].Value, bodySize, null, UiFont);
                return;
            }

            Match numbered = NumberedRegex().Match(line);
            if (numbered.Success)
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = numbered.Groups[1].Value + ".  ",
                    FontFamily = UiFont,
                    FontSize = bodySize
                });
                AddInlines(paragraph, numbered.Groups[2].Value, bodySize, null, UiFont);
                return;
            }

            AddInlines(paragraph, line, bodySize, null, UiFont);
        }

        private void AddInlines(Paragraph paragraph, string text, double size, FontWeight? weight, FontFamily fontFamily)
        {
            int pos = 0;
            foreach (Match match in TokenRegex().Matches(text))
            {
                if (match.Index > pos)
                {
                    paragraph.Inlines.Add(CreateRun(text.Substring(pos, match.Index - pos), size, weight, fontFamily));
                }
                string token = match.Value;
                if (token.StartsWith("**") && token.EndsWith("**") && token.Length > 4)
                {
                    paragraph.Inlines.Add(CreateRun(token.Substring(2, token.Length - 4), size, FontWeights.Bold, fontFamily));
                }
                else if (token.Length >= 2 && token.StartsWith("`"))
                {
                    paragraph.Inlines.Add(new Run
                    {
                        Text = token.Substring(1, token.Length - 2),
                        FontSize = 13,
                        FontFamily = MonoFont
                    });
                }
                else if (token.Length > 2 && token.StartsWith("*"))
                {
                    paragraph.Inlines.Add(CreateItalicRun(token.Substring(1, token.Length - 2), size, fontFamily));
                }
                else
                {
                    paragraph.Inlines.Add(CreateRun(token, size, weight, fontFamily));
                }
                pos = match.Index + match.Length;
            }
            if (pos < text.Length)
            {
                paragraph.Inlines.Add(CreateRun(text.Substring(pos), size, weight, fontFamily));
            }
        }

        private static Run CreateRun(string text, double size, FontWeight? weight, FontFamily fontFamily)
        {
            var run = new Run { Text = text, FontSize = size, FontFamily = fontFamily };
            if (weight.HasValue) run.FontWeight = weight.Value;
            return run;
        }

        private static Run CreateItalicRun(string text, double size, FontFamily fontFamily)
        {
            return new Run
            {
                Text = text,
                FontSize = size,
                FontFamily = fontFamily,
                FontStyle = Windows.UI.Text.FontStyle.Italic
            };
        }

        [GeneratedRegex(@"^([-*+])\s+(.+)$", RegexOptions.CultureInvariant)]
        private static partial Regex BulletRegex();

        [GeneratedRegex(@"^(\d+)[.)]\s+(.+)$", RegexOptions.CultureInvariant)]
        private static partial Regex NumberedRegex();

        [GeneratedRegex(@"(\*\*[^*]+\*\*|\*[^*]+\*|`[^`]+`)", RegexOptions.CultureInvariant)]
        private static partial Regex TokenRegex();
    }
}
