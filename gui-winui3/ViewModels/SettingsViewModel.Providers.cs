using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.ViewModels;

/// <summary>The provider overview and its inline editor; credentials keep their existing owner.</summary>
public sealed partial class SettingsViewModel
{
    public ObservableCollection<ProviderRow> CatalogProviderRows { get; } = new();
    public ObservableCollection<ConfiguredProviderRow> ConfiguredProviders { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProviderOverviewVisible))]
    public partial bool IsProviderEditorOpen { get; private set; }

    public bool IsProviderOverviewVisible => !IsProviderEditorOpen;

    [ObservableProperty] public partial bool HasNoConfiguredProviders { get; private set; } = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProviderManagementEnabled))]
    public partial bool IsProviderNavigationEnabled { get; private set; } = true;
    [ObservableProperty] public partial bool IsCustomSignOutVisible { get; private set; }
    [ObservableProperty] public partial bool IsLegacyProviderVisible { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProviderOverviewMessage))]
    public partial string ProviderOverviewMessage { get; private set; } = string.Empty;

    public bool IsProviderManagementEnabled => !IsLoading && IsProviderNavigationEnabled;
    public bool HasProviderOverviewMessage => ProviderOverviewMessage.Length > 0;

    partial void OnIsLoadingChanged(bool value) => RefreshProviderManagement();
    partial void OnProviderOverviewMessageChanged(string value) => RefreshProviderManagement();

    [RelayCommand]
    private Task ReloadProvidersAsync() => LoadAsync();

    [RelayCommand]
    private void AddCustomProvider() => OpenProviderEditor("custom");

    [RelayCommand]
    private void EditProvider(string? choice) => OpenProviderEditor(choice);

    private void OpenProviderEditor(string? choice)
    {
        if (IsLoading || !IsProviderNavigationEnabled || string.IsNullOrWhiteSpace(choice)) return;
        var row = ProviderRows.FirstOrDefault(row => row.Value == choice)
            ?? (ConfiguredProviders.Any(row => row.Choice == choice)
                ? new ProviderRow(choice, ProviderSetupGuidance.DisplayName(choice)) : null);
        if (row is null) return;
        SelectProvider(row);
    }

    [RelayCommand]
    private async Task ReturnToProvidersAsync()
    {
        if (IsLoading) return;
        IsProviderNavigationEnabled = false;
        try
        {
            CancelDelay(ref _autoSaveDelay);
            CancelDelay(ref _keyProbeDelay);
            // Don't discard a pending key or let it follow the next editor selection.
            if (!string.IsNullOrWhiteSpace(ModelsDevApiKey))
            {
                await ProbeKeyAsync();
                if (!string.IsNullOrWhiteSpace(ModelsDevApiKey)) return;
            }
            if (!await SaveSettingsAsync())
            {
                ShowProviderTest(new ProviderConnectionTest(ProviderConnectionTest.ErrorTone,
                    "Could not save changes. Try again before leaving this provider."));
                return;
            }
            await LoadAsync();
            IsProviderEditorOpen = false;
        }
        finally
        {
            IsProviderNavigationEnabled = true;
        }
    }

    private void RefreshProviderManagement()
    {
        IsCustomSignOutVisible = _lastSettings?.HasCustomKey == true;
        IsLegacyProviderVisible = ProviderChoice is "openai" or "anthropic";
        var rows = _modelsDevKeyIds.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => new ConfiguredProviderRow(ProviderSetupGuidance.ModelsDevPrefix + id,
                _modelsDevNames.TryGetValue(id, out var name) ? name : id, false, "Key saved"))
            .ToList();
        if (_lastSettings?.HasCustomKey == true ||
            string.Equals(_lastSettings?.BackendChoice, "custom", StringComparison.OrdinalIgnoreCase))
        {
            rows.Add(new("custom", "Custom OpenAI Server", true,
                _lastSettings?.HasCustomKey == true ? "Key saved" : "Endpoint configured"));
        }
        if (_lastSettings?.HasOpenAiKey == true) rows.Add(new("openai", "OpenAI (legacy)", false, "Key saved"));
        if (_lastSettings?.HasAnthropicKey == true) rows.Add(new("anthropic", "Anthropic (legacy)", false, "Key saved"));
        ConfiguredProviders.Clear();
        foreach (var row in rows.OrderBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase))
            ConfiguredProviders.Add(row);
        HasNoConfiguredProviders = ConfiguredProviders.Count == 0 && !IsLoading && !HasProviderOverviewMessage;
    }
}

public sealed record ConfiguredProviderRow(string Choice, string DisplayName, bool IsCustom, string Status)
{
    public string EditAutomationName => $"Edit {DisplayName}";
}
