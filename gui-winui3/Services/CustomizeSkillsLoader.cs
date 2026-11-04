using RuAgent.Client;

namespace RuApp.Services;

internal sealed record CustomizeSkillsState(string Workspace, IReadOnlyList<SkillCatalogItem> Items,
    bool IsLoading, string? Status = null, string? Details = null);

/// <summary>Owns the visible page's skill read, independently of XAML load timing.</summary>
internal sealed class CustomizeSkillsLoader(Action<Action> dispatch) : IDisposable
{
    private Func<string, string?, bool, CancellationToken, Task<SkillCatalogResponse>>? _read;
    private Func<string?> _workspace = () => null;
    private CancellationTokenSource? _work;
    private long _generation;
    private bool _active;
    private string _query = string.Empty;

    public CustomizeSkillsState State { get; private set; } = new(string.Empty, [], false);
    public event EventHandler<CustomizeSkillsState>? StateChanged;

    public void Attach(Func<string, string?, bool, CancellationToken, Task<SkillCatalogResponse>> read, Func<string?> workspace)
    {
        Cancel();
        _read = read;
        _workspace = workspace;
        if (_active) _ = LoadAsync(_query);
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (active) _ = LoadAsync(_query);
        else
        {
            Cancel();
            State = State with { IsLoading = false };
        }
    }

    public Task LoadAsync(string? query = null, bool refresh = false)
    {
        _query = query ?? string.Empty;
        if (!_active || _read is not { } read) return Task.CompletedTask;
        var workspace = _workspace() ?? string.Empty;
        Cancel();
        var generation = _generation;
        var items = State.Workspace == workspace ? State.Items : Array.Empty<SkillCatalogItem>();
        _work = new CancellationTokenSource();
        var token = _work.Token;
        Publish(generation, new(workspace, items, true, items.Count > 0 ? "Refreshing skills…" : "Loading skills…"));
        var requestedQuery = _query;
        return ReadAsync();

        async Task ReadAsync()
        {
            try
            {
                var result = await read(workspace, requestedQuery, refresh, token).ConfigureAwait(false);
                var parts = new List<string>();
                if (result.Truncated) parts.Add($"Showing {result.Items.Count} of {result.Total} skills.");
                if (result.Diagnostics.Count > 0) parts.Add(result.Diagnostics.Count == 1
                    ? "1 note while reading skill files." : $"{result.Diagnostics.Count} notes while reading skill files.");
                Publish(generation, new(workspace, result.Items, false, parts.Count == 0 ? null : string.Join(" ", parts),
                    result.Diagnostics.Count == 0 ? null : string.Join(Environment.NewLine,
                        result.Diagnostics.Take(40).Select(item => $"{item.Code}: {item.SourcePath}"))));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error)
            {
                Publish(generation, new(workspace, items, false, $"Could not load skills: {error.Message}"));
            }
        }
    }

    public void RefreshWorkspace()
    {
        if (_active && State.Workspace != (_workspace() ?? string.Empty)) _ = LoadAsync(_query);
    }

    private void Publish(long generation, CustomizeSkillsState state) => dispatch(() =>
    {
        if (!_active || generation != _generation) return;
        State = state;
        StateChanged?.Invoke(this, state);
    });

    private void Cancel()
    {
        _generation++;
        _work?.Cancel();
        _work?.Dispose();
        _work = null;
    }

    public void Dispose() { _active = false; Cancel(); }
}
