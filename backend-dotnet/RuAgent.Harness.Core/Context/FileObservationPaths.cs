namespace RuAgent.Harness.Core.Context;

/// <summary>Bounded discovery hints from paired successful file receipts, independent of model-history compaction.</summary>
public static class FileObservationPaths
{
    public const int MaximumPaths = 128;

    public static IReadOnlyList<string> Read(IEnumerable<ModelContextEntry> entries,
        IReadOnlyList<string>? previousPaths = null)
    {
        var pending = new Dictionary<string, ModelToolCall>(StringComparer.Ordinal);
        var paths = new LinkedList<string>();
        var known = new Dictionary<string, LinkedListNode<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var hint in Copy(previousPaths)) known[hint] = paths.AddLast(hint);
        foreach (var entry in entries)
        {
            if (entry.AssistantStep is { } step)
            {
                pending.Clear();
                foreach (var call in step.ToolCalls) pending[call.ProviderCallId] = call;
            }
            else if (entry.ToolReceipt is { } receipt && pending.Remove(receipt.ProviderCallId!, out var call)
                && receipt.Outcome == ToolExecutionOutcome.Succeeded
                && call.ToolId is "read" or "read_file" or "write_file" or "edit_file" or "write" or "edit" or "files.read" or "files.write" or "files.edit"
                && call.Arguments.ValueKind == System.Text.Json.JsonValueKind.Object
                && call.Arguments.TryGetProperty("path", out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String
                && value.GetString() is { Length: > 0 and <= 4096 } path)
            {
                string? directory;
                try { directory = Path.GetDirectoryName(path); }
                catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                if (string.IsNullOrEmpty(directory)) continue;
                var hint = Path.Combine(directory, ".ru-instruction-scope");
                if (known.Remove(hint, out var previous)) paths.Remove(previous);
                known[hint] = paths.AddLast(hint);
                if (paths.Count > MaximumPaths)
                {
                    known.Remove(paths.First!.Value);
                    paths.RemoveFirst();
                }
            }
        }
        return paths.ToArray();
    }

    internal static IReadOnlyList<string> Copy(IReadOnlyList<string>? paths) => Array.AsReadOnly(
        (paths ?? []).Where(path => path is { Length: > 0 and <= 4096 }).Reverse()
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumPaths).Reverse().ToArray());
}
