namespace RuAgent.Harness.Persistence;

public static partial class HarnessSqliteSchema
{
    private const string SchemaVersionTwentyOneToTwentyTwo = """
        CREATE TABLE IF NOT EXISTS agent_internal_runs (
            run_id TEXT PRIMARY KEY REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            root_run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE RESTRICT,
            CHECK(run_id<>root_run_id)
        );
        CREATE INDEX IF NOT EXISTS ix_agent_internal_runs_root ON agent_internal_runs(root_run_id,run_id);
        INSERT INTO agent_internal_runs(run_id,root_run_id)
            SELECT a.run_id,s.root_run_id FROM agent_subagent_attempts a
            JOIN agent_subagents s ON s.agent_id=a.agent_id WHERE 1 ON CONFLICT DO NOTHING;
        """;
}
