using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence;

public static partial class HarnessSqliteSchema
{
    /// <summary>
    /// Schema v35: the family budget counters' CHECK bounds follow
    /// <see cref="SubagentLimits.FamilyModelSteps"/> and <see cref="SubagentLimits.FamilyToolCalls"/>,
    /// raised for day-long runs. SQLite cannot alter a CHECK, so the table is rebuilt; the
    /// migration runner disables foreign keys around the batch, as for the v21 run rebuild,
    /// so the reservations that reference these rows are kept.
    /// </summary>
    private static readonly string SubagentFamilyBudgetMigration = $"""
        CREATE TABLE agent_subagent_budgets_v35 (
            root_run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            window INTEGER NOT NULL CHECK(window>0),
            model_steps INTEGER NOT NULL CHECK(model_steps BETWEEN 0 AND {SubagentLimits.FamilyModelSteps}),
            tool_calls INTEGER NOT NULL CHECK(tool_calls BETWEEN 0 AND {SubagentLimits.FamilyToolCalls}),
            PRIMARY KEY(root_run_id, window)
        );
        INSERT INTO agent_subagent_budgets_v35(root_run_id, window, model_steps, tool_calls)
            SELECT root_run_id, window, model_steps, tool_calls FROM agent_subagent_budgets;
        DROP TABLE agent_subagent_budgets;
        ALTER TABLE agent_subagent_budgets_v35 RENAME TO agent_subagent_budgets;
        SELECT 1;
        """;
}
