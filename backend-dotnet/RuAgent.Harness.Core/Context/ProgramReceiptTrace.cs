using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RuAgent.Protocol.Programs;

namespace RuAgent.Harness.Core.Context;

public sealed record ProgramReceiptEntry(ModelToolCall Call, JsonElement Output, ToolExecutionOutcome Outcome,
    string Code, bool SideEffectsPossible);

/// <summary>Reads authority-owned trace fields, never fields inside the script's selected result.</summary>
public static class ProgramReceiptTrace
{
    public static bool TryRead(string? content, string providerCallId, out IReadOnlyList<ProgramReceiptEntry> entries)
    {
        entries = [];
        if (content is null || content.Length > 4_000_000) return false;
        try
        {
            using var json = JsonDocument.Parse(content);
            var root = json.RootElement;
            if (root.GetProperty("program_revision").GetString() != ProgramToolContract.Revision
                || root.GetProperty("effects_unknown").GetBoolean()) return false;
            var trace = root.GetProperty("nested_receipts");
            if (trace.ValueKind != JsonValueKind.Array || trace.GetArrayLength() > 32
                || root.GetProperty("tool_count").GetInt32() != trace.GetArrayLength()) return false;
            List<ProgramReceiptEntry> result = [];
            var expectedCallIndex = 0;
            foreach (var entry in trace.EnumerateArray())
            {
                if (!entry.TryGetProperty("call_index", out var callIndex)
                    || callIndex.ValueKind != JsonValueKind.Number || !callIndex.TryGetInt32(out var index)
                    || index != expectedCallIndex) return false;
                var tool = entry.GetProperty("tool_id").GetString()!;
                if (!ProgramToolContract.SupportedTools.Contains(tool)) return false;
                var response = entry.GetProperty("response");
                if (response.TryGetProperty("effects_unknown", out var unknown) && unknown.GetBoolean()) return false;
                var outcome = response.GetProperty("status").GetString() switch
                {
                    "succeeded" => ToolExecutionOutcome.Succeeded,
                    "failed" => ToolExecutionOutcome.Failed,
                    "denied" => ToolExecutionOutcome.Failed,
                    "cancelled" => ToolExecutionOutcome.Cancelled,
                    _ => ToolExecutionOutcome.OutcomeUnknown,
                };
                if (outcome == ToolExecutionOutcome.OutcomeUnknown) return false;
                var id = "program_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(providerCallId + "|" + index))).ToLowerInvariant();
                var call = new ModelToolCall(id, tool, "1.0.0", entry.GetProperty("arguments"));
                var code = response.TryGetProperty("error_code", out var error) && error.ValueKind == JsonValueKind.String
                    ? error.GetString()! : "ok";
                var effect = ProgramToolContract.CanHaveEffects(tool)
                    && response.GetProperty("adapter_invoked").GetBoolean();
                result.Add(new(call, response.GetProperty("output").Clone(), outcome, code, effect));
                expectedCallIndex++;
            }
            entries = result;
            return true;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException)
        { return false; }
    }
}
