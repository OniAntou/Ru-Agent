using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Protocol.Programs;

namespace RuAgent.Broker.Programs;

public sealed class ProgramRunner(IProgramJournal journal)
{
    public const string Revision = ProgramToolContract.Revision;

    public async Task<JsonObject> RunAsync(string runId, string invocation, string binding, string code,
        Func<ProgramToolRequest, string, CancellationToken, Task<JsonObject>> invoke, CancellationToken ct)
    {
        var id = Hash(runId + "|" + invocation);
        var prefix = "program_" + id;
        var evidence = new JsonArray();
        var uncertain = false;
        JsonObject? blockedCall = null;
        var header = new JsonObject { ["revision"] = Revision, ["binding"] = binding, ["code_hash"] = Hash(code) };
        var opened = await journal.CreateAsync(runId, prefix + "_header", header, ct).ConfigureAwait(false);
        if (!JsonNode.DeepEquals(header, opened.Value)) return Result("program_replay_mismatch");
        if (await journal.ReadAsync(runId, prefix + "_final", ct).ConfigureAwait(false) is { } finished) return finished;
        List<ProgramToolReceipt> replay = [];
        var totalBytes = 0;
        var evaluator = new ProgramEvaluator();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var evaluation = evaluator.Evaluate(code, replay, ct);
            if (evaluation.ErrorCode is { } error) return await FinishAsync(error, null).ConfigureAwait(false);
            if (evaluation.Pending is not { } call) return await FinishAsync(null, evaluation.Result).ConfigureAwait(false);
            var key = prefix + "_" + replay.Count;
            var intent = new JsonObject { ["tool_id"] = call.ToolId, ["arguments"] = call.Arguments.DeepClone() };
            var receipt = await journal.ReadAsync(runId, key + "_receipt", ct).ConfigureAwait(false);
            if (receipt is not null)
            {
                if (receipt["intent_hash"]?.GetValue<string>() != Hash(intent.ToJsonString()))
                    return await FinishAsync("program_replay_mismatch", null).ConfigureAwait(false);
            }
            else
            {
                if (!await journal.ReserveToolAsync(runId, key, ct).ConfigureAwait(false))
                    return await FinishAsync("program_budget_exhausted", null).ConfigureAwait(false);
                // Only the writer of this exact intent can dispatch. An existing
                // request with no receipt is an uncertain effect, including after crash.
                var written = await journal.CreateAsync(runId, key + "_request", intent, ct).ConfigureAwait(false);
                if (!written.Created) return await FinishAsync("program_execution_unknown", null).ConfigureAwait(false);
                var response = await invoke(call, key, ct).ConfigureAwait(false);
                receipt = (await journal.CreateAsync(runId, key + "_receipt", new JsonObject
                {
                    ["intent_hash"] = Hash(intent.ToJsonString()), ["tool_id"] = call.ToolId,
                    ["arguments"] = call.Arguments.DeepClone(), ["response"] = response,
                }, ct).ConfigureAwait(false)).Value;
            }
            // First execution and replay both consume the stored, redacted JSON.
            var storedResponse = (JsonObject)receipt["response"]!;
            var storedArgs = (JsonObject)receipt["arguments"]!;
            uncertain |= storedResponse["effects_unknown"]?.GetValue<bool>() == true;
            evidence.Add(receipt.DeepClone());
            totalBytes += Encoding.UTF8.GetByteCount(receipt.ToJsonString());
            if (totalBytes > 2 * 1024 * 1024) return await FinishAsync("program_trace_limit", null).ConfigureAwait(false);
            if (storedResponse["program_stop"]?.GetValue<bool>() == true)
            {
                blockedCall = new JsonObject { ["tool_id"] = call.ToolId, ["arguments"] = storedArgs.DeepClone() };
                if (Encoding.UTF8.GetByteCount(blockedCall.ToJsonString()) > 16 * 1024)
                    blockedCall = new JsonObject { ["tool_id"] = call.ToolId, ["arguments_omitted"] = true };
                return await FinishAsync(storedResponse["program_error"]?.GetValue<string>()
                    ?? storedResponse["error_code"]?.GetValue<string>() ?? "program_tool_failed", null).ConfigureAwait(false);
            }
            replay.Add(new ProgramToolReceipt(call.ToolId, storedArgs, storedResponse));
        }

        JsonObject Result(string? error, JsonNode? value = null) => new()
        {
            ["program_revision"] = Revision, ["program_id"] = id, ["error"] = error,
            ["status"] = error is null ? "succeeded" : "failed", ["result"] = value?.DeepClone(),
            ["tool_count"] = evidence?.Count ?? 0, ["nested_receipts"] = evidence?.DeepClone() ?? new JsonArray(),
            ["blocked_call"] = blockedCall?.DeepClone(),
            ["effects_unknown"] = uncertain || error is "program_execution_unknown" or "program_replay_mismatch",
        };
        async Task<JsonObject> FinishAsync(string? error, JsonNode? value)
        {
            var result = Result(error, value);
            return (await journal.CreateAsync(runId, prefix + "_final", result, ct).ConfigureAwait(false)).Value;
        }
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
