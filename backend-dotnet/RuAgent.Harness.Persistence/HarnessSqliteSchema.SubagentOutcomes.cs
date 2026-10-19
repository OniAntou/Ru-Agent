namespace RuAgent.Harness.Persistence;

public static partial class HarnessSqliteSchema
{
    private const string SchemaVersionTwentyThreeToTwentyFour = """
        CREATE TABLE IF NOT EXISTS agent_subagent_result_outcomes (
            agent_id TEXT NOT NULL,
            run_id TEXT NOT NULL,
            state INTEGER NOT NULL CHECK(state BETWEEN 7 AND 10),
            code TEXT NULL CHECK(code IS NULL OR length(code) BETWEEN 1 AND 64),
            PRIMARY KEY(agent_id,run_id),
            FOREIGN KEY(agent_id,run_id) REFERENCES agent_subagent_results(agent_id,run_id) ON DELETE CASCADE
        );
        INSERT INTO agent_subagent_result_outcomes(agent_id,run_id,state,code)
            SELECT v.agent_id,v.run_id,
                CASE WHEN s.role=0 AND r.state=7 AND c.run_id IS NULL THEN 8 ELSE r.state END,
                CASE WHEN s.role=0 AND r.state=7 AND c.run_id IS NULL THEN 'subagent_changes_missing' ELSE r.terminal_error_code END
            FROM agent_subagent_results v JOIN agent_runs r ON r.run_id=v.run_id
            JOIN agent_subagents s ON s.agent_id=v.agent_id
            LEFT JOIN agent_subagent_change_sets c ON c.run_id=v.run_id
            WHERE r.state BETWEEN 7 AND 10
            ON CONFLICT(agent_id,run_id) DO NOTHING;
        """;
}
