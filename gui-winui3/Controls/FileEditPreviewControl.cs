using System;
using System.Collections.Generic;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RuApp.Models;
using Windows.UI;

namespace RuApp.Controls
{
    public sealed partial class FileEditPreviewControl : UserControl
    {
        private static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable");
        private static readonly FontFamily MonoFont = new FontFamily("Consolas");

        private static readonly SolidColorBrush FallbackSurfaceBrush = new SolidColorBrush(Color.FromArgb(255, 32, 35, 42));
        private static readonly SolidColorBrush FallbackLineBrush = new SolidColorBrush(Color.FromArgb(255, 51, 65, 85));
        private static readonly SolidColorBrush FallbackInkBrush = new SolidColorBrush(Color.FromArgb(255, 241, 245, 249));
        private static readonly SolidColorBrush FallbackMutedBrush = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184));
        private static readonly SolidColorBrush FallbackSuccessBrush = new SolidColorBrush(Color.FromArgb(255, 52, 211, 153));
        private static readonly SolidColorBrush FallbackDangerBrush = new SolidColorBrush(Color.FromArgb(255, 248, 113, 113));
        private static readonly SolidColorBrush RemovedBackgroundBrush = new SolidColorBrush(Color.FromArgb(32, 248, 113, 113));
        private static readonly SolidColorBrush AddedBackgroundBrush = new SolidColorBrush(Color.FromArgb(32, 52, 211, 153));

        public static readonly DependencyProperty FileNameProperty =
            DependencyProperty.Register(
                nameof(FileName),
                typeof(string),
                typeof(FileEditPreviewControl),
                new PropertyMetadata(string.Empty, OnPreviewPropertiesChanged));

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

        public static readonly DependencyProperty AddedCountProperty =
            DependencyProperty.Register(
                nameof(AddedCount),
                typeof(int?),
                typeof(FileEditPreviewControl),
                new PropertyMetadata(null, OnHeaderPropertiesChanged));

        public static readonly DependencyProperty RemovedCountProperty =
            DependencyProperty.Register(
                nameof(RemovedCount),
                typeof(int?),
                typeof(FileEditPreviewControl),
                new PropertyMetadata(null, OnHeaderPropertiesChanged));

        public static readonly DependencyProperty IsExpandedProperty =
            DependencyProperty.Register(
                nameof(IsExpanded),
                typeof(bool),
                typeof(FileEditPreviewControl),
                new PropertyMetadata(true, OnIsExpandedChanged));

        public string FileName
        {
            get => (string)GetValue(FileNameProperty);
            set => SetValue(FileNameProperty, value);
        }

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

        public int? AddedCount
        {
            get => (int?)GetValue(AddedCountProperty);
            set => SetValue(AddedCountProperty, value);
        }

        public int? RemovedCount
        {
            get => (int?)GetValue(RemovedCountProperty);
            set => SetValue(RemovedCountProperty, value);
        }

        public bool IsExpanded
        {
            get => (bool)GetValue(IsExpandedProperty);
            set => SetValue(IsExpandedProperty, value);
        }

        private readonly Border _rootBorder;
        private readonly TextBlock _fileNameBlock;
        private readonly TextBlock _addedCountBlock;
        private readonly TextBlock _removedCountBlock;
        private readonly FontIcon _chevronIcon;
        private readonly Grid _bodyContainer;
        private readonly StackPanel _previewLinesPanel;

        public FileEditPreviewControl()
        {
            _fileNameBlock = new TextBlock
            {
                FontFamily = UiFont,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = GetThemeBrush("RuInkBrush", FallbackInkBrush),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6, 0, 0, 0)
            };

            _addedCountBlock = new TextBlock
            {
                FontFamily = MonoFont,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = GetThemeBrush("RuSuccessBrush", FallbackSuccessBrush),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                Visibility = Visibility.Collapsed
            };

            _removedCountBlock = new TextBlock
            {
                FontFamily = MonoFont,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = GetThemeBrush("RuDangerBrush", FallbackDangerBrush),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Visibility = Visibility.Collapsed
            };

            _chevronIcon = new FontIcon
            {
                Glyph = "\uE70E", // ChevronUp
                FontSize = 10,
                Foreground = GetThemeBrush("RuMutedBrush", FallbackMutedBrush),
                VerticalAlignment = VerticalAlignment.Center
            };

            var fileIcon = new FontIcon
            {
                Glyph = "\uE8A5", // Document
                FontSize = 12,
                Foreground = GetThemeBrush("RuMutedBrush", FallbackMutedBrush),
                VerticalAlignment = VerticalAlignment.Center
            };

            var headerLeft = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { fileIcon, _fileNameBlock }
            };

            var headerRight = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _addedCountBlock, _removedCountBlock, _chevronIcon }
            };

            var headerGrid = new Grid
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Padding = new Thickness(12, 7, 12, 7),
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                }
            };
            Grid.SetColumn(headerLeft, 0);
            Grid.SetColumn(headerRight, 1);
            headerGrid.Children.Add(headerLeft);
            headerGrid.Children.Add(headerRight);
            headerGrid.Tapped += OnHeaderTapped;

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

            var separator = new Border
            {
                Height = 1,
                Background = GetThemeBrush("RuLineBrush", FallbackLineBrush),
                Opacity = 0.5
            };

            _bodyContainer = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition { Height = GridLength.Auto },
                    new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }
                }
            };
            Grid.SetRow(separator, 0);
            Grid.SetRow(scrollViewer, 1);
            _bodyContainer.Children.Add(separator);
            _bodyContainer.Children.Add(scrollViewer);

            var rootGrid = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition { Height = GridLength.Auto },
                    new RowDefinition { Height = GridLength.Auto }
                }
            };
            Grid.SetRow(headerGrid, 0);
            Grid.SetRow(_bodyContainer, 1);
            rootGrid.Children.Add(headerGrid);
            rootGrid.Children.Add(_bodyContainer);

            _rootBorder = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Background = GetThemeBrush("RuSurfaceBrush", FallbackSurfaceBrush),
                BorderBrush = GetThemeBrush("RuLineBrush", FallbackLineBrush),
                Child = rootGrid
            };

            Content = _rootBorder;
            Loaded += (_, _) => RefreshAll();
        }

        private void OnHeaderTapped(object sender, TappedRoutedEventArgs e)
        {
            IsExpanded = !IsExpanded;
        }

        private static void OnPreviewPropertiesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FileEditPreviewControl control)
            {
                control.RefreshAll();
            }
        }

        private static void OnHeaderPropertiesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FileEditPreviewControl control)
            {
                control.UpdateHeader();
            }
        }

        private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FileEditPreviewControl control)
            {
                control.UpdateExpandedState();
            }
        }

        private void UpdateExpandedState()
        {
            _bodyContainer.Visibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed;
            _chevronIcon.Glyph = IsExpanded ? "\uE70E" : "\uE70D"; // ChevronUp vs ChevronDown
        }

        private void RefreshAll()
        {
            UpdateHeader();
            UpdateExpandedState();
            RebuildPreviewLines();
        }

        private void UpdateHeader()
        {
            _fileNameBlock.Text = string.IsNullOrWhiteSpace(FileName) ? "Edit Preview" : FileName;

            int added = AddedCount ?? 0;
            int removed = RemovedCount ?? 0;

            if (added > 0)
            {
                _addedCountBlock.Text = $"+{added}";
                _addedCountBlock.Visibility = Visibility.Visible;
            }
            else
            {
                _addedCountBlock.Visibility = Visibility.Collapsed;
            }

            if (removed > 0)
            {
                _removedCountBlock.Text = $"-{removed}";
                _removedCountBlock.Visibility = Visibility.Visible;
            }
            else
            {
                _removedCountBlock.Visibility = Visibility.Collapsed;
            }
        }

        private void RebuildPreviewLines()
        {
            _previewLinesPanel.Children.Clear();

            if (string.IsNullOrEmpty(BeforeText) && string.IsNullOrEmpty(AfterText))
            {
                return;
            }

            var lines = DiffEngine.Compute(BeforeText, AfterText, StartLine);

            int calculatedAdded = 0;
            int calculatedRemoved = 0;

            var successBrush = GetThemeBrush("RuSuccessBrush", FallbackSuccessBrush);
            var dangerBrush = GetThemeBrush("RuDangerBrush", FallbackDangerBrush);
            var mutedBrush = GetThemeBrush("RuMutedBrush", FallbackMutedBrush);
            var inkBrush = GetThemeBrush("RuInkBrush", FallbackInkBrush);

            foreach (var line in lines)
            {
                if (line.IsAdded) calculatedAdded++;
                if (line.IsRemoved) calculatedRemoved++;

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

            // If header counts were not explicitly supplied, use calculated counts
            if (!AddedCount.HasValue && calculatedAdded > 0)
            {
                _addedCountBlock.Text = $"+{calculatedAdded}";
                _addedCountBlock.Visibility = Visibility.Visible;
            }

            if (!RemovedCount.HasValue && calculatedRemoved > 0)
            {
                _removedCountBlock.Text = $"-{calculatedRemoved}";
                _removedCountBlock.Visibility = Visibility.Visible;
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
