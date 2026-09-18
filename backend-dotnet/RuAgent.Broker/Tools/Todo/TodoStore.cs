using System.Collections.Concurrent;

namespace RuAgent.Broker.Tools.Todo;

/// <summary>
/// The agent's task list, one per session, held in memory.
/// </summary>
/// <remarks>
/// Lost when the process exits. That is a deliberate weakening from the reference
/// implementation, which keeps the list on an event-sourced session log so it survives
/// a restart and replays; this Broker has no such log. Making it durable means writing
/// todo state as a harness event through the existing outbox and SQLite schema, which
/// is a larger change than the feature needs, and doing it later would not change the
/// model-visible contract.
///
/// Session-scoped rather than run-scoped because a plan spans turns: the agent that
/// opens a five-step task usually finishes it on a later message.
/// </remarks>
public sealed class TodoStore
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<TodoRow>> _bySession =
        new(StringComparer.Ordinal);

    public IReadOnlyList<TodoRow> Read(string sessionId) =>
        string.IsNullOrWhiteSpace(sessionId)
            ? Array.Empty<TodoRow>()
            : _bySession.TryGetValue(sessionId, out var rows) ? rows : Array.Empty<TodoRow>();

    public void Replace(string sessionId, IReadOnlyList<TodoRow> rows) =>
        _bySession[sessionId] = rows.ToArray();

    public void Remove(string sessionId) => _bySession.TryRemove(sessionId, out _);

    /// <summary>
    /// The one-line summary the model reads back after a write.
    /// </summary>
    /// <remarks>
    /// Counts rather than the list: the model already sent the list, so repeating it
    /// would spend context on what it just wrote. The counts are what it cannot derive
    /// — how much is left.
    /// </remarks>
    public static string Describe(IReadOnlyList<TodoRow> rows)
    {
        int pending = Count(rows, TodoListValidator.StatusPending);
        int active = Count(rows, TodoListValidator.StatusInProgress);
        int completed = Count(rows, TodoListValidator.StatusCompleted);
        return $"Updated todo list: {pending} pending, {active} in progress, {completed} completed.";
    }

    private static int Count(IReadOnlyList<TodoRow> rows, string status) =>
        rows.Count(row => string.Equals(row.Status, status, StringComparison.Ordinal));
}
