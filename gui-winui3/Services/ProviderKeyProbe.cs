using RuAgent.Client;

namespace RuApp.Services;

internal sealed record ProviderKeyProbeResult(string Code, string? ErrorCode = null);

internal sealed record ProviderKeyProbeCompletion(
    ProviderCredentialTarget Target, ProviderKeyProbeResult Result, bool RetryRequested);

/// <summary>
/// Owns one settings editor's candidate-check lifecycle. Call on the editor's
/// synchronization context; completions resume there before checking ownership.
/// The Host remains responsible for atomic validation and credential storage.
/// </summary>
internal sealed class ProviderKeyProbe : IDisposable
{
    private long _generation;
    private CancellationTokenSource? _cancellation;
    private bool _disposed;

    internal bool IsRunning => _cancellation is not null;

    /// <summary>Call for a key edit, provider change or backend replacement.</summary>
    internal void Invalidate()
    {
        _generation++;
        _cancellation?.Cancel();
    }

    internal async Task<ProviderKeyProbeCompletion?> RunAsync(
        string choice,
        string key,
        Func<string, string, CancellationToken, Task<ProviderKeyProbeResult>> validateAndSave)
    {
        if (_disposed || IsRunning || string.IsNullOrWhiteSpace(key)) return null;
        // This is the same canonical credential target used by the terminal.
        var target = ProviderCredential.Resolve(choice);
        if (target is null || !target.IsModelsDev) return null;

        long generation = _generation;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        ProviderKeyProbeResult result;
        try
        {
            result = await validateAndSave(target.Choice, key, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            result = new("superseded");
        }
        finally
        {
            _cancellation = null;
        }

        bool superseded = _disposed || generation != _generation;
        return new(target, superseded ? new("superseded") : result,
            RetryRequested: !_disposed && generation != _generation);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Invalidate();
    }
}
