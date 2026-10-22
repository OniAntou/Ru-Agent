using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RuApp.Views;

public sealed partial class SettingsView
{
    private void OnEditProviderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string choice }) ViewModel.EditProviderCommand.Execute(choice);
    }

    private void FocusProviderEditor()
    {
        // Let collapsed panels leave the focus tree before the flyout restores focus.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (ViewModel.IsProviderEditorOpen && IsLoaded)
            {
                PanelModelsScrollViewer.ChangeView(null, 0, null, true);
                ProviderBackButton.Focus(FocusState.Programmatic);
            }
        });
    }
}
