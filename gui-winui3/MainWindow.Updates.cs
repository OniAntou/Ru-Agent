namespace RuApp;

public sealed partial class MainWindow
{
    private void OnTitleBarUpdateClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_settingsView is null) return;
        OpenSettingsPopup();
        _settingsView.ShowUpdates();
    }

    // UpdateMaintenance already shut down the idle host before handing off.
    private bool _updateHandOff;
}
