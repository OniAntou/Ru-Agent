using Microsoft.UI.Xaml;
using RuApp.Services;

namespace RuApp.Views;

/// <summary>
/// The Plugins tab's built-in section. Computer use (ADR 0031): the Host owns the
/// opt-in and pins it into new runs; this tab shows and saves it, and puts the switch
/// back where the Host has it when a save fails. User plugins (ADR 0044) live in
/// <c>CustomizeView.UserPlugins.cs</c>.
/// </summary>
public sealed partial class CustomizeView
{
    private bool _applyingComputerUse;

    private async Task LoadComputerUseAsync()
    {
        var api = _api;
        if (api is null) return;
        var generation = _activationGeneration;
        var settings = await api.GetSettingsAsync();
        if (!_active || generation != _activationGeneration || !ReferenceEquals(api, _api)) return;
        if (settings is not null)
        {
            SetComputerUseToggle(settings.ComputerUseEnabled);
            SetBrowserProfileToggle(settings.BrowserPersistentProfile);
        }
        else if (PluginsTab.IsChecked == true) ShowStatus("Could not load plugin settings.");
    }

    private bool _applyingBrowserProfile;

    private void SetBrowserProfileToggle(bool enabled)
    {
        _applyingBrowserProfile = true;
        try { BrowserProfileToggle.IsOn = enabled; }
        finally { _applyingBrowserProfile = false; }
    }

    // ADR 0055 amendment 2026-11-25: the Host owns the persistent-profile opt-in and pins it into new runs.
    private async void OnBrowserProfileToggled(object sender, RoutedEventArgs e)
    {
        if (_applyingBrowserProfile || _api is not { } api) return;
        var requested = BrowserProfileToggle.IsOn;
        BrowserProfileToggle.IsEnabled = false;
        try
        {
            if (!await api.SaveBrowserProfileAsync(requested)) SetBrowserProfileToggle(!requested);
        }
        catch (Exception error)
        {
            // async void: a failed save must leave the switch where the Host has it.
            GuiDiagnosticWriter.AppendTraceLine($"[Customize] Browser profile save failed: {error.GetType().Name}");
            SetBrowserProfileToggle(!requested);
        }
        finally
        {
            BrowserProfileToggle.IsEnabled = true;
        }
    }

    private void SetComputerUseToggle(bool enabled)
    {
        _applyingComputerUse = true;
        try { ComputerUseToggle.IsOn = enabled; }
        finally { _applyingComputerUse = false; }
    }

    private async void OnComputerUseToggled(object sender, RoutedEventArgs e)
    {
        if (_applyingComputerUse || _api is not { } api) return;
        var requested = ComputerUseToggle.IsOn;
        ComputerUseToggle.IsEnabled = false;
        try
        {
            if (!await api.SaveComputerUseAsync(requested)) SetComputerUseToggle(!requested);
        }
        catch (Exception error)
        {
            // async void: a failed save must leave the switch where the Host has it.
            GuiDiagnosticWriter.AppendTraceLine($"[Customize] Computer use save failed: {error.GetType().Name}");
            SetComputerUseToggle(!requested);
        }
        finally
        {
            ComputerUseToggle.IsEnabled = true;
        }
    }
}
