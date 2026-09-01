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
        }

        public void FocusSearch() => PickerSearchBox.Focus(FocusState.Programmatic);

        private void OnPickerSearchTextChanged(object sender, TextChangedEventArgs e) =>
            SearchTextChanged?.Invoke(this, PickerSearchBox.Text);

        private TextBlock CreateEmptyStateText(string text) => new()
        {
            Text = text,
            FontFamily = (FontFamily)Application.Current.Resources["RuUiFontFamily"],
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["RuMutedBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 16),
        };

        private Border CreateGroupDivider() => new()
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["RuLineBrush"],
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
                FontFamily = (FontFamily)Application.Current.Resources["RuUiFontFamily"],
                FontSize = 10.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
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
                    Foreground = (Brush)Application.Current.Resources["RuWarningBrush"],
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
            var defaultBrush = row.IsSelected
                ? (Brush)Application.Current.Resources["RuAccentSoftBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var hoverBrush = row.IsSelected
                ? (Brush)Application.Current.Resources["RuLineBrush"]
                : (Brush)Application.Current.Resources["RuCanvasBrush"];

            var itemButton = new Button
            {
                Style = (Style)Resources["PickerItemButtonStyle"],
                Background = defaultBrush,
                IsEnabled = row.IsEnabled,
            };

            // Flicker-free pointer state handlers
            itemButton.PointerEntered += (s, e) => itemButton.Background = hoverBrush;
            itemButton.PointerExited += (s, e) => itemButton.Background = defaultBrush;
            itemButton.PointerCanceled += (s, e) => itemButton.Background = defaultBrush;
            itemButton.PointerCaptureLost += (s, e) => itemButton.Background = defaultBrush;

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
                FontFamily = (FontFamily)Application.Current.Resources["RuUiFontFamily"],
                FontSize = 12.5,
                Foreground = (Brush)Application.Current.Resources["RuInkBrush"],
                FontWeight = row.IsSelected
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
                Opacity = row.IsSelected ? 1.0 : 0.88,
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
                    Background = (Brush)Application.Current.Resources["RuSurfaceBrush"],
                    BorderBrush = (Brush)Application.Current.Resources["RuLineBrush"],
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3.5),
                    Padding = new Thickness(4.5, 1, 4.5, 1),
                    Margin = new Thickness(6, 0, row.IsSelected ? 5 : 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                specBadge.Child = new TextBlock
                {
                    Text = specDisplay,
                    FontFamily = (FontFamily)Application.Current.Resources["RuUiFontFamily"],
                    FontSize = 9.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                    Foreground = (Brush)Application.Current.Resources["RuMutedBrush"],
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
                    Foreground = (Brush)Application.Current.Resources["RuAccentBrush"],
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
