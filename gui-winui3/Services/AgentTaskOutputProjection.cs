using System.Text.Json;

namespace RuApp.Services;

/// <summary>
/// One bounded page of a background task's retained output, as the Host's
/// <c>/tasks read</c> command reports it. The Host owns the window; this record
/// only describes what arrived and where the neighbouring pages start.
/// </summary>
public sealed record AgentTaskOutput(
    string ExecutionId,
    string State,
    int? ExitCode,
    int Offset,
    int Limit,
    int TotalChars,
    string Page,
    bool HasBefore,
    bool HasMore)
{
    public string Header => $"Task {ExecutionId} · {State}"
        + (ExitCode is { } code ? $" · exit {code}" : string.Empty)
        + $" · chars {Offset}–{Offset + Page.Length} of {TotalChars}";

    /// <summary>Start offset for the page after this one; meaningful while <see cref="HasMore"/>.</summary>
    public int NextOffset => Offset + Page.Length;
}

/// <summary>Reads the typed task-output payload without requiring a newer Host.</summary>
public static class AgentTaskOutputProjection
{
    public static AgentTaskOutput? Project(object? data)
    {
        if (data is not JsonElement { ValueKind: JsonValueKind.Object } element
            || ReadString(element, "execution_id") is not { Length: > 0 } id
            || ReadString(element, "page") is not { } page)
        {
            return null;
        }

        var offset = ReadInt(element, "offset") ?? 0;
        var total = ReadInt(element, "total_chars") ?? offset + page.Length;
        var state = ReadString(element, "state") ?? "unknown";
        if (state is not ("running" or "exited" or "killed" or "expired" or "failed"))
        {
            state = "unknown";
        }

        return new AgentTaskOutput(
            id,
            state,
            ReadInt(element, "exit_code"),
            Math.Clamp(offset, 0, Math.Max(total, page.Length)),
            ReadInt(element, "limit") ?? page.Length,
            Math.Max(total, page.Length),
            page,
            ReadTrue(element, "has_before"),
            ReadTrue(element, "truncated"));
    }

    private static string? ReadString(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadInt(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool ReadTrue(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
}
