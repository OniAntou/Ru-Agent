using CommunityToolkit.Mvvm.ComponentModel;
using RuAgent.Protocol.Providers;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.ViewModels;

/// <summary>
/// The composer's <c>/advisor</c> command (ADR 0047): the model picker opened to choose the
/// second model the agent may consult in this chat. Each session has its own advisor, held by
/// the Host and pinned into the session's new runs; a new chat has none. A choice made before
/// the chat's session exists waits here and is saved when the first message creates it.
/// </summary>
public sealed partial class ChatModelPickerViewModel
{
    internal const string AdvisorAppliesSuffix = "Applies to this chat's next run.";

    private string? _advisorBackend;
    private string? _advisorModel;
    private bool _advisorPending;

    /// <summary>True while the open picker chooses the advisor rather than the main model.</summary>
    [ObservableProperty] public partial bool IsPickingAdvisor { get; private set; }

    /// <summary>Opens the next picker for the advisor; the picker shows the chat's advisor as selected.</summary>
    internal void BeginAdvisorPick() => IsPickingAdvisor = true;

    /// <summary>"model on provider", or null when this chat has no advisor.</summary>
    internal string? AdvisorLabel =>
        _advisorBackend is { Length: > 0 } backend && _advisorModel is { Length: > 0 } model
            ? $"{model} on {ProviderDisplayName(backend)}"
            : null;

    /// <summary>Shows the advisor of the session just opened; null for a new chat.</summary>
    internal void ShowSessionAdvisor(string? backend, string? model)
    {
        _advisorBackend = backend;
        _advisorModel = model;
        _advisorPending = false;
    }

    /// <summary>Saves the advisor for this chat and returns the line to show the user.</summary>
    internal async Task<string> SelectAdvisorAsync(string model, string backend, string? sessionId)
    {
        if (await SaveAdvisorAsync(sessionId, backend, model).ConfigureAwait(true))
            return $"Advisor: {model} on {ProviderDisplayName(backend)}. {AdvisorAppliesSuffix}";
        return "Could not save the advisor. The previous choice is unchanged.";
    }

    /// <summary>Turns this chat's advisor off and returns the line to show the user.</summary>
    internal async Task<string> ClearAdvisorAsync(string? sessionId) =>
        await SaveAdvisorAsync(sessionId, null, null).ConfigureAwait(true)
            ? $"Advisor off. {AdvisorAppliesSuffix}"
            : "Could not turn the advisor off. The previous choice is unchanged.";

    /// <summary>
    /// Saves a choice made before the session existed to the session just created; false when
    /// the save failed, and the run then starts without the advisor.
    /// </summary>
    internal async Task<bool> ApplyPendingAdvisorAsync(string sessionId)
    {
        if (!_advisorPending) return true;
        _advisorPending = false;
        return _backend is { } api && await api.SaveSessionAdvisorAsync(sessionId, _advisorBackend, _advisorModel).ConfigureAwait(true);
    }

    private async Task<bool> SaveAdvisorAsync(string? sessionId, string? backend, string? model)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            ShowSessionAdvisor(backend, model);
            _advisorPending = true;
            return true;
        }

        if (_backend is not { } api) return false;
        try
        {
            if (!await api.SaveSessionAdvisorAsync(sessionId, backend, model).ConfigureAwait(true)) return false;
            ShowSessionAdvisor(backend, model);
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
        if (_advisorBackend is not { Length: > 0 } stored || _advisorModel is not { Length: > 0 } model)
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
