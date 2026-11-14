using CommunityToolkit.Mvvm.ComponentModel;
using RuAgent.Protocol.Providers;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.ViewModels;

/// <summary>
/// The composer's <c>/advisor</c> command (ADR 0047): the model picker opened to choose the
/// second model the agent may consult in new runs. The Host owns the setting and pins it into
/// each new run; this only shows the stored choice and saves a new one.
/// </summary>
public sealed partial class ChatModelPickerViewModel
{
    internal const string AdvisorAppliesSuffix = "Applies to new runs.";

    /// <summary>True while the open picker chooses the advisor rather than the main model.</summary>
    [ObservableProperty] public partial bool IsPickingAdvisor { get; private set; }

    /// <summary>Opens the next picker for the advisor; the picker shows the stored advisor as selected.</summary>
    internal void BeginAdvisorPick() => IsPickingAdvisor = true;

    /// <summary>"model on provider", or null when no advisor is selected.</summary>
    internal string? AdvisorLabel =>
        CurrentSettings is { AdvisorBackend: { Length: > 0 } backend, AdvisorModel: { Length: > 0 } model }
            ? $"{model} on {ProviderDisplayName(backend)}"
            : null;

    /// <summary>Saves the advisor and returns the line to show the user.</summary>
    internal async Task<string> SelectAdvisorAsync(string model, string backend)
    {
        if (await SaveAdvisorAsync(backend, model).ConfigureAwait(true))
            return $"Advisor: {model} on {ProviderDisplayName(backend)}. {AdvisorAppliesSuffix}";
        return "Could not save the advisor. The previous choice is unchanged.";
    }

    /// <summary>Turns the advisor off and returns the line to show the user.</summary>
    internal async Task<string> ClearAdvisorAsync() =>
        await SaveAdvisorAsync(null, null).ConfigureAwait(true)
            ? $"Advisor off. {AdvisorAppliesSuffix}"
            : "Could not turn the advisor off. The previous choice is unchanged.";

    private async Task<bool> SaveAdvisorAsync(string? backend, string? model)
    {
        if (_backend is not { } api) return false;
        try
        {
            if (!await api.SaveAdvisorAsync(backend, model).ConfigureAwait(true)) return false;
            if (CurrentSettings is { } settings)
            {
                settings.AdvisorBackend = backend;
                settings.AdvisorModel = model;
            }

            return true;
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[ChatModelPicker] Advisor save failed: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// The picker row to mark while choosing the advisor. The Host stores a normalized backend
    /// ("Anthropic") while the picker's groups use choices ("anthropic"), so the stored value is
    /// matched to a listed choice.
    /// </summary>
    private (string Backend, string Model) AdvisorMark(IEnumerable<string> listedBackends)
    {
        if (CurrentSettings is not { AdvisorBackend: { Length: > 0 } stored, AdvisorModel: { Length: > 0 } model })
            return (string.Empty, string.Empty);
        return (listedBackends.FirstOrDefault(choice => SameBackend(choice, stored)) ?? stored, model);
    }

    private string ProviderDisplayName(string backend) =>
        (_grouped?.CurrentDefinitions ?? ProviderGroupDefinitions.All)
            .FirstOrDefault(definition => SameBackend(definition.Alias, backend) || SameBackend(definition.Backend, backend))
            ?.DisplayName
        ?? CurrentSettings?.CustomProviders?.FirstOrDefault(provider => CustomProviderPolicy.Backend(provider.Id) == backend)?.DisplayName
        ?? backend;

    private static bool SameBackend(string choice, string stored) =>
        string.Equals(ProviderAliases.Normalize(choice) ?? choice, ProviderAliases.Normalize(stored) ?? stored,
            StringComparison.OrdinalIgnoreCase);
}
