using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RuApp.Models;

namespace RuApp.Views
{
    public sealed class ProviderModelSelectionEventArgs : EventArgs
    {
        public ProviderModelSelectionEventArgs(string backend, string model)
        {
            Backend = backend;
            Model = model;
        }

        public string Backend { get; }

        public string Model { get; }
    }

    public sealed partial class ProviderModelPicker : UserControl
    {
        public ProviderModelPicker()
        {
            this.InitializeComponent();
        }

        public event EventHandler<string>? SearchTextChanged;

        public event EventHandler<ProviderModelSelectionEventArgs>? ModelSelected;

        private ProviderModelPickerProjection? _lastProjection;
        private PickerBrushes _brushes = PickerBrushes.Resolve();

        public string SearchText => PickerSearchBox.Text;

        public void Apply(ProviderModelPickerProjection projection)
        {
            // A poll can hand back exactly what the last one returned, and the picker
            // is rebuilt on every publish. Rebuilding an identical tree costs the UI
            // thread a thousand control constructions for nothing.
            if (projection.HasSameContentAs(_lastProjection))
            {
                return;
            }

            _lastProjection = projection;
            PickerListContainer.Children.Clear();
            if (projection.Groups.Count == 0)
            {
                PickerListContainer.Children.Add(
                    CreateEmptyStateText(projection.EmptyStateText));
                return;
            }

            for (int i = 0; i < projection.Groups.Count; i++)
            {
                var group = projection.Groups[i];
                if (i > 0)
                {
                    PickerListContainer.Children.Add(CreateGroupDivider());
                }
                PickerListContainer.Children.Add(CreateGroupHeading(group, i == 0));
                foreach (var row in group.Rows)
                {
                    PickerListContainer.Children.Add(CreateRowButton(row));
                }
            }

            if (!string.IsNullOrEmpty(projection.TruncationNotice))
            {
                PickerListContainer.Children.Add(CreateTruncationNotice(projection.TruncationNotice));
            }
        }

        public void FocusSearch() => PickerSearchBox.Focus(FocusState.Programmatic);

        private void OnPickerSearchTextChanged(object sender, TextChangedEventArgs e) =>
            SearchTextChanged?.Invoke(this, PickerSearchBox.Text);

        // One shared delegate per event instead of four closures allocated for every
        // row on every rebuild. The row travels on Tag.
        private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is ProviderModelPickerRow row)
            {
                button.Background = row.IsSelected ? _brushes.SelectedHover : _brushes.Hover;
            }
        }

        private void OnRowPointerLeft(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is ProviderModelPickerRow row)
            {
                button.Background = row.IsSelected ? _brushes.Selected : _brushes.Rest;
            }
        }

        /// <summary>
        /// Resolved once instead of per row. The previous shape read these from
        /// Application.Current resources inside every row builder, which is five
        /// dictionary lookups for every control on every rebuild.
        /// </summary>
        private sealed record PickerBrushes(
            FontFamily FontFamily,
            Brush Ink,
            Brush Muted,
            Brush Line,
            Brush Surface,
            Brush Accent,
            Brush Warning,
            Brush Rest,
            Brush Hover,
            Brush Selected,
            Brush SelectedHover,
            Style ItemStyle)
        {
            public static PickerBrushes Resolve()
            {
                var resources = Application.Current.Resources;
                return new PickerBrushes(
                    (FontFamily)resources["RuUiFontFamily"],
                    (Brush)resources["RuInkBrush"],
                    (Brush)resources["RuMutedBrush"],
                    (Brush)resources["RuLineBrush"],
                    (Brush)resources["RuSurfaceBrush"],
                    (Brush)resources["RuAccentBrush"],
                    (Brush)resources["RuWarningBrush"],
                    new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    (Brush)resources["RuCanvasBrush"],
                    (Brush)resources["RuAccentSoftBrush"],
                    (Brush)resources["RuLineBrush"],
                    (Style)resources["PickerItemButtonStyle"]);
            }
        }

        private TextBlock CreateEmptyStateText(string text) => new()
        {
            Text = text,
            FontFamily = _brushes.FontFamily,
            FontSize = 12,
            Foreground = _brushes.Muted,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 16),
        };

        private TextBlock CreateTruncationNotice(string text) => new()
        {
            Text = text,
            FontFamily = _brushes.FontFamily,
            FontSize = 11,
            Foreground = _brushes.Muted,
            Opacity = 0.8,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 6),
        };

        private Border CreateGroupDivider() => new()
        {
            Height = 1,
            Background = _brushes.Line,
            Opacity = 0.35,
            Margin = new Thickness(6, 6, 6, 3),
        };

        private Border CreateGroupHeading(ProviderModelPickerGroup group, bool isFirstGroup)
        {
            var heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = group.Provider.DisplayName,
                FontFamily = _brushes.FontFamily,
                FontSize = 10.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = _brushes.Muted,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 0);
            heading.Children.Add(name);

            if (group.IsStale)
            {
                var stale = new TextBlock
                {
                    Text = ProviderModelPickerProjection.StaleAnnotation,
                    FontFamily = _brushes.FontFamily,
                    FontSize = 9.5,
                    Foreground = _brushes.Warning,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0),
                };
                Grid.SetColumn(stale, 1);
                heading.Children.Add(stale);
            }

            return new Border
            {
                Child = heading,
                Padding = new Thickness(8, isFirstGroup ? 3 : 5, 8, 3),
                IsHitTestVisible = false,
            };
        }

        private Button CreateRowButton(ProviderModelPickerRow row)
        {
            var itemButton = new Button
            {
                Style = _brushes.ItemStyle,
                Background = row.IsSelected ? _brushes.Selected : _brushes.Rest,
                IsEnabled = row.IsEnabled,
                Tag = row,
            };

            itemButton.PointerEntered += OnRowPointerEntered;
            itemButton.PointerExited += OnRowPointerLeft;
            itemButton.PointerCanceled += OnRowPointerLeft;
            itemButton.PointerCaptureLost += OnRowPointerLeft;

            var itemGrid = new Grid
            {
                IsHitTestVisible = false // Prevents child elements from bubbling micro-pointer events
            };
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (row.IsSelected)
            {
                itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            var textBlock = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(row.Annotation)
                    ? row.ModelId
                    : $"{row.ModelId} · {row.Annotation}",
                FontFamily = _brushes.FontFamily,
                FontSize = 12.5,
                Foreground = _brushes.Ink,
                FontWeight = row.IsSelected
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
                Opacity = row.IsSelected ? 1.0 : 0.88,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(textBlock, 0);
            itemGrid.Children.Add(textBlock);

            var (_, specDisplay) = ModelContextSpecs.GetContextSpec(row.ModelId, row.ContextLength);
            if (!string.IsNullOrWhiteSpace(specDisplay))
            {
                var specBadge = new Border
                {
                    Background = _brushes.Surface,
                    BorderBrush = _brushes.Line,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3.5),
                    Padding = new Thickness(4.5, 1, 4.5, 1),
                    Margin = new Thickness(6, 0, row.IsSelected ? 5 : 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                specBadge.Child = new TextBlock
                {
                    Text = specDisplay,
                    FontFamily = _brushes.FontFamily,
                    FontSize = 9.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                    Foreground = _brushes.Muted,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(specBadge, 1);
                itemGrid.Children.Add(specBadge);
            }

            if (row.IsSelected)
            {
                var checkIcon = new FontIcon
                {
                    Glyph = "\uE73E",
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = _brushes.Accent,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0),
                };
                Grid.SetColumn(checkIcon, 2);
                itemGrid.Children.Add(checkIcon);
            }

            itemButton.Content = itemGrid;

            var automationText = ProviderModelPickerProjection.RowAutomationText(row);
            AutomationProperties.SetName(itemButton, automationText);

            itemButton.Click += (s, e) => ModelSelected?.Invoke(
                this,
                new ProviderModelSelectionEventArgs(row.Backend, row.ModelId));

            return itemButton;
        }
    }
}
