using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.ViewModels;

/// <summary>One model row in the editor.</summary>
public sealed partial class CustomModelRow : ObservableObject
{
    [ObservableProperty] public partial string Id { get; set; } = string.Empty;

    /// <summary>Optional; empty leaves the Host's fallback window.</summary>
    [ObservableProperty] public partial string ContextWindow { get; set; } = string.Empty;
}

/// <summary>A model the server reported, offered for adding.</summary>
public sealed partial class DiscoveredModelRow : ObservableObject
{
    public DiscoveredModelRow(string id, int? contextLength)
    {
        Id = id;
        ContextLength = contextLength;
        Detail = contextLength is { } tokens ? $"{tokens:N0} tokens" : string.Empty;
    }

    public string Id { get; }
    public int? ContextLength { get; }
    public string Detail { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    public partial bool IsAdded { get; set; }

    public bool CanAdd => !IsAdded;
}

/// <summary>
/// The "Custom model API" editor (ADR 0034): one named provider's ID, name, base
/// URL, protocol, key and models. Saving replaces the whole stored list, so the
/// editor always starts from the Host's current list.
/// </summary>
public sealed partial class CustomProviderEditorViewModel : ObservableObject
{
    private readonly ICustomProviderBackend _backend;
    private List<CustomProviderDto> _stored = new();
    private string? _editingId;

    internal CustomProviderEditorViewModel(ICustomProviderBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        Models.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoModels));
        Discovered.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDiscovered));
    }

    /// <summary>Raised when the editor is done; true when something was saved or deleted.</summary>
    public event EventHandler<bool>? Closed;

    public ObservableCollection<CustomModelRow> Models { get; } = new();
    public ObservableCollection<DiscoveredModelRow> Discovered { get; } = new();

    public IReadOnlyList<string> ProtocolLabels { get; } =
    [
        CustomProviderPolicy.ProtocolLabel(CustomProviderPolicy.ChatCompletions),
        CustomProviderPolicy.ProtocolLabel(CustomProviderPolicy.AnthropicMessages),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(IsExisting))]
    public partial bool IsNew { get; private set; } = true;

    public bool IsExisting => !IsNew;
    public string Title => IsNew ? "Custom model API" : DisplayName.Trim().Length > 0 ? DisplayName.Trim() : ProviderId;
    public bool HasNoModels => Models.Count == 0;
    public bool HasDiscovered => Discovered.Count > 0;
    public bool HasStatus => StatusMessage.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string ProviderId { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInsecureKeyWarningVisible))]
    public partial string BaseUrl { get; set; } = string.Empty;

    [ObservableProperty] public partial int ProtocolIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInsecureKeyWarningVisible))]
    public partial string ApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyPlaceholder), nameof(IsInsecureKeyWarningVisible))]
    public partial bool HasSavedKey { get; private set; }

    public string KeyPlaceholder => HasSavedKey ? "Saved (leave blank to keep it)" : "Enter your API key (optional for local servers)";

    /// <summary>A key would be refused for this URL; say so before the user saves.</summary>
    public bool IsInsecureKeyWarningVisible =>
        (ApiKey.Length > 0 || HasSavedKey)
        && CustomProviderPolicy.NormalizeBaseUrl(BaseUrl) is { } url
        && !CustomProviderPolicy.AllowsKey(url);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string StatusMessage { get; private set; } = string.Empty;

    [ObservableProperty] public partial string StatusTone { get; private set; } = ProviderConnectionTest.NeutralTone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    private string Protocol => ProtocolIndex == 1 ? CustomProviderPolicy.AnthropicMessages : CustomProviderPolicy.ChatCompletions;

    /// <summary>Opens the editor for a new provider, or for <paramref name="id"/>.</summary>
    internal void Open(AppSettingsDto? settings, string? id)
    {
        _stored = settings?.CustomProviders?.Select(Clone).ToList() ?? new();
        var existing = id is null ? null : _stored.FirstOrDefault(provider => provider.Id == id);
        _editingId = existing?.Id;
        IsNew = existing is null;
        ProviderId = existing?.Id ?? string.Empty;
        DisplayName = existing?.DisplayName ?? string.Empty;
        BaseUrl = existing?.BaseUrl ?? string.Empty;
        ProtocolIndex = existing?.Protocol == CustomProviderPolicy.AnthropicMessages ? 1 : 0;
        ApiKey = string.Empty;
        HasSavedKey = existing is not null && settings?.CustomProviderKeys?.Contains(existing.Id) == true;
        Models.Clear();
        foreach (var model in existing?.Models ?? new())
            Models.Add(new CustomModelRow { Id = model.Id, ContextWindow = model.ContextWindow?.ToString() ?? string.Empty });
        Discovered.Clear();
        SetStatus(string.Empty, ProviderConnectionTest.NeutralTone);
    }

    [RelayCommand]
    private void AddModel() => Models.Add(new CustomModelRow());

    [RelayCommand]
    private void RemoveModel(CustomModelRow? row)
    {
        if (row is null) return;
        Models.Remove(row);
        foreach (var discovered in Discovered.Where(item => item.Id == row.Id)) discovered.IsAdded = false;
    }

    [RelayCommand]
    private void AddDiscovered(DiscoveredModelRow? row)
    {
        if (row is null || row.IsAdded) return;
        var existing = Models.FirstOrDefault(model => model.Id.Trim() == row.Id);
        if (existing is null)
        {
            Models.Add(new CustomModelRow { Id = row.Id, ContextWindow = row.ContextLength?.ToString() ?? string.Empty });
        }
        else if (existing.ContextWindow.Length == 0 && row.ContextLength is { } tokens)
        {
            existing.ContextWindow = tokens.ToString();
        }

        row.IsAdded = true;
    }

    [RelayCommand]
    private void AddAllDiscovered()
    {
        foreach (var row in Discovered.ToArray()) AddDiscovered(row);
    }

    /// <summary>
    /// Saves the draft (the Host only asks a saved provider), then lists what the
    /// server serves. A failure here is the connection test's answer.
    /// </summary>
    [RelayCommand]
    private async Task FetchModelsAsync()
    {
        if (IsBusy) return;
        if (!await SaveCoreAsync(closeOnSuccess: false)) return;
        IsBusy = true;
        try
        {
            SetStatus("Asking the server for its models…", ProviderConnectionTest.NeutralTone);
            var snapshot = await _backend.DiscoverAsync(CustomProviderPolicy.Backend(_editingId!), CancellationToken.None);
            Discovered.Clear();
            if (snapshot is null || !snapshot.IsSuccess)
            {
                SetStatus(CustomProviderPolicy.FailureMessage(snapshot?.ErrorCode ?? "catalog_unavailable"), ProviderConnectionTest.ErrorTone);
                return;
            }

            var listed = Models.Select(model => model.Id.Trim()).ToHashSet(StringComparer.Ordinal);
            foreach (var model in snapshot.Catalog)
                Discovered.Add(new DiscoveredModelRow(model.Id, model.ContextLength) { IsAdded = listed.Contains(model.Id) });
            SetStatus(snapshot.Catalog.Count == 0
                    ? "Connected. The server lists no models; add model IDs by hand."
                    : $"Connected. The server lists {snapshot.Catalog.Count} model{(snapshot.Catalog.Count == 1 ? "" : "s")}; add the ones you want.",
                ProviderConnectionTest.SuccessTone);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy) return;
        await SaveCoreAsync(closeOnSuccess: true);
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, _savedOnce);

    [RelayCommand]
    private async Task RemoveKeyAsync()
    {
        if (IsBusy || _editingId is null) return;
        IsBusy = true;
        try
        {
            if (await _backend.RemoveKeyAsync(CustomProviderPolicy.Backend(_editingId)))
            {
                HasSavedKey = false;
                _savedOnce = true;
                SetStatus("Key removed.", ProviderConnectionTest.SuccessTone);
            }
            else
            {
                SetStatus("Could not remove the key. Try again.", ProviderConnectionTest.ErrorTone);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (IsBusy || _editingId is null) return;
        IsBusy = true;
        try
        {
            // The key goes first: a provider deleted while its key stayed would leave an
            // orphaned secret nobody can see or remove.
            if (HasSavedKey && !await _backend.RemoveKeyAsync(CustomProviderPolicy.Backend(_editingId)))
            {
                SetStatus("Could not remove the provider's key, so the provider was kept. Try again.", ProviderConnectionTest.ErrorTone);
                return;
            }

            var remaining = _stored.Where(provider => provider.Id != _editingId).ToList();
            var result = await _backend.SaveAsync(remaining, null, null);
            if (!result.Success)
            {
                SetStatus(CustomProviderPolicy.FailureMessage(result.Code), ProviderConnectionTest.ErrorTone);
                return;
            }

            _stored = remaining;
            Closed?.Invoke(this, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool _savedOnce;

    private async Task<bool> SaveCoreAsync(bool closeOnSuccess)
    {
        var draft = new CustomProviderDraft(ProviderId, DisplayName, BaseUrl, Protocol,
            Models.Select(model => new CustomProviderModelDraft(model.Id, model.ContextWindow)).ToArray());
        var otherIds = _stored.Where(provider => provider.Id != _editingId).Select(provider => provider.Id);
        if (CustomProviderPolicy.Validate(draft, otherIds) is { } problem)
        {
            SetStatus(problem, ProviderConnectionTest.ErrorTone);
            return false;
        }

        if (IsNew && _stored.Count >= CustomProviderPolicy.MaximumProviders)
        {
            SetStatus($"At most {CustomProviderPolicy.MaximumProviders} custom providers can be added.", ProviderConnectionTest.ErrorTone);
            return false;
        }

        IsBusy = true;
        try
        {
            var dto = CustomProviderPolicy.ToDto(draft);
            var list = _stored.Where(provider => provider.Id != _editingId).Select(Clone).ToList();
            var index = _editingId is null ? -1 : _stored.FindIndex(provider => provider.Id == _editingId);
            if (index < 0) list.Add(dto);
            else list.Insert(Math.Min(index, list.Count), dto);
            var key = ApiKey.Length > 0 ? ApiKey : null;
            var result = await _backend.SaveAsync(list, key is null ? null : dto.Id, key);
            if (!result.Success)
            {
                SetStatus(CustomProviderPolicy.FailureMessage(result.Code), ProviderConnectionTest.ErrorTone);
                return false;
            }

            _stored = list;
            _editingId = dto.Id;
            _savedOnce = true;
            IsNew = false;
            ProviderId = dto.Id;
            BaseUrl = dto.BaseUrl;
            if (key is not null)
            {
                ApiKey = string.Empty;
                HasSavedKey = true;
            }

            SetStatus("Saved.", ProviderConnectionTest.SuccessTone);
            if (closeOnSuccess) Closed?.Invoke(this, true);
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetStatus(string message, string tone)
    {
        StatusMessage = message;
        StatusTone = tone;
    }

    private static CustomProviderDto Clone(CustomProviderDto provider) => new()
    {
        Id = provider.Id,
        DisplayName = provider.DisplayName,
        BaseUrl = provider.BaseUrl,
        Protocol = provider.Protocol,
        Models = provider.Models.Select(model => new CustomProviderModelDto { Id = model.Id, ContextWindow = model.ContextWindow }).ToList(),
    };
}
