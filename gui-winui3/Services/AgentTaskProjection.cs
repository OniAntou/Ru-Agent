using System.Text.Json;
using RuApp.Models;

namespace RuApp.Services;

public sealed record AgentTaskProjectionResult(IReadOnlyList<AgentTaskRow> Rows, string Status);

/// <summary>Reads optional process labels without requiring a newer Host.</summary>
public static class AgentTaskProjection
{
    public const int MaxRows = 16;

    public static AgentTaskProjectionResult Project(object? data)
    {
        var rows = new List<AgentTaskRow>();
        if (data is JsonElement { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty("tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
        {
            foreach (var task in tasks.EnumerateArray())
            {
                if (task.ValueKind != JsonValueKind.Object
                    || (task.TryGetProperty("background", out var background) && background.ValueKind == JsonValueKind.False)
                    || ReadString(task, "execution_id") is not { Length: > 0 } id) continue;
                string command = ReadString(task, "command") ?? string.Empty;
                if (command.Length > 512) command = command[..511] + "…";
                int? exit = task.TryGetProperty("exit_code", out var code) && code.ValueKind == JsonValueKind.Number
                    && code.TryGetInt32(out int value) ? value : null;
                rows.Add(new AgentTaskRow(id, ReadString(task, "state") ?? "unknown", exit, command));
            }
        }

        int running = rows.Count(row => row.State == "running");
        string status = rows.Count == 0 ? "No background tasks"
            : running + " running · " + (rows.Count - running) + " finished";
        if (rows.Count > MaxRows) status = $"Showing {MaxRows} of {rows.Count} · {running} running";
        return new(rows.OrderByDescending(row => row.State == "running").Take(MaxRows).ToArray(), status);
    }

    private static string? ReadString(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
