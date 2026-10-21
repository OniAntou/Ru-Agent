using Microsoft.UI.Xaml;

namespace RuApp;

public sealed partial class MainWindow
{
    private void OnSidebarSectionToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Button { Tag: string key })
        {
            Sidebar.ToggleSection(key);
        }
    }
}
