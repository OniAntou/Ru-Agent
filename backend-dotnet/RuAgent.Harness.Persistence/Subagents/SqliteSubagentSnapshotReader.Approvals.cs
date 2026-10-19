using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentSnapshotReader
{
    private async Task<SubagentPendingApproval?> ReadApprovalAsync(SqliteConnection connection,
        SqliteTransaction tx, AgentRun run, CancellationToken ct)
    {
        if (run.State != HarnessRunState.WaitingApproval) return null;
        using var sql = connection.CreateCommand();
        sql.Transaction = tx;
        sql.CommandText = """
            SELECT a.approval_id,p.payload_id FROM agent_events a JOIN agent_events p
                ON p.run_id=a.run_id AND p.model_step_id=a.model_step_id AND p.tool_call_id=a.tool_call_id
            WHERE a.run_id=$run AND a.kind='approval.requested' AND p.kind='tool.proposed' AND p.sequence<a.sequence
            ORDER BY a.sequence DESC LIMIT 1
            """;
        sql.Parameters.AddWithValue("$run", run.RunId.Value);
        ApprovalId id;
        PayloadId payload;
        using (var rows = await sql.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await rows.ReadAsync(ct).ConfigureAwait(false) || rows.IsDBNull(0) || rows.IsDBNull(1)) return null;
            id = new(rows.GetString(0)); payload = new(rows.GetString(1));
        }
        var content = await Payloads.ReadForClientAsync(connection, tx, run.RunId, payload, ct).ConfigureAwait(false);
        if (content.Kind != HarnessPayloadKind.ToolArguments || content.Encoding != HarnessPayloadEncoding.Json) Invalid();
        var envelope = ToolArgumentsCodec.Deserialize(content.Plaintext);
        if (envelope.ToolId is null || !envelope.HasObjectArguments) Invalid();
        if (content.Plaintext.Length > SubagentLimits.MaxApprovalPreviewBytes)
            return new(id, envelope.ToolId!, System.Text.Json.JsonSerializer.SerializeToElement(
                new { error_code = "subagent_approval_details_too_large", byte_count = content.Plaintext.Length }), true);
        return new(id, envelope.ToolId!, envelope.Arguments.Clone());
    }
}
