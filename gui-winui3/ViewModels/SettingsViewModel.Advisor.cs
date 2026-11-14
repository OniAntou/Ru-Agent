using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RuAgent.Protocol.Providers;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.ViewModels;

/// <summary>
/// The advisor card (ADR 0047): a second model the agent may consult in new runs. The
/// Host owns the setting and pins it into each new run; this card shows it, offers the
/// connected providers and their models, and saves the pair once a model is chosen.
/// </summary>
public sealed partial class SettingsViewModel
{
    internal const string AdvisorSavedSuffix = "Applies to new runs.";

    public ObservableCollection<AdvisorProviderOption> AdvisorProviders { get; } = new();

    public ObservableCollection<string> AdvisorModels { get; } = new();

    [ObservableProperty] public partial AdvisorProviderOption? AdvisorProvider { get; set; }

    [ObservableProperty] public partial string AdvisorModel { get; set; } = string.Empty;

    [ObservableProperty] public partial string AdvisorStatus { get; private set; } = "Off.";

    [ObservableProperty] public partial bool IsAdvisorModelVisible { get; private set; }

    [ObservableProperty] public partial bool IsAdvisorEnabled { get; private set; } = true;

    private bool _applyingAdvisor;
    private string? _advisorModelsFor;
    private long _advisorModelsRequest;
    private long _advisorSaveRequest;

    /// <summary>Rebuilds the provider choices from the connected providers and shows the stored advisor.</summary>
    private void RefreshAdvisor()
    {
        string? storedBackend = _lastSettings?.AdvisorBackend;
        string? storedModel = _lastSettings?.AdvisorModel;
        bool hasStored = !string.IsNullOrWhiteSpace(storedBackend) && !string.IsNullOrWhiteSpace(storedModel);
        var options = new List<AdvisorProviderOption> { AdvisorProviderOption.Off };
        options.AddRange(ConfiguredProviders.Select(row => new AdvisorProviderOption(row.Choice, row.DisplayName)));
        AdvisorProviderOption selected = AdvisorProviderOption.Off;
        if (hasStored)
        {
            selected = options.FirstOrDefault(option => !option.IsOff && SameBackend(option.Choice, storedBackend!))
                ?? new AdvisorProviderOption(storedBackend!, $"{storedBackend} (not connected)");
            if (!options.Contains(selected)) options.Add(selected);
        }

        _applyingAdvisor = true;
        try
        {
            // This runs on every provider refresh. Clearing the list drops the combo box's
            // selection, and re-assigning an equal option would not notify, so the list is
            // rebuilt only when it changed and the selection is then re-published.
            if (!AdvisorProviders.SequenceEqual(options))
            {
                AdvisorProviders.Clear();
                foreach (var option in options) AdvisorProviders.Add(option);
                AdvisorProvider = null;
            }

            ShowAdvisor(selected, hasStored ? storedModel! : string.Empty);
            AdvisorStatus = hasStored ? $"{storedModel} on {selected.DisplayName}. {AdvisorSavedSuffix}" : "Off.";
        }
        finally
        {
            _applyingAdvisor = false;
        }

        if (!selected.IsOff) _ = LoadAdvisorModelsAsync(selected.Choice);
    }

    /// <summary>Shows a provider and model without saving them.</summary>
    private void ShowAdvisor(AdvisorProviderOption provider, string model)
    {
        bool applying = _applyingAdvisor;
        _applyingAdvisor = true;
        try
        {
            AdvisorProvider = provider;
            AdvisorModel = model;
            IsAdvisorModelVisible = !provider.IsOff;
        }
        finally
        {
            _applyingAdvisor = applying;
        }
    }

    /// <summary>Puts the card back on the advisor the Host holds, after a save it refused.</summary>
    private void ShowStoredAdvisor()
    {
        string? storedBackend = _lastSettings?.AdvisorBackend;
        string? storedModel = _lastSettings?.AdvisorModel;
        if (string.IsNullOrWhiteSpace(storedBackend) || string.IsNullOrWhiteSpace(storedModel))
        {
            ShowAdvisor(AdvisorProviderOption.Off, string.Empty);
            return;
        }

        var provider = AdvisorProviders.FirstOrDefault(option => !option.IsOff && SameBackend(option.Choice, storedBackend))
            ?? AdvisorProviderOption.Off;
        ShowAdvisor(provider, provider.IsOff ? string.Empty : storedModel);
    }

    partial void OnAdvisorProviderChanged(AdvisorProviderOption? value)
    {
        if (_applyingAdvisor) return;
        IsAdvisorModelVisible = value is { IsOff: false };
        if (value is null || value.IsOff)
        {
            _ = SaveAdvisorAsync(null, null);
            return;
        }

        // A provider alone is not an advisor: the pair is saved once a model is chosen.
        _applyingAdvisor = true;
        try { AdvisorModel = string.Empty; }
        finally { _applyingAdvisor = false; }
        AdvisorStatus = "Choose a model to finish.";
        _ = LoadAdvisorModelsAsync(value.Choice);
    }

    /// <summary>Saves the chosen model with the selected provider; blank input and the off row save nothing.</summary>
    [RelayCommand]
    private Task CommitAdvisorModelAsync(string? model)
    {
        var provider = AdvisorProvider;
        string trimmed = model?.Trim() ?? string.Empty;
        if (provider is null || provider.IsOff || trimmed.Length == 0 || trimmed.Any(char.IsWhiteSpace))
            return Task.CompletedTask;
        if (string.Equals(trimmed, _lastSettings?.AdvisorModel, StringComparison.Ordinal)
            && SameBackend(provider.Choice, _lastSettings?.AdvisorBackend))
            return Task.CompletedTask;
        return SaveAdvisorAsync(provider.Choice, trimmed);
    }

    private async Task SaveAdvisorAsync(string? backend, string? model)
    {
        var api = _backend;
        if (api is null) return;
        long request = ++_advisorSaveRequest;
        IsAdvisorEnabled = false;
        try
        {
            bool saved = await api.SaveAdvisorAsync(backend, model);
            if (request != _advisorSaveRequest || !ReferenceEquals(api, _backend)) return;
            if (!saved)
            {
                ShowStoredAdvisor();
                AdvisorStatus = "Could not save the advisor. The previous choice is unchanged.";
                return;
            }

            if (_lastSettings is { } stored)
            {
                stored.AdvisorBackend = backend;
                stored.AdvisorModel = model;
            }

            AdvisorStatus = model is null
                ? $"Off. {AdvisorSavedSuffix}"
                : $"{model} on {AdvisorProvider?.DisplayName ?? backend}. {AdvisorSavedSuffix}";
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[SettingsViewModel] Advisor save failed: {ex.GetType().Name}");
            if (request == _advisorSaveRequest)
            {
                ShowStoredAdvisor();
                AdvisorStatus = "Could not save the advisor. The previous choice is unchanged.";
            }
        }
        finally
        {
            if (request == _advisorSaveRequest) IsAdvisorEnabled = true;
        }
    }

    private async Task LoadAdvisorModelsAsync(string backend)
    {
        var api = _backend;
        if (api is null || string.Equals(_advisorModelsFor, backend, StringComparison.Ordinal)) return;
        _advisorModelsFor = backend;
        long request = ++_advisorModelsRequest;
        AdvisorModels.Clear();
        try
        {
            var models = await api.GetModelsAsync(backend);
            if (request != _advisorModelsRequest || !ReferenceEquals(api, _backend)) return;
            if (models is null)
            {
                // Let a later open retry, and keep typing a model id possible meanwhile.
                _advisorModelsFor = null;
                if (AdvisorModel.Length == 0) AdvisorStatus = "Could not load this provider's models. Type a model ID.";
                return;
            }

            foreach (var id in models.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
                AdvisorModels.Add(id);
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[SettingsViewModel] Advisor models failed: {ex.GetType().Name}");
            if (request == _advisorModelsRequest) _advisorModelsFor = null;
        }
    }

    /// <summary>The Host stores normalized backends ("Anthropic"); provider rows use choices ("anthropic").</summary>
    private static bool SameBackend(string choice, string? stored) =>
        stored is not null
        && string.Equals(ProviderAliases.Normalize(choice) ?? choice, ProviderAliases.Normalize(stored) ?? stored,
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>One advisor provider choice; the empty choice means no advisor.</summary>
public sealed record AdvisorProviderOption(string Choice, string DisplayName)
{
    public static AdvisorProviderOption Off { get; } = new(string.Empty, "Off");

    public bool IsOff => Choice.Length == 0;

    public override string ToString() => DisplayName;
}
