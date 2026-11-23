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

        public static readonly DependencyProperty ContextTextProperty =
            DependencyProperty.Register(
                nameof(ContextText),
                typeof(string),
                typeof(FileEditPreviewControl),
                new PropertyMetadata(null, OnPreviewPropertiesChanged));

        /// <summary>
        /// The file as it stands on disk, supplied by the view that can read the workspace.
        /// When it still contains the recorded fragment, the diff draws the whole file with
        /// the edit in place instead of one lonely fragment.
        /// </summary>
        public string? ContextText
        {
            get => (string?)GetValue(ContextTextProperty);
            set => SetValue(ContextTextProperty, value);
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

        /// <summary>Lines of context drawn around an edit when the file is too long to show whole.</summary>
        private const int ContextLines = 40;

        /// <summary>Files up to this many lines are drawn whole; longer ones show a window.</summary>
        private const int WholeFileLines = 400;

        private readonly record struct RenderRow(int Number, DiffKind Kind, string Text, bool IsEllipsis = false);

        private void RebuildPreviewLines()
        {
            _previewLinesPanel.Children.Clear();

            if (string.IsNullOrEmpty(BeforeText) && string.IsNullOrEmpty(AfterText))
            {
                return;
            }

            var lines = DiffEngine.Compute(BeforeText, AfterText, StartLine);
            int baseLine = Math.Max(1, StartLine.GetValueOrDefault(1));
            var whole = PlanWholeFile(lines, baseLine);
            bool context = whole is not null;
            var rows = whole ?? FragmentRows(lines);

            var language = CodeHighlighter.LanguageForFile(SyntaxFile);
            var oldSpans = CodeHighlighter.HighlightLines(BeforeText, language);
            var newSpans = CodeHighlighter.HighlightLines(AfterText, language);
            var fileSpans = context ? CodeHighlighter.HighlightLines(ContextText, language) : [];

            var successBrush = GetThemeBrush("RuSuccessBrush", FallbackSuccessBrush);
            var dangerBrush = GetThemeBrush("RuDangerBrush", FallbackDangerBrush);
            var mutedBrush = GetThemeBrush("RuMutedBrush", FallbackMutedBrush);
            var inkBrush = GetThemeBrush("RuInkBrush", FallbackInkBrush);

            // The block names its own range the way every other diff does, so a hunk needs no
            // caption invented beside it.
            var visible = rows.Where(row => !row.IsEllipsis).ToArray();
            int headerStart = visible.Length > 0 ? visible[0].Number : baseLine;
            _previewLinesPanel.Children.Add(new TextBlock
            {
                Text = DiffEngine.Header(
                    headerStart,
                    visible.Count(row => row.Kind != DiffKind.Added),
                    visible.Count(row => row.Kind != DiffKind.Removed)),
                FontFamily = MonoFont,
                FontSize = 11,
                Foreground = mutedBrush,
                Opacity = 0.8,
                Margin = new Thickness(0, 2, 0, 4)
            });

            // The gutter hugs the widest number: the fixed 44 px column it used to carry left
            // a dead band on the left of every diff, which read as an indent over the code.
            int digits = 1;
            foreach (var row in visible)
            {
                digits = Math.Max(digits, row.Number.ToString().Length);
            }

            double numberWidth = Math.Max(14, (digits * 7) + 6);

            foreach (var row in rows)
            {
                if (row.IsEllipsis)
                {
                    _previewLinesPanel.Children.Add(new TextBlock
                    {
                        Text = "… " + row.Text,
                        FontFamily = MonoFont,
                        FontSize = 11,
                        Foreground = mutedBrush,
                        Opacity = 0.7,
                        Margin = new Thickness(6, 2, 0, 2)
                    });
                    continue;
                }

                var line = new Grid
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
                    Text = row.Number > 0 ? row.Number.ToString() : string.Empty,
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
                    Text = row.Kind == DiffKind.Added ? "+" : row.Kind == DiffKind.Removed ? "−" : string.Empty,
                    FontFamily = MonoFont,
                    FontSize = 12,
                    Foreground = row.Kind == DiffKind.Added ? successBrush : dangerBrush,
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

                // Color each line from its own side: removed lines from the recorded "before",
                // everything else from the file itself when it is drawn, else from "after".
                IReadOnlyList<CodeHighlighter.Span>? spans = null;
                if (row.Kind == DiffKind.Removed)
                {
                    int index = row.Number - baseLine;
                    if (index >= 0 && index < oldSpans.Count) spans = oldSpans[index];
                }
                else if (context)
                {
                    int index = row.Number - 1;
                    if (index >= 0 && index < fileSpans.Count) spans = fileSpans[index];
                }
                else
                {
                    int index = row.Number - baseLine;
                    if (index >= 0 && index < newSpans.Count) spans = newSpans[index];
                }

                if (spans is not null)
                {
                    foreach (var span in spans) textBlock.Inlines.Add(CodeTheme.Run(span));
                }
                else
                {
                    textBlock.Text = row.Text;
                }

                if (row.Kind == DiffKind.Removed)
                {
                    line.Background = RemovedBackgroundBrush;
                    lineBar.Background = dangerBrush;
                }
                else if (row.Kind == DiffKind.Added)
                {
                    // Every changed line is tinted, a new file included: a diff nobody can
                    // color-code reads as plain text beside the other agents' output.
                    line.Background = AddedBackgroundBrush;
                    lineBar.Background = successBrush;
                }
                else
                {
                    line.Background = TransparentBrush;
                }

                Grid.SetColumn(lineBar, 0);
                Grid.SetColumn(lineNumBlock, 1);
                Grid.SetColumn(markerBlock, 2);
                Grid.SetColumn(textBlock, 3);

                line.Children.Add(lineBar);
                line.Children.Add(lineNumBlock);
                line.Children.Add(markerBlock);
                line.Children.Add(textBlock);

                _previewLinesPanel.Children.Add(line);
            }
        }

        private static IReadOnlyList<RenderRow> FragmentRows(IReadOnlyList<DiffLine> lines) =>
            lines.Select(line => new RenderRow(
                int.TryParse(line.DisplayLineNumber, out int number) ? number : 0,
                line.Kind,
                line.Text)).ToArray();

        /// <summary>
        /// The whole file with the edit in place, or null when the row has no file text or the
        /// file no longer contains the recorded fragment. A long file keeps a window around
        /// the edit, so one enormous file cannot grow the block without bound.
        /// </summary>
        private IReadOnlyList<RenderRow>? PlanWholeFile(IReadOnlyList<DiffLine> lines, int baseLine)
        {
            if (string.IsNullOrEmpty(ContextText))
            {
                return null;
            }

            var file = DiffEngine.SplitLines(ContextText);
            // A pure deletion has no "after" lines to look for; its "before" lines stand in.
            string anchorText = AfterText.Length > 0 ? AfterText : BeforeText;
            int anchor = DiffEngine.Locate(file, anchorText, baseLine);
            if (anchor < 0)
            {
                return null;
            }

            string[] removed = lines.Where(line => line.IsRemoved).Select(line => line.Text).ToArray();
            int added = lines.Count(line => line.IsAdded);
            bool whole = file.Count <= WholeFileLines;
            int first = whole ? 0 : Math.Max(0, anchor - ContextLines);
            int last = whole ? file.Count - 1 : Math.Min(file.Count - 1, anchor + added + ContextLines);

            var rows = new List<RenderRow>();
            if (first > 0)
            {
                rows.Add(new RenderRow(0, DiffKind.Unchanged, $"{first} lines above", IsEllipsis: true));
            }

            for (int index = first; index <= last; index++)
            {
                if (index == anchor)
                {
                    for (int offset = 0; offset < removed.Length; offset++)
                    {
                        rows.Add(new RenderRow(baseLine + offset, DiffKind.Removed, removed[offset]));
                    }
                }

                bool isAdded = index >= anchor && index - anchor < added;
                rows.Add(new RenderRow(index + 1, isAdded ? DiffKind.Added : DiffKind.Unchanged, file[index]));
            }

            if (last < file.Count - 1)
            {
                rows.Add(new RenderRow(0, DiffKind.Unchanged, $"{file.Count - 1 - last} lines below", IsEllipsis: true));
            }

            return rows;
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
