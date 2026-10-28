using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using RuApp.Services;

namespace RuApp.Views;

/// <summary>
/// The computer-use opt-in (ADR 0031). The Host owns the setting and pins it into new
/// runs; this view only shows and saves it, and reverts the switch when a save fails.
/// </summary>
public sealed partial class SettingsView
{
    private ApiService? _computerUseApi;
    private bool _applyingComputerUse;

    private async Task LoadComputerUseAsync(ApiService apiService)
    {
        _computerUseApi = apiService;
        var settings = await apiService.GetSettingsAsync();
        DispatchToUi(() => SetComputerUseToggle(settings?.ComputerUseEnabled == true));
    }

    private void SetComputerUseToggle(bool enabled)
    {
        _applyingComputerUse = true;
        try { ComputerUseToggle.IsOn = enabled; }
        finally { _applyingComputerUse = false; }
    }

    private async void OnComputerUseToggled(object sender, RoutedEventArgs e)
    {
        if (_applyingComputerUse || _computerUseApi is not { } api) return;
        var requested = ComputerUseToggle.IsOn;
        ComputerUseToggle.IsEnabled = false;
        try
        {
            if (!await api.SaveComputerUseAsync(requested)) SetComputerUseToggle(!requested);
        }
        catch (Exception error)
        {
            // async void: a failed save must leave the switch where the Host has it.
            GuiDiagnosticWriter.AppendTraceLine($"[Settings] Computer use save failed: {error.GetType().Name}");
            SetComputerUseToggle(!requested);
        }
        finally
        {
            ComputerUseToggle.IsEnabled = true;
        }
    }
}
