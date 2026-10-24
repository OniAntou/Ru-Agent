using Microsoft.UI.Xaml.Controls;
using RuApp.Services;

namespace RuApp.Controls;

public sealed partial class TurnChangesDetailsControl : UserControl
{
    public TurnChangesDetailsControl() => InitializeComponent();

    public void ShowSummary(TurnChangeSummary summary, TurnChangedFile? selected)
    {
        FilePicker.ItemsSource = summary.Files;
        FilePicker.SelectedItem = selected ?? summary.Files.FirstOrDefault();
    }

    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs args) =>
        EditsList.ItemsSource = (FilePicker.SelectedItem as TurnChangedFile)?.Edits;
}
