using RuApp.Models;

namespace RuApp.Services;

/// <summary>
/// Owns selected provider/model state and persists a choice before publishing it.
/// Catalog refreshes and control rendering remain with ChatView.
/// </summary>
internal sealed class ChatModelSelectionPresenter
{
    private readonly ModelSelectionSaveQueue _saves = new();

    internal string Backend { get; private set; } = string.Empty;
    internal string Model { get; private set; } = string.Empty;

    internal void Adopt(string? backend, string? model)
    {
        Backend = backend ?? string.Empty;
        Model = model ?? string.Empty;
    }

    internal void Adopt(AppSettingsDto settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var backend = settings.BackendChoice ?? string.Empty;
        Adopt(backend, ProviderModelSelection.GetSelectedModel(settings, backend));
    }

    internal async Task<ModelSelectionSaveResult> SelectAsync(
        string model,
        string backend,
        Func<string, string, Task<bool>> save)
    {
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(backend))
            return ModelSelectionSaveResult.Failed;

        var result = await _saves.SaveAsync(() => save(backend, model)).ConfigureAwait(true);
        if (result == ModelSelectionSaveResult.Saved) Adopt(backend, model);
        return result;
    }

    internal void Invalidate() => _saves.Invalidate();
}
