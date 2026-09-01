using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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

        public string SearchText => PickerSearchBox.Text;

        public void Apply(ProviderModelPickerProjection projection)
        {
            PickerListContainer.Children.Clear();
            if (projection.Groups.Count == 0)
            {
                PickerListContainer.Children.Add(
                    CreateEmptyStateText(projection.EmptyStateText));
                return;
            }

            foreach (var group in projection.Groups)
            {
                PickerListContainer.Children.Add(CreateGroupHeading(group));
                foreach (var row in group.Rows)
                {
                    PickerListContainer.Children.Add(CreateRowButton(row));
                }
            }
        }

        public void FocusSearch() => PickerSearchBox.Focus(FocusState.Programmatic);

        private void OnPickerSearchTextChanged(object sender, TextChangedEventArgs e) =>
            SearchTextChanged?.Invoke(this, PickerSearchBox.Text);

        private TextBlock CreateEmptyStateText(string text) => new()
        {
            Text = text,
            FontFamily = (FontFamily)Application.Current.Resources["RuUiFontFamily"],
            FontSize = 11.5,
            Foreground = (Brush)Application.Current.Resources["RuMutedBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 14),
        };

        private Border CreateGroupHeading(ProviderModelPickerGroup group)
        {
            var heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = group.Provider.DisplayName,
                FontFamily = (FontFamily)Application.Current.Resources["RuUiFontFamily"],
                FontSize = 10.5,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Foreground = (Brush)Application.Current.Resources["RuMutedBrush"],
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
                    FontFamily = (FontFamily)Application.Current.Resources["RuUiFontFamily"],
                    FontSize = 9.5,
                    Foreground = (Brush)Application.Current.Resources["RuMutedBrush"],
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                };
                Grid.SetColumn(stale, 1);
                heading.Children.Add(stale);
            }

            var count = new TextBlock
            {
                Text = group.Rows.Count.ToString(),
                FontFamily = (FontFamily)Application.Current.Resources["RuUiFontFamily"],
                FontSize = 9.5,
                Foreground = (Brush)Application.Current.Resources["RuMutedBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(count, 2);
            heading.Children.Add(count);

            return new Border
            {
                Child = heading,
                Padding = new Thickness(7, 4, 7, 2),
                Opacity = 0.85,
                IsHitTestVisible = false,
            };
        }

        private Button CreateRowButton(ProviderModelPickerRow row)
        {
            var itemButton = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(7, 5, 7, 5),
                CornerRadius = (CornerRadius)Application.Current.Resources["RuControlCornerRadius"],
                MinHeight = 28,
                IsEnabled = row.IsEnabled,
            };

            itemButton.Resources["ButtonBackgroundPointerOver"] =
                (Brush)Application.Current.Resources["RuAccentSoftBrush"];
            itemButton.Resources["ButtonBackgroundPressed"] =
                (Brush)Application.Current.Resources["RuAccentSoftBrush"];
            itemButton.Resources["ButtonBorderBrushPointerOver"] =
                new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            itemButton.Resources["ButtonBorderBrushPressed"] =
                new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            var itemGrid = new Grid();
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
                FontFamily = (FontFamily)Application.Current.Resources["RuUiFontFamily"],
                FontSize = 12,
                Foreground = row.IsSelected
                    ? (Brush)Application.Current.Resources["RuInkBrush"]
                    : (Brush)Application.Current.Resources["RuMutedBrush"],
                FontWeight = row.IsSelected
                    ? Microsoft.UI.Text.FontWeights.Medium
                    : Microsoft.UI.Text.FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(textBlock, 0);
            itemGrid.Children.Add(textBlock);

            var (_, specDisplay) = ModelContextSpecs.GetContextSpec(row.ModelId);
            if (!string.IsNullOrWhiteSpace(specDisplay))
            {
                var specBadge = new Border
                {
                    Background = (Brush)Application.Current.Resources["RuAccentSoftBrush"],
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 1, 4, 1),
                    Margin = new Thickness(6, 0, row.IsSelected ? 4 : 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                specBadge.Child = new TextBlock
                {
                    Text = specDisplay,
                    FontFamily = (FontFamily)Application.Current.Resources["RuUiFontFamily"],
                    FontSize = 9.5,
                    Foreground = (Brush)Application.Current.Resources["RuAccentBrush"],
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
                    FontSize = 10,
                    Foreground = (Brush)Application.Current.Resources["RuAccentBrush"],
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(3, 0, 0, 0),
                };
                Grid.SetColumn(checkIcon, 2);
                itemGrid.Children.Add(checkIcon);
            }

            itemButton.Content = itemGrid;

            var automationText = ProviderModelPickerProjection.RowAutomationText(row);
            AutomationProperties.SetName(itemButton, automationText);
            ToolTipService.SetToolTip(itemButton, automationText);

            itemButton.Click += (s, e) => ModelSelected?.Invoke(
                this,
                new ProviderModelSelectionEventArgs(row.Backend, row.ModelId));

            return itemButton;
        }
    }
}
