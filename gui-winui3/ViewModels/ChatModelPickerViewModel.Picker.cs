using RuApp.Models;
using RuApp.Services;

namespace RuApp.ViewModels;

public sealed partial class ChatModelPickerViewModel
{
    private void OnGroupedStateChanged(object? sender, GroupedProviderCatalogState state) => _dispatch(() =>
    {
        if (_disposed) return;
        _pendingGroupedState = state;
        // A fixed window bounds first-paint latency even while hundreds of providers publish.
        if (_publishDelay is not null) return;
        Restart(ref _publishDelay, UiTiming.GroupedPublishCoalesce, () =>
        {
            _publishDelay?.Dispose();
            _publishDelay = null;
            var pending = _pendingGroupedState;
            _pendingGroupedState = null;
            if (pending is not null && _grouped is not null && ReferenceEquals(_grouped.State, pending))
                ApplyGroupedState(pending);
            return Task.CompletedTask;
        });
    });

    internal void ApplyGroupedState(GroupedProviderCatalogState state)
    {
        // Catalog callbacks carry the current selection version without owning it.
        // Shell settings reloads advance that version via SetSelection; ordinary
        // provider publications and delayed snapshots cannot replace the choice.
        if (state.SelectionVersion > _appliedGroupedSelectionVersion)
        {
            _appliedGroupedSelectionVersion = state.SelectionVersion;
            Backend = state.SelectedBackend;
            Model = state.SelectedModel;
            _selection.Adopt(Backend, Model);
            _selectionRead = true;
            _selectionVersion++;
            ShowSelection(Backend, Model);
        }

        ShowSelection(Backend, Model);
        if (IsPickerOpen) PublishPicker(state);
        else _isPickerDirty = true;
        SelectionChanged?.Invoke();
        RefreshEffort();
    }

    private void PublishPicker(GroupedProviderCatalogState state)
    {
        var catalogs = new Dictionary<string, ProviderCatalogState>(state.Catalogs, StringComparer.Ordinal);
        var definitions = _grouped?.CurrentDefinitions ?? ProviderGroupDefinitions.All;
        if (_catalog?.State is { } selected && selected.Backend == EffectiveBackend && selected.Catalog.Count > 0
            && (!catalogs.TryGetValue(selected.Backend, out var existing)
                || !existing.IsSuccess && existing.Catalog.Count == 0 && existing.ErrorCode is null
                    && existing.LastSuccessUtc is null))
        {
            catalogs[selected.Backend] = selected;
            if (!definitions.Any(item => item.Alias == selected.Backend))
            {
                var name = CurrentSettings?.CustomProviders?
                    .FirstOrDefault(item => CustomProviderPolicy.Backend(item.Id) == selected.Backend)?.DisplayName
                    ?? (selected.Backend.StartsWith("models.dev:", StringComparison.Ordinal)
                        ? selected.Backend["models.dev:".Length..] : selected.Backend);
                definitions = [.. definitions, new(selected.Backend, selected.Backend, name, definitions.Count)];
            }
        }
        var projection = ProviderModelPickerProjection.Create(catalogs.Values, EffectiveBackend, EffectiveModel, state.SearchText, definitions);
        if (projection.Groups.Count == 0 && string.IsNullOrWhiteSpace(state.SearchText) && _catalogRefreshes > 0)
            projection = projection with { EmptyStateText = "Loading models…" };
        PickerProjectionReady?.Invoke(projection);
        _isPickerDirty = false;
    }

    private void OnCatalogStateChanged(object? sender, ProviderCatalogState state) => _dispatch(() =>
    {
        if (_disposed || state.Backend != EffectiveBackend) return;
        RefreshEffort();
        if (IsPickerOpen && _grouped is { } grouped) PublishPicker(grouped.State);
        else _isPickerDirty = true;
    });
}
