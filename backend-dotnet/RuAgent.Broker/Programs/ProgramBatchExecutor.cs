using System.Text.Json.Nodes;
using RuAgent.Protocol.Programs;

namespace RuAgent.Broker.Programs;

public sealed record ProgramBatchExecutionResult(
    IReadOnlyList<ProgramIndexedToolReceipt> Receipts,
    string? ErrorCode = null);
public sealed record ProgramIndexedToolReceipt(int Index, ProgramToolReceipt Receipt);

/// <summary>Runs a validated read-only batch through the existing Broker invocation callback.</summary>
public sealed class ProgramBatchExecutor(IProgramJournal journal)
{
    public async Task<ProgramBatchExecutionResult> ExecuteAsync(
        string runId,
        string prefix,
        ProgramToolBatchRequest batch,
        Func<ProgramToolRequest, string, CancellationToken, Task<JsonObject>> invoke,
        CancellationToken ct)
    {
        if (batch.Calls.Count > ProgramToolContract.MaximumParallelCalls)
            return new([], "program_parallel_limit");
        if (batch.StartIndex < 0 || batch.StartIndex > ProgramEvaluator.MaximumCalls - batch.Calls.Count)
            return new([], "program_call_limit");
        if (batch.Calls.Count < 1)
            return new([], "program_parallel_arguments_invalid");
        if (batch.Calls.Any(call => !ProgramToolContract.CanRunInParallel(call.ToolId)))
            return new([], "program_parallel_tool_not_read_only");

        var items = batch.Calls.Select((call, offset) => new BatchItem(
            prefix + "_" + (batch.StartIndex + offset),
            call,
            new JsonObject { ["tool_id"] = call.ToolId, ["arguments"] = call.Arguments.DeepClone() })).ToArray();
        var receipts = new ProgramToolReceipt?[items.Length];
        var intents = new JsonObject?[items.Length];
        var unknownDispatch = false;

        for (var index = 0; index < items.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[index];
            var storedReceipt = await journal.ReadAsync(runId, item.Key + "_receipt", ct).ConfigureAwait(false);
            var storedIntent = await journal.ReadAsync(runId, item.Key + "_intent", ct).ConfigureAwait(false);
            var storedRequest = await journal.ReadAsync(runId, item.Key + "_request", ct).ConfigureAwait(false);
            if (storedIntent is not null && !JsonNode.DeepEquals(storedIntent, item.Intent))
                return new(AvailableReceipts(), "program_replay_mismatch");
            if (storedRequest is not null && !JsonNode.DeepEquals(storedRequest, item.Intent))
                return new(AvailableReceipts(), "program_replay_mismatch");
            if (storedRequest is not null && storedIntent is null)
                return new(AvailableReceipts(), "program_replay_mismatch");
            if (storedReceipt is not null)
            {
                if (storedIntent is null || storedRequest is null || !ReceiptMatches(storedReceipt, item.Intent))
                    return new(AvailableReceipts(), "program_replay_mismatch");
                receipts[index] = ReadReceipt(item.Call, storedReceipt);
                continue;
            }

            if (storedRequest is not null)
            {
                // A dispatch marker without a receipt may have run. Never replay it or
                // start other members of the same batch while its outcome is unknown.
                unknownDispatch = true;
                continue;
            }
            intents[index] = storedIntent ?? item.Intent;
        }

        if (unknownDispatch) return new(AvailableReceipts(), "program_execution_unknown");

        // Reserve the complete batch in one durable operation. A partial reservation
        // would consume shared family budget even though no member could be dispatched.
        var reservationKey = prefix + "_batch_" + batch.StartIndex + "_" + batch.Calls.Count;
        if (!await journal.ReserveToolsAsync(runId, reservationKey, batch.Calls.Count, ct).ConfigureAwait(false))
            return new(AvailableReceipts(), "program_budget_exhausted");

        // Persist PREPARED intents for every member before dispatching any. If this
        // preparation fails partway through, no request marker exists and replay can
        // safely complete preparation.
        for (var index = 0; index < items.Length; index++)
        {
            if (receipts[index] is not null) continue;
            ct.ThrowIfCancellationRequested();
            var item = items[index];
            var written = await journal.CreateAsync(runId, item.Key + "_intent", intents[index]!, ct).ConfigureAwait(false);
            if (!JsonNode.DeepEquals(written.Value, item.Intent))
                return new(AvailableReceipts(), "program_replay_mismatch");
        }

        var pending = items.Select((item, index) => (item, index))
            .Where(pair => receipts[pair.index] is null)
            .Select(pair => DispatchAndRecordAsync(pair.item, pair.index))
            .ToArray();
        var completed = await Task.WhenAll(pending).ConfigureAwait(false);
        var errorCode = completed.Select(result => result.ErrorCode).FirstOrDefault(error => error is not null);
        foreach (var result in completed)
            if (result.Receipt is not null) receipts[result.Index] = result.Receipt;
        return new(AvailableReceipts(), errorCode);

        async Task<(int Index, ProgramToolReceipt? Receipt, string? ErrorCode)> DispatchAndRecordAsync(BatchItem item, int index)
        {
            // This durable marker is the dispatch boundary. A crash after it is
            // conservatively unknown; prepared-only intents remain safe to resume.
            var request = await journal.CreateAsync(runId, item.Key + "_request", item.Intent, ct).ConfigureAwait(false);
            if (!request.Created)
            {
                return JsonNode.DeepEquals(request.Value, item.Intent)
                    ? (index, null, "program_execution_unknown")
                    : (index, null, "program_replay_mismatch");
            }

            var response = await invoke(item.Call, item.Key, ct).ConfigureAwait(false);
            var stored = await journal.CreateAsync(runId, item.Key + "_receipt", new JsonObject
            {
                ["intent_hash"] = ProgramRunner.Hash(item.Intent.ToJsonString()),
                ["tool_id"] = item.Call.ToolId,
                ["arguments"] = item.Call.Arguments.DeepClone(),
                ["response"] = response,
            }, ct).ConfigureAwait(false);
            if (!ReceiptMatches(stored.Value, item.Intent))
                return (index, null, "program_replay_mismatch");
            return (index, ReadReceipt(item.Call, stored.Value), null);
        }

        IReadOnlyList<ProgramIndexedToolReceipt> AvailableReceipts() => receipts
            .Select((receipt, index) => (receipt, index))
            .Where(item => item.receipt is not null)
            .Select(item => new ProgramIndexedToolReceipt(batch.StartIndex + item.index, item.receipt!))
            .ToArray();
    }

    private static bool ReceiptMatches(JsonObject receipt, JsonObject intent)
    {
        return receipt["intent_hash"]?.GetValue<string>() == ProgramRunner.Hash(intent.ToJsonString())
            && receipt["tool_id"]?.GetValue<string>() == intent["tool_id"]?.GetValue<string>()
            && JsonNode.DeepEquals(receipt["arguments"], intent["arguments"])
            && receipt["response"] is JsonObject;
    }

    private static ProgramToolReceipt ReadReceipt(ProgramToolRequest call, JsonObject receipt) => new(
        call.ToolId,
        (JsonObject)receipt["arguments"]!.DeepClone(),
        (JsonObject)receipt["response"]!.DeepClone());

    private sealed record BatchItem(string Key, ProgramToolRequest Call, JsonObject Intent);
}
