using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentScopeStore(HarnessSqliteOptions options, WorkspaceJournalProtector protector)
{
    public async Task SaveAsync(RunId rootId, RunId childId, SubagentRuntimeScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(scope);
        if (bytes.Length > WorkspaceJournalProtector.MaximumJournalBytes) throw new HarnessStoreException("subagent_scope_too_large");
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var owner = await ReadOwnerAsync(connection, transaction, childId, ct).ConfigureAwait(false)
            ?? throw new HarnessStoreException("subagent_not_found");
        if (owner.RootId != rootId) throw new HarnessStoreException("subagent_not_found");
        var root = await HarnessSqliteQueries.GetRunAsync(connection, transaction, rootId, ct).ConfigureAwait(false)
            ?? throw new HarnessStoreException("subagent_root_not_found");
        var child = await HarnessSqliteQueries.GetRunAsync(connection, transaction, childId, ct).ConfigureAwait(false)
            ?? throw new HarnessStoreException("subagent_not_found");
        Validate(scope, root, child, owner.Role);
        var existing = await ReadAsync(connection, transaction, childId, root.WorkspaceId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing != scope) throw new HarnessStoreException("subagent_scope_conflict");
            return;
        }
        if (owner.Canceled || root.State.IsTerminal() || child.State != HarnessRunState.Created)
            throw new HarnessStoreException("subagent_root_inactive");
        var encrypted = await protector.EncryptAsync(root.WorkspaceId, childId.Value, "subagent.scope", bytes, ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO agent_subagent_scopes(run_id,protection_version,nonce,ciphertext,authentication_tag)
            VALUES($run,$version,$nonce,$ciphertext,$tag)
            """;
        command.Parameters.AddWithValue("$run", childId.Value);
        command.Parameters.AddWithValue("$version", encrypted.ProtectionVersion);
        command.Parameters.AddWithValue("$nonce", encrypted.Nonce.ToArray());
        command.Parameters.AddWithValue("$ciphertext", encrypted.Ciphertext.ToArray());
        command.Parameters.AddWithValue("$tag", encrypted.AuthenticationTag.ToArray());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<SubagentRuntimeScope?> GetAsync(RunId childId, CancellationToken ct)
    {
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var owner = await ReadOwnerAsync(connection, transaction, childId, ct).ConfigureAwait(false);
        if (owner is null) return null;
        var root = await HarnessSqliteQueries.GetRunAsync(connection, transaction, owner.RootId, ct).ConfigureAwait(false)
            ?? throw new HarnessStoreException("subagent_root_not_found");
        var child = await HarnessSqliteQueries.GetRunAsync(connection, transaction, childId, ct).ConfigureAwait(false)
            ?? throw new HarnessStoreException("subagent_not_found");
        var scope = await ReadAsync(connection, transaction, childId, root.WorkspaceId, ct).ConfigureAwait(false);
        if (scope is not null) Validate(scope, root, child, owner.Role);
        return scope;
    }

    private async Task<SubagentRuntimeScope?> ReadAsync(SqliteConnection connection, SqliteTransaction transaction,
        RunId childId, WorkspaceId workspaceId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT protection_version,nonce,ciphertext,authentication_tag FROM agent_subagent_scopes WHERE run_id=$run";
        command.Parameters.AddWithValue("$run", childId.Value);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var envelope = new EncryptedWorkspaceJournal(reader.GetInt32(0),
            HarnessSqliteMapper.ReadBoundedBlob(reader, 1, 12, "subagent_scope_invalid", 12),
            HarnessSqliteMapper.ReadBoundedBlob(reader, 2, WorkspaceJournalProtector.MaximumJournalBytes, "subagent_scope_invalid"),
            HarnessSqliteMapper.ReadBoundedBlob(reader, 3, 16, "subagent_scope_invalid", 16));
        var bytes = await protector.DecryptAsync(workspaceId, childId.Value, "subagent.scope", envelope, ct).ConfigureAwait(false);
        try { return JsonSerializer.Deserialize<SubagentRuntimeScope>(bytes.Span) ?? throw new HarnessStoreException("subagent_scope_invalid"); }
        catch (JsonException ex) { throw new HarnessStoreException("subagent_scope_invalid", ex); }
    }

    private sealed record Owner(RunId RootId, SubagentRole Role, bool Canceled);
    private static async Task<Owner?> ReadOwnerAsync(SqliteConnection connection, SqliteTransaction transaction, RunId childId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.root_run_id,s.role,(s.canceled OR f.canceled) FROM agent_subagent_attempts a
            JOIN agent_subagents s ON s.agent_id=a.agent_id JOIN agent_subagent_families f ON f.root_run_id=s.root_run_id
            WHERE a.run_id=$run
            """;
        command.Parameters.AddWithValue("$run", childId.Value);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? new(new(reader.GetString(0)), (SubagentRole)reader.GetInt32(1), reader.GetInt32(2) != 0) : null;
    }

    private static void Validate(SubagentRuntimeScope scope, AgentRun root, AgentRun child, SubagentRole role)
    {
        try
        {
            var policy = BrokerPolicyContracts.ReadSnapshot(JsonNode.Parse(scope.PolicySnapshotJson) as JsonObject
                ?? throw new HarnessStoreException("subagent_scope_invalid"));
            var parentPolicy = BrokerPolicyContracts.ReadSnapshot(JsonNode.Parse(scope.ParentPolicySnapshotJson) as JsonObject
                ?? throw new HarnessStoreException("subagent_scope_invalid"));
            if (scope.ParentPolicy is null || scope.ParentPolicy.Version != root.PolicySnapshotVersion
                || scope.ParentPolicy.Sha256 != root.PolicySnapshotSha256 || policy.Version != child.PolicySnapshotVersion
                || parentPolicy.Version != root.PolicySnapshotVersion
                || BrokerPolicyContracts.ComputeCanonicalSnapshotHash(parentPolicy) != root.PolicySnapshotSha256
                || parentPolicy.Workspace.WorkspaceId != root.WorkspaceId.Value
                || parentPolicy.Preset != AgentPermissionPresetMapper.ToWireValue(root.Preset)
                || BrokerPolicyContracts.ComputeCanonicalSnapshotHash(policy) != child.PolicySnapshotSha256
                || policy.Workspace.WorkspaceId != child.WorkspaceId.Value
                || policy.Preset != AgentPermissionPresetMapper.ToWireValue(child.Preset)
                || !Path.IsPathFullyQualified(scope.ParentRoot) || Path.GetFullPath(scope.ParentRoot) != scope.ParentRoot
                || !Path.IsPathFullyQualified(scope.WorkspaceRoot) || Path.GetFullPath(scope.WorkspaceRoot) != scope.WorkspaceRoot
                || role == SubagentRole.Review && !string.Equals(scope.ParentRoot, scope.WorkspaceRoot, StringComparison.OrdinalIgnoreCase)
                || role == SubagentRole.Coding && string.IsNullOrWhiteSpace(scope.WorktreeManifestJson))
                throw new HarnessStoreException("subagent_scope_invalid");
        }
        catch (Exception ex) when (ex is JsonException or AgentProtocolException or ArgumentException)
        { throw new HarnessStoreException("subagent_scope_invalid", ex); }
    }
}
