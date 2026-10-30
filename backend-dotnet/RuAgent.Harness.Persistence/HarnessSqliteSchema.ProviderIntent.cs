namespace RuAgent.Harness.Persistence;

public static partial class HarnessSqliteSchema
{
    // Two development branches used v32: one introduced this table, the other
    // the execution index. Merge both shapes without replacing retained intent.
    private const string ProviderIntentMigration = """
        CREATE TABLE IF NOT EXISTS agent_run_provider_intents (
            run_id TEXT PRIMARY KEY REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            backend TEXT NOT NULL CHECK(length(backend) BETWEEN 1 AND 128),
            model TEXT NOT NULL CHECK(length(model) BETWEEN 1 AND 256)
        );
        CREATE INDEX IF NOT EXISTS ix_agent_events_execution_request
            ON agent_events(run_id, execution_id, tool_call_id, provider_call_id, sequence)
            WHERE kind = 'tool.execution_requested';
        """;
}
