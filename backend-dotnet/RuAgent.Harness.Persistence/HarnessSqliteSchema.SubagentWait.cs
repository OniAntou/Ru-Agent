namespace RuAgent.Harness.Persistence;

public static partial class HarnessSqliteSchema
{
    private static readonly string SchemaVersionTwentyToTwentyOne = $"""
        CREATE TABLE agent_runs_v21 (
            run_id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL,
            workspace_id TEXT NOT NULL REFERENCES agent_workspaces(workspace_id) ON DELETE RESTRICT,
            state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 11),
            history_wait_kind INTEGER NULL CHECK(history_wait_kind IN (0,1)),
            version INTEGER NOT NULL CHECK(version>=0),
            last_sequence INTEGER NOT NULL CHECK(last_sequence>=0),
            budget_window INTEGER NOT NULL CHECK(budget_window>0),
            model_steps_in_window INTEGER NOT NULL CHECK(model_steps_in_window>=0),
            tool_calls_in_window INTEGER NOT NULL CHECK(tool_calls_in_window>=0),
            preset TEXT NOT NULL CHECK(preset IN ('{Core.AgentPermissionPresetMapper.PlanWireValue}','{Core.AgentPermissionPresetMapper.StandardWireValue}','{Core.AgentPermissionPresetMapper.FullAccessWireValue}')),
            permission_profile_version INTEGER NOT NULL CHECK(permission_profile_version>=0),
            session_preset_version INTEGER NOT NULL CHECK(session_preset_version>0),
            policy_snapshot_version INTEGER NOT NULL CHECK(policy_snapshot_version>0),
            policy_snapshot_sha256 TEXT NOT NULL CHECK(length(policy_snapshot_sha256)=64),
            idempotency_key_sha256 TEXT NOT NULL CHECK(length(idempotency_key_sha256)=64),
            normalized_request_sha256 TEXT NOT NULL CHECK(length(normalized_request_sha256)=64),
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            terminal_at_utc TEXT NULL,
            terminal_error_code TEXT NULL,
            base_context_captured_at_utc TEXT NULL,
            base_context_entry_count INTEGER NOT NULL DEFAULT 0 CHECK(base_context_entry_count>=0),
            UNIQUE(session_id,idempotency_key_sha256)
        );
        INSERT INTO agent_runs_v21 SELECT * FROM agent_runs;
        DROP TABLE agent_runs;
        ALTER TABLE agent_runs_v21 RENAME TO agent_runs;
        CREATE UNIQUE INDEX ux_agent_runs_active_session ON agent_runs(session_id) WHERE terminal_at_utc IS NULL;
        CREATE INDEX ix_agent_runs_session_created_run ON agent_runs(session_id,created_at_utc DESC,run_id DESC);
        CREATE TABLE IF NOT EXISTS agent_subagent_waits (
            root_run_id TEXT PRIMARY KEY REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            invocation_id TEXT NOT NULL,
            arguments_payload_id TEXT NOT NULL REFERENCES agent_payloads(payload_id) ON DELETE CASCADE,
            deadline_at_utc TEXT NOT NULL,
            delivered INTEGER NOT NULL DEFAULT 0 CHECK(delivered IN (0,1))
        );
        CREATE TABLE IF NOT EXISTS agent_subagent_scopes (
            run_id TEXT PRIMARY KEY REFERENCES agent_subagent_attempts(run_id) ON DELETE CASCADE,
            protection_version INTEGER NOT NULL CHECK(protection_version=1),
            nonce BLOB NOT NULL CHECK(length(nonce)=12),
            ciphertext BLOB NOT NULL,
            authentication_tag BLOB NOT NULL CHECK(length(authentication_tag)=16)
        );
        CREATE TABLE IF NOT EXISTS agent_subagent_apply_operations (
            root_run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
            agent_id TEXT NOT NULL REFERENCES agent_subagents(agent_id) ON DELETE CASCADE,
            result_version INTEGER NOT NULL CHECK(result_version>0),
            fingerprint TEXT NOT NULL CHECK(length(fingerprint)=64),
            status INTEGER NOT NULL CHECK(status BETWEEN 0 AND 4),
            applied_files INTEGER NOT NULL DEFAULT 0 CHECK(applied_files>=0),
            code TEXT NOT NULL,
            PRIMARY KEY(root_run_id,agent_id,result_version)
        );
        CREATE TABLE IF NOT EXISTS agent_subagent_apply_files (
            root_run_id TEXT NOT NULL,
            agent_id TEXT NOT NULL,
            result_version INTEGER NOT NULL,
            file_index INTEGER NOT NULL CHECK(file_index>=0),
            protection_version INTEGER NOT NULL CHECK(protection_version=1),
            nonce BLOB NOT NULL CHECK(length(nonce)=12),
            ciphertext BLOB NOT NULL,
            authentication_tag BLOB NOT NULL CHECK(length(authentication_tag)=16),
            receipted INTEGER NOT NULL DEFAULT 0 CHECK(receipted IN (0,1)),
            PRIMARY KEY(root_run_id,agent_id,result_version,file_index),
            FOREIGN KEY(root_run_id,agent_id,result_version) REFERENCES agent_subagent_apply_operations(root_run_id,agent_id,result_version) ON DELETE CASCADE
        );
        """;
}
