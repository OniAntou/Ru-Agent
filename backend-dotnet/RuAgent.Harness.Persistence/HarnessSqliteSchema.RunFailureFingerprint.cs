namespace RuAgent.Harness.Persistence;

public static partial class HarnessSqliteSchema
{
    /// <summary>
    /// Bounded fingerprint of the failure that ended a run, one row per failed run.
    /// A side table rather than run columns: fixture databases are downgraded by hand
    /// and replay the suffix, so every migration here must be replay-safe, and
    /// <c>ALTER TABLE ... ADD COLUMN</c> is not.
    /// </summary>
    private const string RunFailureFingerprintMigration = """
        CREATE TABLE IF NOT EXISTS agent_run_failures (
            run_id TEXT PRIMARY KEY REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            kind TEXT NOT NULL CHECK(length(kind) BETWEEN 1 AND 32),
            site TEXT NULL CHECK(site IS NULL OR length(site) BETWEEN 1 AND 160),
            recorded_at_utc TEXT NOT NULL
        );
        SELECT 1;
        """;
}
