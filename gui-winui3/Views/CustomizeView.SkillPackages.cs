using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuAgent.Client;
using RuApp.Services;

namespace RuApp.Views;

public sealed partial class CustomizeView
{
    private bool _installingPackage;

    private async void OnInstallPackageClick(object sender, RoutedEventArgs e) => await InstallSkillPackageAsync(update: false);

    private async void OnUpdatePackageClick(object sender, RoutedEventArgs e) => await InstallSkillPackageAsync(update: true);

    private async Task InstallSkillPackageAsync(bool update)
    {
        if (_installingPackage || !TryGetSkillsTarget(out var skills, out var workspace)) return;
        var input = new TextBox
        {
            Header = "GitHub skill folder at a full commit SHA",
            PlaceholderText = "https://github.com/owner/repo/tree/<40-character SHA>/skills/name",
            MinWidth = 420,
        };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(input);
        body.Children.Add(new TextBlock
        {
            Text = update
                ? "Replaces an unchanged managed package from the same source. Review the source before updating."
                : "Installs into your Ru skill folder. Review the source before installing; skill instructions can influence Ru's work.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
        });
        var dialog = new ContentDialog
        {
            Title = update ? "Update skill package" : "Install skill package",
            Content = body,
            PrimaryButtonText = update ? "Update" : "Install",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        var result = await ContentDialogGate.ShowAsync(dialog, () => _active);
        if (result != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text) || !_active) return;
        _installingPackage = true;
        AddButton.IsEnabled = false;
        try
        {
            var installed = await skills.InstallPackageAsync(
                new SkillPackageInstallRequest(workspace, input.Text.Trim(), update ? "update" : "install"), BeginSkillWork());
            if (!_active) return;
            await LoadSkillsAsync(refresh: true);
            ShowStatus($"{(update ? "Updated" : "Installed")} {installed.Receipt.Name} · {installed.Receipt.FileCount} files · {installed.Receipt.Commit[..12]}");
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (_active) ShowStatus($"Could not {(update ? "update" : "install")} skill package: {error.Message}");
        }
        finally
        {
            _installingPackage = false;
            AddButton.IsEnabled = true;
        }
    }
}
