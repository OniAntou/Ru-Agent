namespace RuAgent.Cli;

/// <summary>
/// One-slot type-ahead queue: a prompt typed while a run is streaming is sent when that run
/// finishes. The slot never replaces its occupant, so a stray Enter cannot silently discard a
/// queued instruction.
/// </summary>
public sealed class PendingPromptSlot
{
    // Written by the type-ahead key loop, read by the turn loop.
    private volatile string? _pending;

    public bool HasValue => _pending is not null;

    public string? Value => _pending;

    /// <summary>Queues the first prompt; returns false while a prompt is already waiting.</summary>
    public bool TryQueue(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || _pending is not null)
        {
            return false;
        }

        _pending = prompt;
        return true;
    }

    /// <summary>Takes the queued prompt and clears the slot.</summary>
    public string? Take()
    {
        var pending = _pending;
        _pending = null;
        return pending;
    }

    public void Clear() => _pending = null;
}
