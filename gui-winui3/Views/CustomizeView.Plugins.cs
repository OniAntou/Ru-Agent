using Microsoft.UI.Xaml;
using RuApp.Services;

namespace RuApp.Views;

/// <summary>
/// The Plugins tab. Computer use (ADR 0031) is its one plugin: the Host owns the
/// opt-in and pins it into new runs; this tab shows and saves it, and puts the switch
/// back where the Host has it when a save fails.
/// </summary>
public sealed partial class CustomizeView
{
    private bool _applyingComputerUse;

    private async Task LoadComputerUseAsync()
    {
        if (_api is null) return;
        var settings = await _api.GetSettingsAsync();
        if (_active) SetComputerUseToggle(settings?.ComputerUseEnabled == true);
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
