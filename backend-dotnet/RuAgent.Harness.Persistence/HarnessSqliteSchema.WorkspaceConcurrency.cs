namespace RuAgent.Harness.Persistence;

public static partial class HarnessSqliteSchema
{
    /// <summary>
    /// Schema v37: a workspace no longer has a ceiling on concurrent runs. The stored cap
    /// becomes optional, with 0 meaning no cap; a user-chosen cap is still at least two,
    /// since parallel sessions cannot be disabled (ADR 0020). Rows at the old ceiling or
    /// default of 32 move to no cap; smaller caps a user chose are kept. SQLite cannot
    /// alter a CHECK, so the table is rebuilt; the migration runner disables foreign keys
    /// around the batch, as for v35, so memberships, claims and journal rows are kept.
    /// </summary>
    private const string WorkspaceConcurrencyUnboundedMigration = """
        CREATE TABLE workspace_coordinators_v37 (
            workspace_id TEXT PRIMARY KEY REFERENCES agent_workspaces(workspace_id) ON DELETE CASCADE,
            mode TEXT NOT NULL CHECK(mode IN ('exclusive', 'shared')),
            max_concurrent_runs INTEGER NOT NULL CHECK(max_concurrent_runs = 0 OR max_concurrent_runs >= 2),
            lease_ttl_seconds INTEGER NOT NULL CHECK(lease_ttl_seconds BETWEEN 5 AND 600),
            merge_text_conflicts INTEGER NOT NULL CHECK(merge_text_conflicts IN (0, 1)),
            external_writer_detection INTEGER NOT NULL CHECK(external_writer_detection IN (0, 1)),
            commit_sequence INTEGER NOT NULL CHECK(commit_sequence >= 0),
            policy_json TEXT NOT NULL CHECK(length(policy_json) BETWEEN 2 AND 16384),
            version INTEGER NOT NULL CHECK(version > 0),
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );
        INSERT INTO workspace_coordinators_v37(
            workspace_id, mode, max_concurrent_runs, lease_ttl_seconds, merge_text_conflicts,
            external_writer_detection, commit_sequence, policy_json, version, created_at_utc, updated_at_utc)
        SELECT workspace_id, mode,
            CASE WHEN max_concurrent_runs >= 32 THEN 0 ELSE max_concurrent_runs END,
            lease_ttl_seconds, merge_text_conflicts, external_writer_detection, commit_sequence,
            CASE WHEN max_concurrent_runs >= 32
                THEN json_set(policy_json, '$.max_concurrent_runs', 0) ELSE policy_json END,
            CASE WHEN max_concurrent_runs >= 32 THEN version + 1 ELSE version END,
            created_at_utc,
            CASE WHEN max_concurrent_runs >= 32
                THEN strftime('%Y-%m-%dT%H:%M:%f+00:00', 'now') ELSE updated_at_utc END
        FROM workspace_coordinators;
        DROP TABLE workspace_coordinators;
        ALTER TABLE workspace_coordinators_v37 RENAME TO workspace_coordinators;
        SELECT 1;
        """;
}
