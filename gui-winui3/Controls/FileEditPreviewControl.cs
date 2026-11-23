using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RuApp.Models;
using RuApp.Services;
using Windows.UI;

namespace RuApp.Controls
{
    /// <summary>
    /// The diff body of one edit: a unified-diff header, then the changed lines with their
    /// numbers, a +/− marker and a syntax-colored body. Every changed line is tinted, a new
    /// file included, so the block reads like the other diff views.
    ///
    /// It names its own range instead of carrying a caption, and it has no header row of
    /// its own: the file name and the totals live in the row that expands it.
    /// </summary>
    public sealed partial class FileEditPreviewControl : UserControl
    {
        private static FontFamily MonoFont =>
            (FontFamily)Application.Current.Resources["RuMonoFontFamily"];

        private static readonly SolidColorBrush FallbackInkBrush = new SolidColorBrush(Color.FromArgb(255, 241, 245, 249));
        private static readonly SolidColorBrush FallbackMutedBrush = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184));
        private static readonly SolidColorBrush FallbackSuccessBrush = new SolidColorBrush(Color.FromArgb(255, 52, 211, 153));
        private static readonly SolidColorBrush FallbackDangerBrush = new SolidColorBrush(Color.FromArgb(255, 248, 113, 113));
        private static readonly SolidColorBrush RemovedBackgroundBrush = new SolidColorBrush(Color.FromArgb(26, 248, 113, 113));
        private static readonly SolidColorBrush AddedBackgroundBrush = new SolidColorBrush(Color.FromArgb(26, 52, 211, 153));
        private static readonly SolidColorBrush TransparentBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        public static readonly DependencyProperty SyntaxFileProperty =
            DependencyProperty.Register(
                nameof(SyntaxFile),
                typeof(string),
                typeof(FileEditPreviewControl),
                new PropertyMetadata(null, OnPreviewPropertiesChanged));

        /// <summary>The edited file's name, used only for its extension: it picks the syntax colors.</summary>
        public string? SyntaxFile
        {
            get => (string?)GetValue(SyntaxFileProperty);
            set => SetValue(SyntaxFileProperty, value);
        }

        public static readonly DependencyProperty BeforeTextProperty =
            DependencyProperty.Register(
                nameof(BeforeText),
                typeof(string),
                typeof(FileEditPreviewControl),
                new PropertyMetadata(string.Empty, OnPreviewPropertiesChanged));

        public static readonly DependencyProperty AfterTextProperty =
            DependencyProperty.Register(
                nameof(AfterText),
                typeof(string),
                typeof(FileEditPreviewControl),
                new PropertyMetadata(string.Empty, OnPreviewPropertiesChanged));

        public static readonly DependencyProperty StartLineProperty =
            DependencyProperty.Register(
                nameof(StartLine),
                typeof(int?),
                typeof(FileEditPreviewControl),
                new PropertyMetadata(null, OnPreviewPropertiesChanged));

        public string BeforeText
        {
            get => (string)GetValue(BeforeTextProperty);
            set => SetValue(BeforeTextProperty, value);
        }

        public string AfterText
        {
            get => (string)GetValue(AfterTextProperty);
            set => SetValue(AfterTextProperty, value);
        }

        public int? StartLine
        {
            get => (int?)GetValue(StartLineProperty);
            set => SetValue(StartLineProperty, value);
        }

        private readonly StackPanel _previewLinesPanel;

        public FileEditPreviewControl()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch;

            _previewLinesPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 0
            };

            // One source line per row, scrolled sideways when it is wider than the panel —
            // the same reading as the Preview tab. The surface is flat, so the horizontal
            // bar sits under the block instead of inside a card.
            var scrollViewer = new ScrollViewer
            {
                MaxHeight = 320,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Enabled,
                VerticalScrollMode = ScrollMode.Enabled,
                Padding = new Thickness(0, 4, 0, 6),
                Content = _previewLinesPanel
            };

            // A flat code pane with no surface of its own, exactly like the Preview tab: the
            // diff sits on the panel, so the numbers start at the panel's left edge instead
            // of after a tinted block. Added/removed rows still carry their own tint.
            Content = scrollViewer;
            Loaded += (_, _) => RefreshAll();
        }

        private static void OnPreviewPropertiesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FileEditPreviewControl control)
            {
                control.RefreshAll();
            }
        }

        private void RefreshAll()
        {
            RebuildPreviewLines();
        }

        private void RebuildPreviewLines()
        {
            _previewLinesPanel.Children.Clear();

            if (string.IsNullOrEmpty(BeforeText) && string.IsNullOrEmpty(AfterText))
            {
                return;
            }

            var lines = DiffEngine.Compute(BeforeText, AfterText, StartLine);
            int baseLine = Math.Max(1, StartLine.GetValueOrDefault(1));
            var language = CodeHighlighter.LanguageForFile(SyntaxFile);
            var oldSpans = CodeHighlighter.HighlightLines(BeforeText, language);
            var newSpans = CodeHighlighter.HighlightLines(AfterText, language);

            var successBrush = GetThemeBrush("RuSuccessBrush", FallbackSuccessBrush);
            var dangerBrush = GetThemeBrush("RuDangerBrush", FallbackDangerBrush);
            var mutedBrush = GetThemeBrush("RuMutedBrush", FallbackMutedBrush);
            var inkBrush = GetThemeBrush("RuInkBrush", FallbackInkBrush);

            // The block names its own range the way every other diff does, so a hunk needs no
            // caption invented beside it.
            _previewLinesPanel.Children.Add(new TextBlock
            {
                Text = DiffEngine.Header(lines, StartLine.GetValueOrDefault(1)),
                FontFamily = MonoFont,
                FontSize = 11,
                Foreground = mutedBrush,
                Opacity = 0.8,
                Margin = new Thickness(0, 2, 0, 4)
            });

            // The gutter hugs the widest number: the fixed 44 px column it used to carry left
            // a dead band on the left of every diff, which read as an indent over the code.
            int digits = 1;
            foreach (var line in lines)
            {
                digits = Math.Max(digits, line.DisplayLineNumber.Length);
            }

            double numberWidth = Math.Max(14, (digits * 7) + 6);

            foreach (var line in lines)
            {
                var row = new Grid
                {
                    MinHeight = 20,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(3, GridUnitType.Pixel) },
                        new ColumnDefinition { Width = new GridLength(numberWidth, GridUnitType.Pixel) },
                        new ColumnDefinition { Width = new GridLength(12, GridUnitType.Pixel) },
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                    }
                };

                var lineBar = new Border { Width = 3, HorizontalAlignment = HorizontalAlignment.Left, Background = TransparentBrush };
                var lineNumBlock = new TextBlock
                {
                    Text = line.DisplayLineNumber,
                    FontFamily = MonoFont,
                    FontSize = 11,
                    Foreground = mutedBrush,
                    Opacity = 0.7,
                    TextAlignment = TextAlignment.Right,
                    Margin = new Thickness(0, 1, 4, 1),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var markerBlock = new TextBlock
                {
                    Text = line.IsAdded ? "+" : line.IsRemoved ? "−" : string.Empty,
                    FontFamily = MonoFont,
                    FontSize = 12,
                    Foreground = line.IsAdded ? successBrush : dangerBrush,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var textBlock = new TextBlock
                {
                    FontFamily = MonoFont,
                    FontSize = 12,
                    Foreground = inkBrush,
                    IsTextSelectionEnabled = true,
                    Margin = new Thickness(0, 1, 12, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.NoWrap
                };

                // Color the line from its own side of the edit, highlighted as a whole
                // so comments and strings spanning lines keep their kind.
                var source = line.IsRemoved ? oldSpans : newSpans;
                int? number = line.IsRemoved ? line.OldLineNumber : line.NewLineNumber;
                int index = (number ?? 0) - baseLine;
                if (number is not null && index >= 0 && index < source.Count)
                {
                    foreach (var span in source[index]) textBlock.Inlines.Add(CodeTheme.Run(span));
                }
                else
                {
                    textBlock.Text = line.Text;
                }

                if (line.IsRemoved)
                {
                    row.Background = RemovedBackgroundBrush;
                    lineBar.Background = dangerBrush;
                }
                else if (line.IsAdded)
                {
                    // Every changed line is tinted, a new file included: a diff nobody can
                    // color-code reads as plain text beside the other agents' output.
                    row.Background = AddedBackgroundBrush;
                    lineBar.Background = successBrush;
                }
                else
                {
                    row.Background = TransparentBrush;
                }

                Grid.SetColumn(lineBar, 0);
                Grid.SetColumn(lineNumBlock, 1);
                Grid.SetColumn(markerBlock, 2);
                Grid.SetColumn(textBlock, 3);

                row.Children.Add(lineBar);
                row.Children.Add(lineNumBlock);
                row.Children.Add(markerBlock);
                row.Children.Add(textBlock);

                _previewLinesPanel.Children.Add(row);
            }
        }

        private static Brush GetThemeBrush(string resourceKey, Brush fallback)
        {
            if (Application.Current?.Resources.TryGetValue(resourceKey, out var res) == true && res is Brush brush)
            {
                return brush;
            }
            return fallback;
        }
    }
}
