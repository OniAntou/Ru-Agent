using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Context;

namespace RuBackend.Harness;

public sealed partial class AgentRuntimeLoopService
{
    // Observe only authority-produced file-tool receipts, never model-selected
    // paths or arbitrary MCP/terminal output with similarly named JSON fields.
    internal async Task ObserveFileToolResultAsync(string root, string tool, JsonObject output, CancellationToken ct)
    {
        if (_workspaceWatches is null) return;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(tool, output);
        foreach (var path in paths)
            await _workspaceWatches.ObserveKnownPathAsync(root, path, ct).ConfigureAwait(false);

        void Collect(string id, JsonObject result)
        {
            if (id is "write_file" or "edit_file" or "file_delete" or "file_restore") Add(result);
            else if (id == "apply_patch" && result["files"] is JsonArray files)
                foreach (var file in files.OfType<JsonObject>().Take(64)) Add(file);
            else if (id == "run_code" && ProgramReceiptTrace.TryRead(result.ToJsonString(), "observed-program", out var trace))
                foreach (var receipt in trace.Where(receipt => receipt.Outcome == RuAgent.Harness.Core.ToolExecutionOutcome.Succeeded))
                    if (JsonNode.Parse(receipt.Output.GetRawText()) is JsonObject nested) Collect(receipt.Call.ToolId, nested);
        }

        void Add(JsonObject result)
        {
            if (result["path"] is JsonValue value && value.TryGetValue<string>(out var path)
                && !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path)
                && !path.Replace('\\', '/').Split('/').Any(segment => segment is "." or "..")) paths.Add(path);
        }
    }
}
