using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Persistence;

public sealed partial class SqliteHarnessEventStore
{
    private async Task UpdateWorkStateFromReceiptsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HarnessCommit commit,
        AgentRun currentRun,
        CancellationToken cancellationToken)
    {
        foreach (var receipt in commit.Events.Where(evt => evt.Kind == HarnessEventKind.ToolExecutionCompleted
                     && evt.ModelStepId is not null && evt.ProviderCallId is not null))
        {
            try
            {
                var call = await ReadModelToolCallAsync(connection, transaction, commit.RunId,
                    receipt.ModelStepId!.Value, receipt.ProviderCallId!, cancellationToken).ConfigureAwait(false);
                var state = await _workStates.GetInTransactionAsync(connection, transaction,
                    currentRun.SessionId, currentRun.WorkspaceId, cancellationToken, commit.RunId,
                    currentRun.LastSequence + 1, commit.NextRun.LastSequence).ConfigureAwait(false);
                if (state is null) continue;
                if (call is null)
                {
                    await MarkWorkStatePartialAsync(connection, transaction, state, commit,
                        receipt, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var output = await ReadToolOutputAsync(connection, transaction, commit.RunId,
                    receipt.PayloadId, cancellationToken).ConfigureAwait(false);
                var updated = SessionWorkStateReceiptReducer.Apply(state, call, output,
                    receipt.ToolOutcome == ToolExecutionOutcome.Succeeded, currentRun.RunId,
                    receipt.EventId, commit.NextRun.UpdatedAt);
                if (updated is null) continue;
                _ = await _workStates.WriteInTransactionAsync(connection, transaction, updated,
                    state.Version, commit.NextRun.UpdatedAt, cancellationToken).ConfigureAwait(false);
            }
            catch (HarnessStoreException error) when (error.Code.StartsWith("session_work_state_", StringComparison.Ordinal))
            {
                continue;
            }
            catch (HarnessStoreException)
            {
                await TryMarkWorkStatePartialAsync(connection, transaction, currentRun, commit,
                    receipt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is ArgumentException or JsonException)
            {
                await TryMarkWorkStatePartialAsync(connection, transaction, currentRun, commit,
                    receipt, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
                continue;
            }
        }
    }

    private async Task TryMarkWorkStatePartialAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentRun run,
        HarnessCommit commit,
        HarnessEventIntent receipt,
        CancellationToken cancellationToken)
    {
        try
        {
            await MarkWorkStatePartialAsync(connection, transaction, run, commit,
                receipt, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessStoreException error) when (error.Code.StartsWith("session_work_state_", StringComparison.Ordinal))
        {
        }
        catch (SqliteException)
        {
        }
        catch (Exception error) when (error is ArgumentException or JsonException)
        {
        }
    }

    private async Task MarkWorkStatePartialAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentRun run,
        HarnessCommit commit,
        HarnessEventIntent receipt,
        CancellationToken cancellationToken)
    {
        var state = await _workStates.GetInTransactionAsync(connection, transaction,
            run.SessionId, run.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (state is null) return;
        await MarkWorkStatePartialAsync(connection, transaction, state, commit, receipt,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkWorkStatePartialAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionWorkState state,
        HarnessCommit commit,
        HarnessEventIntent receipt,
        CancellationToken cancellationToken)
    {
        var updated = SessionWorkStateReceiptReducer.MarkUnrepresented(state,
            commit.NextRun.RunId, receipt.EventId);
        _ = await _workStates.WriteInTransactionAsync(connection, transaction, updated,
            state.Version, commit.NextRun.UpdatedAt, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement?> ReadToolOutputAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunId runId,
        PayloadId? payloadId,
        CancellationToken cancellationToken)
    {
        if (payloadId is null) return null;
        var payload = await _payloadStore.ReadForModelAsync(connection, transaction, runId,
            payloadId.Value, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(payload.Plaintext);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<ModelToolCall?> ReadModelToolCallAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunId runId,
        ModelStepId modelStepId,
        string providerCallId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT payload_id FROM agent_events
            WHERE run_id = $run_id AND model_step_id = $model_step_id
              AND kind = 'model.step_completed' AND payload_id IS NOT NULL
            LIMIT 2
            """;
        command.Parameters.AddWithValue("$run_id", runId.Value);
        command.Parameters.AddWithValue("$model_step_id", modelStepId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var payloadId = new PayloadId(reader.GetString(0));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        await reader.DisposeAsync().ConfigureAwait(false);
        var payload = await _payloadStore.ReadForModelAsync(connection, transaction, runId,
            payloadId, cancellationToken).ConfigureAwait(false);
        var step = AssistantStepJsonCodec.Deserialize(payload.Plaintext);
        return step.ToolCalls.SingleOrDefault(call =>
            string.Equals(call.ProviderCallId, providerCallId, StringComparison.Ordinal));
    }

    private async Task SaveInitialWorkStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NewAgentRun request,
        CancellationToken cancellationToken)
    {
        var prompt = request.Payloads.SingleOrDefault(payload => payload.Kind == HarnessPayloadKind.UserPrompt);
        if (prompt is null) return;
        var source = request.Events.SingleOrDefault(evt => evt.PayloadId == prompt.PayloadId);
        if (source is null) throw new HarnessStoreException("session_work_state_source_missing");
        var redacted = await _payloadStore.ReadForModelAsync(connection, transaction,
            request.Run.RunId, prompt.PayloadId, cancellationToken).ConfigureAwait(false);
        var text = BoundUserText(redacted.Plaintext.Span);
        var previous = await _workStates.GetInTransactionAsync(connection, transaction,
            request.Run.SessionId, request.Run.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var workspaceHash = await SessionWorkStateStore.ReadWorkspaceIdentityHashAsync(
            connection, transaction, request.Run.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var expectedVersion = previous?.Version ?? 0;
        var state = SessionWorkStateReducer.StartRun(previous, request.Run.SessionId,
            request.Run.WorkspaceId, workspaceHash, request.Run.RunId, source.EventId, text);
        if (!await _workStates.WriteInTransactionAsync(connection, transaction, state,
                expectedVersion, request.Run.UpdatedAt, cancellationToken).ConfigureAwait(false))
            throw new HarnessStoreException("session_work_state_conflict");
    }

    private static string BoundUserText(ReadOnlySpan<byte> bytes)
    {
        const int goalByteCap = 4 * 1024;
        var text = new UTF8Encoding(false, true).GetString(bytes);
        if (Encoding.UTF8.GetByteCount(text) <= goalByteCap) return text;
        var builder = new StringBuilder(goalByteCap);
        var byteCount = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (byteCount + rune.Utf8SequenceLength > goalByteCap - 32) break;
            builder.Append(rune.ToString());
            byteCount += rune.Utf8SequenceLength;
        }
        return builder.Append(" [earlier user intent omitted by work-state cap]").ToString();
    }

}
