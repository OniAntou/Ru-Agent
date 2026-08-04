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
                VerticalAlignment = VerticalAlignment.Top
            };
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
                    codePara.Inlines.Add(new Run { Text = line, FontFamily = new FontFamily("Consolas"), FontSize = 13 });
                    _richTextBlock.Blocks.Add(codePara);
                    continue;
                }
                if (trimmed == string.Empty)
                {
                    continue;
                }

                Paragraph paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
                RenderLine(trimmed, paragraph);
                _richTextBlock.Blocks.Add(paragraph);
            }
        }

        private void RenderLine(string line, Paragraph paragraph)
        {
            if (line.StartsWith("### "))
            {
                AddInlines(paragraph, line.Substring(4).Trim(), 16, FontWeights.SemiBold);
                return;
            }
            if (line.StartsWith("## "))
            {
                AddInlines(paragraph, line.Substring(3).Trim(), 18, FontWeights.Bold);
                return;
            }
            if (line.StartsWith("# "))
            {
                AddInlines(paragraph, line.Substring(2).Trim(), 20, FontWeights.Bold);
                return;
            }

            Match bullet = Regex.Match(line, @"^([-*+])\s+(.+)$");
            if (bullet.Success)
            {
                paragraph.Inlines.Add(new Run { Text = "\u2022  " });
                AddInlines(paragraph, bullet.Groups[2].Value, 14, null);
                return;
            }

            Match numbered = Regex.Match(line, @"^(\d+)[.)]\s+(.+)$");
            if (numbered.Success)
            {
                paragraph.Inlines.Add(new Run { Text = numbered.Groups[1].Value + ".  " });
                AddInlines(paragraph, numbered.Groups[2].Value, 14, null);
                return;
            }

            AddInlines(paragraph, line, 14, null);
        }

        private void AddInlines(Paragraph paragraph, string text, double size, FontWeight? weight)
        {
            var tokenRegex = new Regex(@"(\*\*[^*]+\*\*|\*[^*]+\*|`[^`]+`)");
            int pos = 0;
            foreach (Match match in tokenRegex.Matches(text))
            {
                if (match.Index > pos)
                {
                    paragraph.Inlines.Add(CreateRun(text.Substring(pos, match.Index - pos), size, weight));
                }
                string token = match.Value;
                if (token.StartsWith("**") && token.EndsWith("**") && token.Length > 4)
                {
                    paragraph.Inlines.Add(CreateRun(token.Substring(2, token.Length - 4), size, FontWeights.Bold));
                }
                else if (token.Length >= 2 && token.StartsWith("`"))
                {
                    paragraph.Inlines.Add(new Run
                    {
                        Text = token.Substring(1, token.Length - 2),
                        FontSize = 13,
                        FontFamily = new FontFamily("Consolas")
                    });
                }
                else if (token.Length > 2 && token.StartsWith("*"))
                {
                    paragraph.Inlines.Add(CreateItalicRun(token.Substring(1, token.Length - 2), size));
                }
                else
                {
                    paragraph.Inlines.Add(CreateRun(token, size, weight));
                }
                pos = match.Index + match.Length;
            }
            if (pos < text.Length)
            {
                paragraph.Inlines.Add(CreateRun(text.Substring(pos), size, weight));
            }
        }

        private static Run CreateRun(string text, double size, FontWeight? weight)
        {
            var run = new Run { Text = text, FontSize = size };
            if (weight.HasValue) run.FontWeight = weight.Value;
            return run;
        }

        private static Run CreateItalicRun(string text, double size)
        {
            return new Run { Text = text, FontSize = size, FontStyle = Windows.UI.Text.FontStyle.Italic };
        }
    }
}