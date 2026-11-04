using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Subagents;

namespace RuBackend.Harness.Subagents;

/// <summary>
/// Summarizes what each child of a timed-out <c>agent_wait</c> is doing, from the same
/// durable activity replay a client reads. Without it a timed-out wait told the parent
/// nothing, so it could only wait again or probe the filesystem.
/// </summary>
/// <remarks>
/// Read-only and advisory: a child whose replay cannot be read contributes no entry, and
/// a failure never blocks the wait from completing. Text is bounded display data from the
/// child's own run, delivered under the wait output's untrusted source label.
/// </remarks>
public sealed class SubagentWaitProgressReader(IHarnessEventStore store, AgentActivitiesService activities,
    ILogger<SubagentWaitProgressReader>? logger = null)
{
    internal const int MaxRecentActivities = 5;
    internal const int MaxActivityCharacters = 160;
    internal const int MaxNarrationCharacters = 400;

    public async Task<IReadOnlyList<SubagentWaitProgress>> ReadAsync(
        IReadOnlyList<(string AgentId, RunId RunId)> attempts, CancellationToken ct)
    {
        var progress = new List<SubagentWaitProgress>(attempts.Count);
        foreach (var (agentId, runId) in attempts)
        {
            try
            {
                if (await ReadOneAsync(agentId, runId, ct).ConfigureAwait(false) is { } entry) progress.Add(entry);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                logger?.LogDebug("Subagent progress unavailable for {RunId}: {Kind}", runId.Value, error.GetType().Name);
            }
        }

        return progress.AsReadOnly();
    }

    private async Task<SubagentWaitProgress?> ReadOneAsync(string agentId, RunId runId, CancellationToken ct)
    {
        var run = await store.GetRunAsync(runId, ct).ConfigureAwait(false);
        if (run is null) return null;
        var replay = await activities.ListRunActivitiesAsync(runId.Value, ct).ConfigureAwait(false);
        var recent = new List<string>();
        string? narration = null;
        if (replay.StatusCode == 200)
        {
            var body = JsonSerializer.SerializeToElement(replay.Body);
            if (body.TryGetProperty("activities", out var rows) && rows.ValueKind == JsonValueKind.Array)
                recent = rows.EnumerateArray().Select(Describe).Where(text => text is not null)
                    .TakeLast(MaxRecentActivities).Select(text => text!).ToList();
            if (body.TryGetProperty("transcript", out var transcript) && transcript.ValueKind == JsonValueKind.Array)
                narration = transcript.EnumerateArray()
                    .Where(entry => Text(entry, "kind") == "text")
                    .Select(entry => Text(entry, "text"))
                    .LastOrDefault(text => !string.IsNullOrWhiteSpace(text)) is { } latest
                    ? Bound(latest.Trim(), MaxNarrationCharacters)
                    : null;
        }

        // How long the attempt has existed, so a parent can tell a slow child from a stuck one.
        var elapsed = (long)Math.Max(0, (DateTimeOffset.UtcNow - run.CreatedAt).TotalSeconds);
        return new SubagentWaitProgress(agentId, runId, run.State.ToString().ToLowerInvariant(),
            run.ModelStepsInWindow, run.ToolCallsInWindow, recent.AsReadOnly(), narration, elapsed);
    }

    /// <summary>"Terminal completed · dotnet test" — kind, state and the row's own target.</summary>
    private static string? Describe(JsonElement row)
    {
        var kind = Text(row, "kind");
        if (kind is null or "thinking" or "narration") return null;
        var parts = new List<string> { kind + " " + (Text(row, "state") ?? "unknown") };
        foreach (var name in new[] { "tool_display", "file_name", "detail" })
            if (Text(row, name) is { Length: > 0 } value && !parts.Contains(value)) parts.Add(value);
        if (row.TryGetProperty("exit_code", out var exit) && exit.ValueKind == JsonValueKind.Number) parts.Add("exit " + exit.GetInt32());
        return Bound(string.Join(" · ", parts).ReplaceLineEndings(" "), MaxActivityCharacters);
    }

    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Bound(string text, int maximum)
    {
        if (text.Length <= maximum) return text;
        var keep = maximum - 1;
        if (char.IsHighSurrogate(text[keep - 1])) keep--;
        return text[..keep] + "…";
    }
}
