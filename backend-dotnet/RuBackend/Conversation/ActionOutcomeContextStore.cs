using System.Collections.Concurrent;
using RuBackend.Worker;

namespace RuBackend.Conversation;

public interface IActionOutcomeContextStore
{
    void Put(string sessionId, WorkerActionOutcome outcome);

    WorkerActionOutcome? Take(string sessionId);

    void Remove(string sessionId);
}

public sealed class ActionOutcomeContextStore : IActionOutcomeContextStore
{
    private readonly ConcurrentDictionary<string, WorkerActionOutcome> _outcomes =
        new(StringComparer.Ordinal);

    public void Put(string sessionId, WorkerActionOutcome outcome)
    {
        ValidateSessionId(sessionId);
        ArgumentNullException.ThrowIfNull(outcome);
        if (!outcome.ShouldRetain)
        {
            _outcomes.TryRemove(sessionId, out _);
            return;
        }

        _outcomes[sessionId] = outcome;
    }

    public WorkerActionOutcome? Take(string sessionId)
    {
        ValidateSessionId(sessionId);
        return _outcomes.TryRemove(sessionId, out var outcome) ? outcome : null;
    }

    public void Remove(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _outcomes.TryRemove(sessionId, out _);
        }
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 128)
        {
            throw new ArgumentException("Session ID is invalid.", nameof(sessionId));
        }
    }
}
