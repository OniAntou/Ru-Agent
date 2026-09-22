using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RuApp.Models;
using Windows.UI;

namespace RuApp.Controls
{
    /// <summary>
    /// The diff body of an edit row: the changed lines with their numbers, drawn as
    /// two tints against the row's own surface.
    ///
    /// It carries no header of its own. The row above it already names the file and
    /// counts the changes, and a second header repeating them inside the panel pushed
    /// the diff down for nothing. The row header is now the toggle, which also gives
    /// the reader a way back to a folded diff.
    /// </summary>
    public sealed partial class FileEditPreviewControl : UserControl
    {
        private static FontFamily MonoFont =>
            (FontFamily)Application.Current.Resources["RuMonoFontFamily"];

        private static readonly SolidColorBrush FallbackSurfaceBrush = new SolidColorBrush(Color.FromArgb(255, 32, 35, 42));
        private static readonly SolidColorBrush FallbackLineBrush = new SolidColorBrush(Color.FromArgb(255, 51, 65, 85));
        private static readonly SolidColorBrush FallbackInkBrush = new SolidColorBrush(Color.FromArgb(255, 241, 245, 249));
        private static readonly SolidColorBrush FallbackMutedBrush = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184));
        private static readonly SolidColorBrush FallbackSuccessBrush = new SolidColorBrush(Color.FromArgb(255, 52, 211, 153));
        private static readonly SolidColorBrush FallbackDangerBrush = new SolidColorBrush(Color.FromArgb(255, 248, 113, 113));
        private static readonly SolidColorBrush RemovedBackgroundBrush = new SolidColorBrush(Color.FromArgb(32, 248, 113, 113));
        private static readonly SolidColorBrush AddedBackgroundBrush = new SolidColorBrush(Color.FromArgb(32, 52, 211, 153));

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

            var rootBorder = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = GetThemeBrush("RuSurfaceBrush", FallbackSurfaceBrush),
                BorderBrush = GetThemeBrush("RuLineBrush", FallbackLineBrush),
                Child = scrollViewer
            };

            Content = rootBorder;
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

            var successBrush = GetThemeBrush("RuSuccessBrush", FallbackSuccessBrush);
            var dangerBrush = GetThemeBrush("RuDangerBrush", FallbackDangerBrush);
            var mutedBrush = GetThemeBrush("RuMutedBrush", FallbackMutedBrush);
            var inkBrush = GetThemeBrush("RuInkBrush", FallbackInkBrush);

            foreach (var line in lines)
            {
                var row = new Grid
                {
                    MinHeight = 21,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(3, GridUnitType.Pixel) },
                        new ColumnDefinition { Width = new GridLength(40, GridUnitType.Pixel) },
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                    }
                };

                var lineBar = new Border
                {
                    Width = 3,
                    HorizontalAlignment = HorizontalAlignment.Left
                };

                var lineNumBlock = new TextBlock
                {
                    Text = line.DisplayLineNumber,
                    FontFamily = MonoFont,
                    FontSize = 11,
                    TextAlignment = TextAlignment.Right,
                    Margin = new Thickness(0, 2, 8, 2),
                    VerticalAlignment = VerticalAlignment.Center
                };

                var textBlock = new TextBlock
                {
                    Text = line.Text,
                    FontFamily = MonoFont,
                    FontSize = 12,
                    Foreground = inkBrush,
                    IsTextSelectionEnabled = true,
                    Margin = new Thickness(4, 2, 8, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.NoWrap
                };

                if (line.IsRemoved)
                {
                    row.Background = RemovedBackgroundBrush;
                    lineBar.Background = dangerBrush;
                    lineNumBlock.Foreground = dangerBrush;
                }
                else if (line.IsAdded)
                {
                    row.Background = AddedBackgroundBrush;
                    lineBar.Background = successBrush;
                    lineNumBlock.Foreground = successBrush;
                }
                else
                {
                    row.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    lineBar.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    lineNumBlock.Foreground = mutedBrush;
                    lineNumBlock.Opacity = 0.6;
                }

                Grid.SetColumn(lineBar, 0);
                Grid.SetColumn(lineNumBlock, 1);
                Grid.SetColumn(textBlock, 2);

                row.Children.Add(lineBar);
                row.Children.Add(lineNumBlock);
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
