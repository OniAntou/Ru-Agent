using Microsoft.UI.Xaml;
using RuApp.Services;

namespace RuApp.Controls;

public sealed partial class AgentSidePanelControl
{
    private bool _previewChipHovered;
    public void ClearPreview() => FilePreview.Clear();
    public void ShutdownPreview() => FilePreview.Dispose();

    /// <summary>
    /// Opens a workspace file in the read-only preview. The preview's own path
    /// checks enforce workspace bounds, hidden paths and reparse rules.
    /// </summary>
    public async Task OpenPreviewAsync(string workspace, string path)
    {
        OpenPanelTab(SidePanelTabKind.Preview);
        await FilePreview.OpenAsync(workspace, path);
    }
    public void SetPreviewVisibility(bool surfaceVisible) =>
        FilePreview.SetActive(surfaceVisible && _mode == AgentSidePanelMode.Panel && _activeTab == SidePanelTabKind.Preview);
    public void SetPreviewOcclusion(Func<bool> isOccluded) => FilePreview.IsOccluded = isOccluded;
    private void OnPreviewTabClick(object sender, RoutedEventArgs e) => TabActivated?.Invoke(this, SidePanelTabKind.Preview);
    private void OnPreviewTabCloseClick(object sender, RoutedEventArgs e) => TabClosed?.Invoke(this, SidePanelTabKind.Preview);
}
