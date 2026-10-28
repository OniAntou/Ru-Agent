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
            if (evaluation.PendingBatch is { } batch)
            {
                var execution = await new ProgramBatchExecutor(journal).ExecuteAsync(
                    runId, prefix, batch, invoke, ct).ConfigureAwait(false);
                foreach (var indexed in execution.Receipts)
                {
                    var batchReceipt = new JsonObject
                    {
                        ["call_index"] = indexed.Index,
                        ["intent_hash"] = Hash(new JsonObject
                        {
                            ["tool_id"] = indexed.Receipt.ToolId,
                            ["arguments"] = indexed.Receipt.Arguments.DeepClone(),
                        }.ToJsonString()),
                        ["tool_id"] = indexed.Receipt.ToolId,
                        ["arguments"] = indexed.Receipt.Arguments.DeepClone(),
                        ["response"] = indexed.Receipt.Response.DeepClone(),
                    };
                    if (!TryAppendEvidence(batchReceipt))
                        return await FinishAsync("program_trace_limit", null).ConfigureAwait(false);
                    replay.Add(indexed.Receipt);
                    if (indexed.Receipt.Response["program_stop"]?.GetValue<bool>() == true && blockedCall is null)
                    {
                        blockedCall = BlockedCall(indexed.Receipt.ToolId, indexed.Receipt.Arguments);
                    }
                }
                if (execution.ErrorCode is { } batchError)
                    return await FinishAsync(batchError, null).ConfigureAwait(false);
                if (blockedCall is not null)
                {
                    var blocked = execution.Receipts.Select(entry => entry.Receipt)
                        .First(receipt => receipt.Response["program_stop"]?.GetValue<bool>() == true);
                    return await FinishAsync(StopError(blocked.Response), null).ConfigureAwait(false);
                }
                continue;
            }
            if (evaluation.Pending is not { } call) return await FinishAsync(null, evaluation.Result).ConfigureAwait(false);
            var key = prefix + "_" + replay.Count;
            var intent = new JsonObject { ["tool_id"] = call.ToolId, ["arguments"] = call.Arguments.DeepClone() };
            var receipt = await journal.ReadAsync(runId, key + "_receipt", ct).ConfigureAwait(false);
            var storedIntent = await journal.ReadAsync(runId, key + "_intent", ct).ConfigureAwait(false);
            var storedRequest = await journal.ReadAsync(runId, key + "_request", ct).ConfigureAwait(false);
            if (storedIntent is not null && !JsonNode.DeepEquals(storedIntent, intent)
                || storedRequest is not null && !JsonNode.DeepEquals(storedRequest, intent)
                || storedRequest is not null && storedIntent is null)
                return await FinishAsync("program_replay_mismatch", null).ConfigureAwait(false);
            if (receipt is not null)
            {
                if (storedIntent is null || storedRequest is null || !ReceiptMatches(receipt, intent))
                    return await FinishAsync("program_replay_mismatch", null).ConfigureAwait(false);
            }
            else
            {
                if (storedRequest is not null)
                    return await FinishAsync("program_execution_unknown", null).ConfigureAwait(false);
                if (!await journal.ReserveToolAsync(runId, key, ct).ConfigureAwait(false))
                    return await FinishAsync("program_budget_exhausted", null).ConfigureAwait(false);
                // The intent records preparation. A crash here is safe to resume because
                // dispatch has not been marked and the callback has not started.
                var prepared = await journal.CreateAsync(runId, key + "_intent", intent, ct).ConfigureAwait(false);
                if (!JsonNode.DeepEquals(prepared.Value, intent))
                    return await FinishAsync("program_replay_mismatch", null).ConfigureAwait(false);
                // This insert is the dispatch boundary; an existing marker without a
                // receipt remains uncertain and is never automatically repeated.
                var written = await journal.CreateAsync(runId, key + "_request", intent, ct).ConfigureAwait(false);
                if (!written.Created)
                {
                    var dispatchError = JsonNode.DeepEquals(written.Value, intent) ? "program_execution_unknown" : "program_replay_mismatch";
                    return await FinishAsync(dispatchError, null).ConfigureAwait(false);
                }
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
            var indexedReceipt = (JsonObject)receipt.DeepClone();
            indexedReceipt["call_index"] = replay.Count;
            if (!TryAppendEvidence(indexedReceipt)) return await FinishAsync("program_trace_limit", null).ConfigureAwait(false);
            if (storedResponse["program_stop"]?.GetValue<bool>() == true)
            {
                blockedCall = BlockedCall(call.ToolId, storedArgs);
                return await FinishAsync(StopError(storedResponse), null).ConfigureAwait(false);
            }
            replay.Add(new ProgramToolReceipt(call.ToolId, storedArgs, storedResponse));
        }

        JsonObject Result(string? error, JsonNode? value = null) => new()
        {
            ["program_revision"] = Revision, ["program_id"] = id, ["error"] = error,
            ["status"] = error is null ? "succeeded" : "failed", ["result"] = value?.DeepClone(),
            ["tool_count"] = evidence?.Count ?? 0, ["nested_receipts"] = evidence?.DeepClone() ?? new JsonArray(),
            ["blocked_call"] = blockedCall?.DeepClone(),
            ["effects_unknown"] = uncertain || error is "program_execution_unknown" or "program_replay_mismatch" or "program_trace_limit",
        };
        bool TryAppendEvidence(JsonObject receipt)
        {
            var bytes = Encoding.UTF8.GetByteCount(receipt.ToJsonString());
            if (totalBytes + bytes > 2 * 1024 * 1024)
            {
                uncertain = true;
                return false;
            }
            totalBytes += bytes;
            uncertain |= receipt["response"]?["effects_unknown"]?.GetValue<bool>() == true;
            evidence.Add(receipt.DeepClone());
            return true;
        }
        JsonObject BlockedCall(string toolId, JsonObject arguments)
        {
            var result = new JsonObject { ["tool_id"] = toolId, ["arguments"] = arguments.DeepClone() };
            return Encoding.UTF8.GetByteCount(result.ToJsonString()) <= 16 * 1024
                ? result : new JsonObject { ["tool_id"] = toolId, ["arguments_omitted"] = true };
        }
        static string StopError(JsonObject response) => response["program_error"]?.GetValue<string>()
            ?? response["error_code"]?.GetValue<string>() ?? "program_tool_failed";
        async Task<JsonObject> FinishAsync(string? error, JsonNode? value)
        {
            var result = Result(error, value);
            return (await journal.CreateAsync(runId, prefix + "_final", result, ct).ConfigureAwait(false)).Value;
        }
    }

    private static bool ReceiptMatches(JsonObject receipt, JsonObject intent) =>
        receipt["intent_hash"]?.GetValue<string>() == Hash(intent.ToJsonString())
        && receipt["tool_id"]?.GetValue<string>() == intent["tool_id"]?.GetValue<string>()
        && JsonNode.DeepEquals(receipt["arguments"], intent["arguments"])
        && receipt["response"] is JsonObject;

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
