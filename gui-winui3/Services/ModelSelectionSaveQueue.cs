namespace RuApp.Services;

internal enum ModelSelectionSaveResult { Superseded, Saved, Failed }

/// <summary>Orders settings writes; only the latest user choice publishes a result.</summary>
internal sealed class ModelSelectionSaveQueue
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly UiRequestLifetime _requests = new();

    internal void Invalidate() => _requests.Replace();

    internal async Task<ModelSelectionSaveResult> SaveAsync(Func<Task<bool>> save)
    {
        var version = _requests.Replace();
        await _gate.WaitAsync();
        try
        {
            if (!_requests.Owns(version)) return ModelSelectionSaveResult.Superseded;
            bool saved;
            try { saved = await save(); }
            catch (Exception) { saved = false; }
            if (!_requests.Owns(version)) return ModelSelectionSaveResult.Superseded;
            return saved ? ModelSelectionSaveResult.Saved : ModelSelectionSaveResult.Failed;
        }
        finally { _gate.Release(); }
    }
}
