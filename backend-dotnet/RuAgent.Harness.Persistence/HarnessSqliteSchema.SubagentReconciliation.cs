namespace RuAgent.Harness.Persistence;

public static partial class HarnessSqliteSchema
{
    private const string SchemaVersionTwentySixToTwentySeven = """
        CREATE TABLE IF NOT EXISTS agent_subagent_apply_resolutions (
            root_run_id TEXT NOT NULL,
            agent_id TEXT NOT NULL,
            result_version INTEGER NOT NULL CHECK(result_version BETWEEN 1 AND 3),
            resolution_event_id TEXT NOT NULL UNIQUE REFERENCES agent_events(event_id) ON DELETE CASCADE,
            model_step_id TEXT NOT NULL,
            tool_call_id TEXT NOT NULL,
            execution_id TEXT NOT NULL,
            decision TEXT NOT NULL CHECK(decision IN ('applied','not_applied','unknown_continue')),
            PRIMARY KEY(root_run_id,agent_id,result_version),
            FOREIGN KEY(root_run_id,agent_id,result_version)
                REFERENCES agent_subagent_apply_operations(root_run_id,agent_id,result_version) ON DELETE CASCADE
        );
        """;
}
