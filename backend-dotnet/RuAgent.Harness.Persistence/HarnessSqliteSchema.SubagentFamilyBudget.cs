namespace RuAgent.Harness.Persistence;

public static partial class HarnessSqliteSchema
{
    /// <summary>
    /// Schema v35: the family budget counters' CHECK bounds were raised to 1,024 model steps
    /// and 2,048 tool calls for day-long runs. SQLite cannot alter a CHECK, so the table is rebuilt; the
    /// migration runner disables foreign keys around the batch, as for the v21 run rebuild,
    /// so the reservations that reference these rows are kept.
    /// </summary>
    private const string SubagentFamilyBudgetMigration = """
        CREATE TABLE agent_subagent_budgets_v35 (
            root_run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            window INTEGER NOT NULL CHECK(window>0),
            model_steps INTEGER NOT NULL CHECK(model_steps BETWEEN 0 AND 1024),
            tool_calls INTEGER NOT NULL CHECK(tool_calls BETWEEN 0 AND 2048),
            PRIMARY KEY(root_run_id, window)
        );
        INSERT INTO agent_subagent_budgets_v35(root_run_id, window, model_steps, tool_calls)
            SELECT root_run_id, window, model_steps, tool_calls FROM agent_subagent_budgets;
        DROP TABLE agent_subagent_budgets;
        ALTER TABLE agent_subagent_budgets_v35 RENAME TO agent_subagent_budgets;
        SELECT 1;
        """;

    /// <summary>
    /// Schema v36: the family counters no longer have an upper bound. A root and its children
    /// work until they finish, so the row only records what the family has used in the root's
    /// current window and keeps reservations idempotent. Rebuilt for the same reason as v35.
    /// </summary>
    private const string SubagentFamilyBudgetUnboundedMigration = """
        CREATE TABLE agent_subagent_budgets_v36 (
            root_run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            window INTEGER NOT NULL CHECK(window>0),
            model_steps INTEGER NOT NULL CHECK(model_steps>=0),
            tool_calls INTEGER NOT NULL CHECK(tool_calls>=0),
            PRIMARY KEY(root_run_id, window)
        );
        INSERT INTO agent_subagent_budgets_v36(root_run_id, window, model_steps, tool_calls)
            SELECT root_run_id, window, model_steps, tool_calls FROM agent_subagent_budgets;
        DROP TABLE agent_subagent_budgets;
        ALTER TABLE agent_subagent_budgets_v36 RENAME TO agent_subagent_budgets;
        SELECT 1;
        """;
}
