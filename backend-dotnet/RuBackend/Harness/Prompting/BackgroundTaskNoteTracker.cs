using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using RuAgent.Protocol;

namespace RuBackend.Harness.Prompting;

/// <summary>
/// Turns the Broker's session task list into one bounded, change-only note for
/// the model context: a task that appeared or changed state since the previous
/// model step is reported once, so a background command that finished while the
/// model was away does not have to be polled to be noticed. The tracker is
/// per-run; task ids the Broker no longer knows are forgotten.
/// </summary>
public sealed class BackgroundTaskNoteTracker
{
    /// <summary>Longest note the prompt reference may carry.</summary>
    public const int MaxNoteLength = 2_000;

    /// <summary>Task ids remembered for change detection.</summary>
    public const int MaxTrackedTasks = 64;

    /// <summary>Task entries reported in one note.</summary>
    public const int MaxNotesPerProbe = 8;

    private readonly Func<CancellationToken, Task<JsonObject>> _probe;
    private readonly ConcurrentDictionary<string, string> _lastStates = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public BackgroundTaskNoteTracker(Func<CancellationToken, Task<JsonObject>> probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    /// <summary>
    /// One note for the tasks whose state changed since the previous call, or
    /// null when nothing changed or the Broker could not be reached. A probe
    /// failure stays silent: the note is advisory and must not fail the prompt.
    /// </summary>
    public async Task<string?> ProbeAsync(CancellationToken cancellationToken)
    {
        JsonObject payload;
        try
        {
            payload = await _probe(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        if (!string.Equals(
                payload["status"]?.GetValue<string>(),
                ToolResultStatuses.Succeeded,
                StringComparison.Ordinal)
            || payload["tasks"] is not JsonArray tasks)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var notes = new List<string>();
        lock (_gate)
        {
            foreach (var task in tasks)
            {
                if (task is not JsonObject entry
                    || entry["execution_id"]?.GetValue<string>() is not { } executionId
                    || entry["state"]?.GetValue<string>() is not { } state)
                {
                    continue;
                }

                seen.Add(executionId);
                var changed = !_lastStates.TryGetValue(executionId, out var previous)
                    || !string.Equals(previous, state, StringComparison.Ordinal);
                _lastStates[executionId] = state;
                if (!changed)
                {
                    continue;
                }

                var exit = entry["exit_code"] is JsonValue exitValue && exitValue.TryGetValue<int>(out var code)
                    ? $", exit {code}"
                    : string.Empty;
                notes.Add(previous is null
                    ? $"{executionId}: {state}{exit}"
                    : $"{executionId}: {previous} -> {state}{exit}");
            }

            foreach (var tracked in _lastStates.Keys)
            {
                if (!seen.Contains(tracked))
                {
                    _lastStates.TryRemove(tracked, out _);
                }
            }

            while (_lastStates.Count > MaxTrackedTasks)
            {
                _lastStates.TryRemove(_lastStates.Keys.First(), out _);
            }
        }

        if (notes.Count == 0)
        {
            return null;
        }

        var recent = notes.Count <= MaxNotesPerProbe
            ? notes
            : notes.GetRange(notes.Count - MaxNotesPerProbe, MaxNotesPerProbe);
        var note = "Background terminal task state changed: "
            + string.Join("; ", recent)
            + ". Page live output with bash mode=read_output (execution_id) or stop a task with mode=kill.";
        return note.Length <= MaxNoteLength ? note : note[..MaxNoteLength];
    }
}
