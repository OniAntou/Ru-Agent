using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RuApp.Services;

namespace RuApp.Views;

/// <summary>
/// Renders <see cref="KeyboardShortcutCatalog"/> as a two-column list, grouped. The
/// shortcuts sheet (Ctrl+/) and the Settings page show the same element, so they cannot
/// list different keys.
/// </summary>
internal static class KeyboardShortcutList
{
    internal static Grid Build()
    {
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        foreach (var group in KeyboardShortcutCatalog.All.GroupBy(shortcut => shortcut.Group))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new TextBlock
            {
                Text = group.Key,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Resource<Brush>("RuInkBrush"),
                Margin = new Thickness(0, row == 0 ? 0 : 12, 0, 0),
            };
            Grid.SetRow(header, row++);
            Grid.SetColumnSpan(header, 2);
            grid.Children.Add(header);
            foreach (var shortcut in group)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var keys = new TextBlock
                {
                    Text = shortcut.Keys,
                    FontFamily = Resource<FontFamily>("RuMonoFontFamily"),
                    Foreground = Resource<Brush>("RuMutedBrush"),
                };
                var description = new TextBlock
                {
                    Text = shortcut.Description,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Resource<Brush>("RuInkBrush"),
                };
                Grid.SetRow(keys, row);
                Grid.SetRow(description, row++);
                Grid.SetColumn(description, 1);
                grid.Children.Add(keys);
                grid.Children.Add(description);
            }
        }

        return grid;
    }

    private static T Resource<T>(string key) where T : class => (T)Application.Current.Resources[key];
}
