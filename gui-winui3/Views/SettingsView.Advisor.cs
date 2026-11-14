using Microsoft.UI.Xaml.Controls;
using RuApp.ViewModels;

namespace RuApp.Views;

/// <summary>Forwards the advisor card's input to the view model (ADR 0047); it decides nothing.</summary>
public sealed partial class SettingsView
{
    private void OnAdvisorProviderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AdvisorProviderBox.SelectedItem is AdvisorProviderOption option) ViewModel.AdvisorProvider = option;
    }

    private void OnAdvisorModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AdvisorModelBox.SelectedItem is string model) ViewModel.CommitAdvisorModelCommand.Execute(model);
    }

    private void OnAdvisorModelTextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        ViewModel.CommitAdvisorModelCommand.Execute(args.Text);
        args.Handled = true;
    }
}
