using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Services;

namespace RuApp.Controls;

public sealed partial class TurnChangesSummaryControl
{
    private async void OnViewChangesClick(object sender, RoutedEventArgs args) => await ShowDetailsAsync(null);

    private async void OnFileClick(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: TurnChangedFile file }) await ShowDetailsAsync(file);
    }

    private async Task ShowDetailsAsync(TurnChangedFile? selected)
    {
        if (_openingDetails || _summary.Files.Count == 0 || Message is not { } owner || XamlRoot is not { } root) return;
        _openingDetails = true;
        var focus = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) as Control;
        TurnChangesDetailsControl? body = null;
        void ResizeBody()
        {
            if (body is null) return;
            body.Width = Math.Max(0, Math.Min(820, root.Size.Width - 96));
            body.MaxHeight = Math.Max(0, Math.Min(520, root.Size.Height - 180));
        }
        void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ResizeBody();
        try
        {
            body = new TurnChangesDetailsControl();
            body.ShowSummary(_summary, selected);
            var dialog = new ContentDialog
            {
                Title = "Changes in this turn", Content = body, CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close, XamlRoot = root,
                RequestedTheme = ActualTheme,
            };
            dialog.Resources["ContentDialogMaxWidth"] = 920d;
            ResizeBody();
            root.Changed += OnRootChanged;
            _detailsDialog = dialog;
            DetailsError.Visibility = Visibility.Collapsed;
            await ContentDialogGate.ShowAsync(dialog,
                () => IsLoaded && ReferenceEquals(Message, owner) && !owner.IsStreaming);
        }
        catch (Exception error)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[TurnChanges] Could not show recorded changes: {error.GetType().Name}");
            if (IsLoaded && ReferenceEquals(Message, owner))
            {
                DetailsError.Text = "Could not open changes. Please try again.";
                DetailsError.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            root.Changed -= OnRootChanged;
            _detailsDialog = null;
            _openingDetails = false;
            if (IsLoaded && ReferenceEquals(Message, owner)) focus?.Focus(FocusState.Programmatic);
        }
    }
}
