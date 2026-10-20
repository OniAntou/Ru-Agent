using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RuApp.Views;

public sealed partial class SettingsView
{
    private sealed record SettingRow(Grid Grid, FrameworkElement Field, Thickness Margin);
    private readonly List<SettingRow> _settingRows = [];
    private readonly Dictionary<FrameworkElement, double> _settingFieldWidths = [];
    private readonly Dictionary<StackPanel, Orientation> _settingFieldOrientations = [];
    private bool? _settingsNarrow;

    private void InitializeSettingsLayout()
    {
        Loaded += (_, _) => ApplySettingsLayout();
        SizeChanged += (_, _) => ApplySettingsLayout();
    }

    private void ApplySettingsLayout()
    {
        if (ActualWidth <= 0) return;
        if (_settingRows.Count == 0)
        {
            foreach (var grid in SettingsDescendants(SettingsLayout).OfType<Grid>().Where(grid => grid.Tag as string == "SettingsRow"))
            {
                var field = grid.Children.OfType<FrameworkElement>().First(child => Grid.GetColumn(child) == 1);
                _settingRows.Add(new(grid, field, field.Margin));
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                foreach (var element in SettingsDescendants(field).OfType<FrameworkElement>().Prepend(field))
                {
                    if (double.IsFinite(element.Width)) _settingFieldWidths.TryAdd(element, element.Width);
                    if (element is StackPanel panel) _settingFieldOrientations.TryAdd(panel, panel.Orientation);
                }
            }
        }
        // Reserve navigation, body padding and a readable label beside fields.
        // The old 700px modal threshold left labels squeezed at 800px windows.
        bool narrow = ActualWidth < 840;
        if (_settingsNarrow == narrow) return;
        _settingsNarrow = narrow;
        SettingsNavigationColumn.Width = narrow ? new GridLength(0) : new GridLength(200);
        SettingsNavigation.BorderThickness = narrow ? new Thickness(0, 0, 0, 1) : new Thickness(0, 0, 1, 0);
        Grid.SetColumn(SettingsNavigation, 0);
        Grid.SetColumnSpan(SettingsNavigation, narrow ? 2 : 1);
        Grid.SetRowSpan(SettingsNavigation, narrow ? 1 : 2);
        SettingsNavigationButtons.Orientation = narrow ? Orientation.Horizontal : Orientation.Vertical;
        SettingsNavigationHeading.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(SettingsBody, narrow ? 0 : 1);
        Grid.SetColumnSpan(SettingsBody, narrow ? 2 : 1);
        Grid.SetRow(SettingsBody, narrow ? 1 : 0);
        Grid.SetRowSpan(SettingsBody, narrow ? 1 : 2);
        foreach (var row in _settingRows)
        {
            row.Grid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : GridLength.Auto;
            Grid.SetColumn(row.Field, narrow ? 0 : 1);
            Grid.SetRow(row.Field, narrow ? 1 : 0);
            row.Field.Margin = narrow ? new Thickness(0, 10, 0, 0) : row.Margin;
        }
        foreach (var (element, width) in _settingFieldWidths) element.Width = narrow ? double.NaN : width;
        foreach (var (panel, orientation) in _settingFieldOrientations) panel.Orientation = narrow ? Orientation.Vertical : orientation;
    }

    private static IEnumerable<DependencyObject> SettingsDescendants(DependencyObject root)
    {
        // Walk authored children too: collapsed tabs have no realized visual subtree.
        IEnumerable<DependencyObject> children = root switch
        {
            Panel panel => panel.Children.Cast<DependencyObject>(),
            Border { Child: { } child } => [child],
            ContentControl { Content: DependencyObject content } => [content],
            _ => Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(root))
                .Select(index => VisualTreeHelper.GetChild(root, index)),
        };
        foreach (var child in children)
        {
            yield return child;
            foreach (var descendant in SettingsDescendants(child)) yield return descendant;
        }
    }
}
