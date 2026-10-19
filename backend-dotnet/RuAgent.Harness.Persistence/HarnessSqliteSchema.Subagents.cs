namespace RuAgent.Harness.Persistence;

public static partial class HarnessSqliteSchema
{
    private const string SchemaVersionNineteenToTwenty = """
        CREATE TABLE IF NOT EXISTS agent_subagent_families (
            root_run_id TEXT PRIMARY KEY REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            canceled INTEGER NOT NULL DEFAULT 0 CHECK(canceled IN (0,1))
        );
        CREATE TABLE IF NOT EXISTS agent_subagents (
            agent_id TEXT PRIMARY KEY,
            root_run_id TEXT NOT NULL REFERENCES agent_subagent_families(root_run_id) ON DELETE CASCADE,
            internal_session_id TEXT NOT NULL UNIQUE,
            role INTEGER NOT NULL CHECK(role IN (0,1)),
            canceled INTEGER NOT NULL DEFAULT 0 CHECK(canceled IN (0,1)),
            invocation_id TEXT NOT NULL,
            request_hash TEXT NOT NULL CHECK(length(request_hash)=64),
            UNIQUE(root_run_id, invocation_id)
        );
        CREATE INDEX IF NOT EXISTS ix_agent_subagents_root ON agent_subagents(root_run_id, agent_id);
        CREATE TABLE IF NOT EXISTS agent_subagent_attempts (
            agent_id TEXT NOT NULL REFERENCES agent_subagents(agent_id) ON DELETE CASCADE,
            attempt_number INTEGER NOT NULL CHECK(attempt_number BETWEEN 1 AND 3),
            run_id TEXT NOT NULL UNIQUE REFERENCES agent_runs(run_id) ON DELETE RESTRICT,
            created_at_utc TEXT NOT NULL,
            admitted_at_utc TEXT NULL,
            deadline_at_utc TEXT NULL,
            PRIMARY KEY(agent_id, attempt_number)
        );
        CREATE TABLE IF NOT EXISTS agent_subagent_messages (
            agent_id TEXT NOT NULL REFERENCES agent_subagents(agent_id) ON DELETE CASCADE,
            message_id TEXT NOT NULL,
            payload_id TEXT NOT NULL REFERENCES agent_payloads(payload_id) ON DELETE CASCADE,
            consumed_run_id TEXT NULL REFERENCES agent_runs(run_id) ON DELETE RESTRICT,
            continuation_run_id TEXT NULL REFERENCES agent_runs(run_id) ON DELETE RESTRICT,
            continuation_error_code TEXT NULL CHECK(continuation_error_code IS NULL OR length(continuation_error_code) BETWEEN 1 AND 64),
            PRIMARY KEY(agent_id, message_id)
        );
        CREATE TABLE IF NOT EXISTS agent_subagent_results (
            agent_id TEXT NOT NULL REFERENCES agent_subagents(agent_id) ON DELETE CASCADE,
            result_version INTEGER NOT NULL CHECK(result_version>0),
            run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE RESTRICT,
            payload_id TEXT NOT NULL REFERENCES agent_payloads(payload_id) ON DELETE CASCADE,
            delivered INTEGER NOT NULL DEFAULT 0 CHECK(delivered IN (0,1)),
            PRIMARY KEY(agent_id, result_version)
        );
        CREATE TABLE IF NOT EXISTS agent_subagent_budgets (
            root_run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            window INTEGER NOT NULL CHECK(window>0),
            model_steps INTEGER NOT NULL CHECK(model_steps BETWEEN 0 AND 128),
            tool_calls INTEGER NOT NULL CHECK(tool_calls BETWEEN 0 AND 256),
            PRIMARY KEY(root_run_id, window)
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_subagent_results_attempt ON agent_subagent_results(agent_id,run_id);
        CREATE TABLE IF NOT EXISTS agent_subagent_reservations (
            root_run_id TEXT NOT NULL,
            window INTEGER NOT NULL,
            invocation_id TEXT NOT NULL,
            kind INTEGER NOT NULL CHECK(kind IN (0,1)),
            amount INTEGER NOT NULL DEFAULT 1 CHECK(amount BETWEEN 1 AND 256),
            PRIMARY KEY(root_run_id, window, invocation_id),
            FOREIGN KEY(root_run_id, window) REFERENCES agent_subagent_budgets(root_run_id, window) ON DELETE CASCADE
        );
        """;
}
