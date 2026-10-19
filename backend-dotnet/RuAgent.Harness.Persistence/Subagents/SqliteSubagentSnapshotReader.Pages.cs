using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentSnapshotReader
{
    public async Task<SubagentResultPage> ReadResultAsync(RunId root, string agent, long version,
        int offset, int limit, CancellationToken ct)
    {
        if (!SubagentContractValidator.IsValidId(agent) || version is < 1 or > SubagentLimits.MaxAttempts
            || offset < 0 || limit is < 4 or > SubagentLimits.MaxInlineResultBytes)
            throw new HarnessStoreException("subagent_page_invalid");
        await using var connection = await Options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        _ = await RequireRootAsync(connection, transaction, root, ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$root", root.Value);
        command.Parameters.AddWithValue("$agent", agent);
        command.Parameters.AddWithValue("$version", version);
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_subagents WHERE root_run_id=$root AND agent_id=$agent)";
        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 1)
            throw new HarnessStoreException("subagent_not_found");
        command.CommandText = """
            SELECT v.payload_id,v.run_id,o.state,r.state,a.attempt_number FROM agent_subagent_results v
            JOIN agent_runs r ON r.run_id=v.run_id
            LEFT JOIN agent_subagent_result_outcomes o ON o.agent_id=v.agent_id AND o.run_id=v.run_id
            LEFT JOIN agent_subagent_attempts a ON a.agent_id=v.agent_id AND a.run_id=v.run_id
            WHERE v.agent_id=$agent AND v.result_version=$version
            """;
        PayloadId payloadId;
        RunId child;
        using (var row = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await row.ReadAsync(ct).ConfigureAwait(false)) throw new HarnessStoreException("subagent_result_not_found");
            if (row.IsDBNull(2) || row.IsDBNull(4)) Invalid();
            SubagentResultRow.Validate(version, row.GetInt64(4), (HarnessRunState)row.GetInt32(2), (HarnessRunState)row.GetInt32(3));
            payloadId = new(row.GetString(0));
            child = new(row.GetString(1));
        }
        var payload = await Payloads.ReadForClientAsync(connection, transaction, child, payloadId, ct).ConfigureAwait(false);
        if (payload.Kind != HarnessPayloadKind.AssistantText || payload.Encoding != HarnessPayloadEncoding.Utf8Text) Invalid();
        return Page(root.Value, agent, version, payload.Plaintext, offset, limit);
    }

    private static SubagentResultPage Page(string root, string agent, long version, ReadOnlyMemory<byte> content, int offset, int limit)
    {
        var bytes = content.Span;
        if (offset > bytes.Length || offset < bytes.Length && (bytes[offset] & 0xc0) == 0x80)
            throw new HarnessStoreException("subagent_page_invalid");
        var end = (int)Math.Min(bytes.Length, (long)offset + limit);
        while (end > offset && end < bytes.Length && (bytes[end] & 0xc0) == 0x80) end--;
        if (end == offset && offset < bytes.Length) throw new HarnessStoreException("subagent_page_invalid");
        try
        {
            return new(root, agent, version, new UTF8Encoding(false, true).GetString(bytes[offset..end]),
                offset, bytes.Length, end < bytes.Length ? end : null);
        }
        catch (DecoderFallbackException error) { throw new HarnessStoreException("subagent_result_invalid", error); }
    }
}
