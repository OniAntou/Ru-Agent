namespace RuAgent.Harness.Persistence;

public static partial class HarnessSqliteSchema
{
    private const string SchemaVersionTwentySevenToTwentyEight = """
        CREATE TABLE IF NOT EXISTS agent_subagent_user_applies (
            root_run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            agent_id TEXT NOT NULL,
            result_version INTEGER NOT NULL CHECK(result_version BETWEEN 1 AND 3),
            action_key TEXT NOT NULL UNIQUE CHECK(length(action_key)=32),
            status TEXT NOT NULL CHECK(status IN ('queued','awaiting_approval','executing','completed','unknown','failed','cancelled')),
            epoch TEXT,
            approval_id TEXT,
            code TEXT,
            revision INTEGER NOT NULL DEFAULT 1 CHECK(revision>0),
            PRIMARY KEY(root_run_id,agent_id,result_version),
            FOREIGN KEY(agent_id,result_version) REFERENCES agent_subagent_results(agent_id,result_version) ON DELETE CASCADE
        );
        """;
}
