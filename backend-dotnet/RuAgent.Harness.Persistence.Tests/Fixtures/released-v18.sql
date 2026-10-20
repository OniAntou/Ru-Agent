-- Exact fresh-install SQL from v0.1.1-preview.1 (ec5e8fbec4c4945aaf54fd4308b3d31448fd7895).
-- Extracted from that tag's schema constants in fresh-install order, not from current schema.
CREATE TABLE IF NOT EXISTS agent_workspaces (
    workspace_id TEXT PRIMARY KEY,
    canonical_root_nonce BLOB NOT NULL CHECK(length(canonical_root_nonce) = 12),
    canonical_root_ciphertext BLOB NOT NULL CHECK(length(canonical_root_ciphertext) BETWEEN 1 AND 1048576),
    canonical_root_tag BLOB NOT NULL CHECK(length(canonical_root_tag) = 16),
    volume_serial_number TEXT NOT NULL CHECK(length(volume_serial_number) BETWEEN 1 AND 64),
    root_file_id TEXT NOT NULL CHECK(length(root_file_id) BETWEEN 1 AND 128),
    display_identity_hash TEXT NOT NULL CHECK(length(display_identity_hash) = 64),
    trust_request_id TEXT NOT NULL UNIQUE,
    trust_requested_at_utc TEXT NOT NULL,
    trust_expires_at_utc TEXT NOT NULL,
    trust_decision INTEGER NULL CHECK(trust_decision IN (0, 1)),
    trust_decided_at_utc TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS agent_session_settings (
    session_id TEXT PRIMARY KEY,
    preset INTEGER NOT NULL CHECK(preset IN (0, 1, 2)),
    version INTEGER NOT NULL CHECK(version > 0),
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS agent_runs (
    run_id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL,
    workspace_id TEXT NOT NULL REFERENCES agent_workspaces(workspace_id) ON DELETE RESTRICT,
    state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 10),
    history_wait_kind INTEGER NULL CHECK(history_wait_kind IN (0, 1)),
    version INTEGER NOT NULL CHECK(version >= 0),
    last_sequence INTEGER NOT NULL CHECK(last_sequence >= 0),
    budget_window INTEGER NOT NULL CHECK(budget_window > 0),
    model_steps_in_window INTEGER NOT NULL CHECK(model_steps_in_window >= 0),
    tool_calls_in_window INTEGER NOT NULL CHECK(tool_calls_in_window >= 0),
    preset INTEGER NOT NULL CHECK(preset IN (0, 1, 2)),
    session_preset_version INTEGER NOT NULL CHECK(session_preset_version > 0),
    policy_snapshot_version INTEGER NOT NULL CHECK(policy_snapshot_version > 0),
    policy_snapshot_sha256 TEXT NOT NULL CHECK(length(policy_snapshot_sha256) = 64),
    idempotency_key_sha256 TEXT NOT NULL CHECK(length(idempotency_key_sha256) = 64),
    normalized_request_sha256 TEXT NOT NULL CHECK(length(normalized_request_sha256) = 64),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    terminal_at_utc TEXT NULL,
    terminal_error_code TEXT NULL,
    base_context_captured_at_utc TEXT NULL,
    base_context_entry_count INTEGER NOT NULL DEFAULT 0 CHECK(base_context_entry_count >= 0),
    UNIQUE(session_id, idempotency_key_sha256)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_runs_active_session
    ON agent_runs(session_id)
    WHERE terminal_at_utc IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_runs_active_workspace
    ON agent_runs(workspace_id)
    WHERE terminal_at_utc IS NULL;

CREATE TABLE IF NOT EXISTS agent_payloads (
    payload_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
    kind INTEGER NOT NULL,
    audience INTEGER NOT NULL,
    encoding INTEGER NOT NULL,
    byte_cap INTEGER NOT NULL CHECK(byte_cap BETWEEN 1 AND 16777216),
    protected_content BLOB NOT NULL,
    protection_version INTEGER NOT NULL CHECK(protection_version = 1),
    nonce BLOB NOT NULL CHECK(length(nonce) = 12),
    authentication_tag BLOB NOT NULL CHECK(length(authentication_tag) = 16),
    consumed_at_utc TEXT NULL,
    context_scope INTEGER NULL CHECK(context_scope IN (0, 1)),
    context_source_id TEXT NULL CHECK(length(context_source_id) BETWEEN 1 AND 128),
    context_sequence INTEGER NULL CHECK(context_sequence > 0),
    context_role INTEGER NULL CHECK(context_role BETWEEN 0 AND 3),
    context_entry_kind INTEGER NULL CHECK(context_entry_kind BETWEEN 0 AND 3),
    created_at_utc TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_payloads_context_source
    ON agent_payloads(run_id, context_source_id)
    WHERE context_scope IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_payloads_context_sequence
    ON agent_payloads(run_id, context_scope, context_sequence)
    WHERE context_scope IS NOT NULL;

CREATE TABLE IF NOT EXISTS agent_events (
    event_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
    sequence INTEGER NOT NULL CHECK(sequence > 0),
    kind TEXT NOT NULL CHECK(length(kind) BETWEEN 1 AND 64),
    occurred_at_utc TEXT NOT NULL,
    payload_id TEXT NULL REFERENCES agent_payloads(payload_id) ON DELETE RESTRICT,
     model_step_id TEXT NULL,
     tool_call_id TEXT NULL,
     execution_id TEXT NULL,
    approval_id TEXT NULL,
    safe_count INTEGER NULL CHECK(safe_count >= 0),
    safe_code TEXT NULL CHECK(length(safe_code) BETWEEN 1 AND 64),
    tool_outcome INTEGER NULL CHECK(tool_outcome BETWEEN 0 AND 4),
    side_effects_possible INTEGER NULL CHECK(side_effects_possible IN (0, 1)),
    UNIQUE(run_id, sequence)
);
CREATE INDEX IF NOT EXISTS ix_agent_events_provider_continuation
    ON agent_events(run_id, kind, sequence DESC)
    WHERE kind = 'model.provider_continuation_recorded';
CREATE INDEX IF NOT EXISTS ix_agent_events_session_receipts
    ON agent_events(run_id, sequence DESC)
    WHERE kind = 'tool.execution_completed';

CREATE TABLE IF NOT EXISTS agent_conversation_outbox (
    intent_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
    session_id TEXT NOT NULL,
    payload_id TEXT NOT NULL REFERENCES agent_payloads(payload_id) ON DELETE RESTRICT,
    role INTEGER NOT NULL CHECK(role IN (0, 1)),
    history_wait_kind INTEGER NOT NULL CHECK(history_wait_kind IN (0, 1)),
    delivery_state INTEGER NOT NULL DEFAULT 0 CHECK(delivery_state BETWEEN 0 AND 3),
    attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count >= 0),
    next_attempt_at_utc TEXT NULL,
    claim_token TEXT NULL,
    lease_expires_at_utc TEXT NULL,
    last_error_code TEXT NULL,
    acknowledged_at_utc TEXT NULL,
    created_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_agent_conversation_outbox_dispatch
    ON agent_conversation_outbox(
        delivery_state,
        next_attempt_at_utc,
        lease_expires_at_utc,
        created_at_utc,
        intent_id);

CREATE TABLE IF NOT EXISTS agent_global_rules (
    rule_id TEXT PRIMARY KEY,
    rule_document BLOB NOT NULL,
    version INTEGER NOT NULL CHECK(version > 0),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS agent_project_rules (
    rule_id TEXT PRIMARY KEY,
    workspace_id TEXT NOT NULL REFERENCES agent_workspaces(workspace_id) ON DELETE CASCADE,
    rule_document BLOB NOT NULL,
    version INTEGER NOT NULL CHECK(version > 0),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS agent_run_rules (
    rule_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
    rule_document BLOB NOT NULL,
    created_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS agent_context_compactions (
    compaction_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
    summary_payload_id TEXT NOT NULL REFERENCES agent_payloads(payload_id) ON DELETE RESTRICT,
    expected_run_version INTEGER NOT NULL CHECK(expected_run_version >= 0),
    strategy TEXT NOT NULL CHECK(length(strategy) BETWEEN 1 AND 32),
    reason TEXT NOT NULL CHECK(length(reason) BETWEEN 1 AND 32),
    covered_ranges_json TEXT NOT NULL CHECK(length(covered_ranges_json) BETWEEN 2 AND 1048576),
    before_tokens INTEGER NOT NULL CHECK(before_tokens >= 0),
    after_tokens INTEGER NOT NULL CHECK(after_tokens >= 0 AND after_tokens <= before_tokens),
    input_hash TEXT NOT NULL CHECK(length(input_hash) = 64),
    created_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_agent_context_compactions_run_created
    ON agent_context_compactions(run_id, created_at_utc, compaction_id);

CREATE TABLE IF NOT EXISTS workspace_coordinators (
    workspace_id TEXT PRIMARY KEY REFERENCES agent_workspaces(workspace_id) ON DELETE CASCADE,
    mode TEXT NOT NULL CHECK(mode IN ('exclusive', 'shared')),
    max_concurrent_runs INTEGER NOT NULL CHECK(max_concurrent_runs BETWEEN 1 AND 32),
    lease_ttl_seconds INTEGER NOT NULL CHECK(lease_ttl_seconds BETWEEN 5 AND 600),
    merge_text_conflicts INTEGER NOT NULL CHECK(merge_text_conflicts IN (0, 1)),
    external_writer_detection INTEGER NOT NULL CHECK(external_writer_detection IN (0, 1)),
    commit_sequence INTEGER NOT NULL CHECK(commit_sequence >= 0),
    policy_json TEXT NOT NULL CHECK(length(policy_json) BETWEEN 2 AND 16384),
    version INTEGER NOT NULL CHECK(version > 0),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS workspace_run_members (
    member_id TEXT PRIMARY KEY,
    workspace_id TEXT NOT NULL REFERENCES workspace_coordinators(workspace_id) ON DELETE CASCADE,
    run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
    writer_kind TEXT NOT NULL CHECK(writer_kind IN ('ru_peer', 'unknown_external')),
    state TEXT NOT NULL CHECK(state IN ('active', 'expired', 'released')),
    heartbeat_at_utc TEXT NOT NULL,
    lease_expires_at_utc TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    UNIQUE(workspace_id, run_id)
);

CREATE TABLE IF NOT EXISTS workspace_path_claims (
    claim_id TEXT PRIMARY KEY,
    workspace_id TEXT NOT NULL REFERENCES workspace_coordinators(workspace_id) ON DELETE CASCADE,
    run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
    normalized_path TEXT NOT NULL CHECK(length(normalized_path) BETWEEN 1 AND 4096),
    path_kind TEXT NOT NULL CHECK(path_kind IN ('text', 'binary', 'unknown')),
    state TEXT NOT NULL CHECK(state IN ('active', 'expired', 'released', 'conflict')),
    base_sha256 TEXT NULL CHECK(base_sha256 IS NULL OR length(base_sha256) = 64),
    lease_expires_at_utc TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    UNIQUE(workspace_id, run_id, normalized_path)
);

CREATE INDEX IF NOT EXISTS ix_workspace_path_claims_active
    ON workspace_path_claims(workspace_id, normalized_path, state, lease_expires_at_utc);

CREATE TABLE IF NOT EXISTS workspace_change_journal (
    change_id TEXT PRIMARY KEY,
    workspace_id TEXT NOT NULL REFERENCES workspace_coordinators(workspace_id) ON DELETE CASCADE,
    run_id TEXT NULL REFERENCES agent_runs(run_id) ON DELETE SET NULL,
    commit_sequence INTEGER NOT NULL CHECK(commit_sequence > 0),
    normalized_path TEXT NOT NULL CHECK(length(normalized_path) BETWEEN 1 AND 4096),
    before_sha256 TEXT NULL CHECK(before_sha256 IS NULL OR length(before_sha256) = 64),
    after_sha256 TEXT NULL CHECK(after_sha256 IS NULL OR length(after_sha256) = 64),
    protected_payload BLOB NOT NULL,
    nonce BLOB NOT NULL CHECK(length(nonce) = 12),
    authentication_tag BLOB NOT NULL CHECK(length(authentication_tag) = 16),
    protection_version INTEGER NOT NULL CHECK(protection_version = 1),
    status TEXT NOT NULL CHECK(status IN ('prepared', 'committed', 'restored', 'rejected')),
    created_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_workspace_change_journal_sequence
    ON workspace_change_journal(workspace_id, commit_sequence, created_at_utc);

CREATE TABLE IF NOT EXISTS agent_file_recovery (
    recovery_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
    canonical_path TEXT NOT NULL CHECK(length(canonical_path) BETWEEN 1 AND 4096),
    content_sha256 TEXT NOT NULL CHECK(length(content_sha256) = 64),
    content_length INTEGER NOT NULL CHECK(content_length >= 0),
    created_at_utc TEXT NOT NULL,
    consumed_at_utc TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_agent_file_recovery_run ON agent_file_recovery(run_id);

DROP INDEX IF EXISTS ux_agent_runs_active_workspace;

CREATE TABLE agent_session_settings_new (
    session_id TEXT PRIMARY KEY,
    preset INTEGER NOT NULL CHECK(preset IN (0, 1, 2)),
    version INTEGER NOT NULL CHECK(version > 0),
    updated_at_utc TEXT NOT NULL
);
INSERT INTO agent_session_settings_new(session_id, preset, version, updated_at_utc)
    SELECT session_id, preset, version, updated_at_utc FROM agent_session_settings;
DROP TABLE agent_session_settings;
ALTER TABLE agent_session_settings_new RENAME TO agent_session_settings;

CREATE TABLE agent_runs_new (
    run_id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL,
    workspace_id TEXT NOT NULL REFERENCES agent_workspaces(workspace_id) ON DELETE RESTRICT,
    state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 10),
    history_wait_kind INTEGER NULL CHECK(history_wait_kind IN (0, 1)),
    version INTEGER NOT NULL CHECK(version >= 0),
    last_sequence INTEGER NOT NULL CHECK(last_sequence >= 0),
    budget_window INTEGER NOT NULL CHECK(budget_window > 0),
    model_steps_in_window INTEGER NOT NULL CHECK(model_steps_in_window >= 0),
    tool_calls_in_window INTEGER NOT NULL CHECK(tool_calls_in_window >= 0),
    preset INTEGER NOT NULL CHECK(preset IN (0, 1, 2)),
    session_preset_version INTEGER NOT NULL CHECK(session_preset_version > 0),
    policy_snapshot_version INTEGER NOT NULL CHECK(policy_snapshot_version > 0),
    policy_snapshot_sha256 TEXT NOT NULL CHECK(length(policy_snapshot_sha256) = 64),
    idempotency_key_sha256 TEXT NOT NULL CHECK(length(idempotency_key_sha256) = 64),
    normalized_request_sha256 TEXT NOT NULL CHECK(length(normalized_request_sha256) = 64),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    terminal_at_utc TEXT NULL,
    terminal_error_code TEXT NULL,
    base_context_captured_at_utc TEXT NULL,
    base_context_entry_count INTEGER NOT NULL DEFAULT 0 CHECK(base_context_entry_count >= 0),
    UNIQUE(session_id, idempotency_key_sha256)
);
INSERT INTO agent_runs_new(run_id, session_id, workspace_id, state, history_wait_kind, version, last_sequence, budget_window, model_steps_in_window, tool_calls_in_window, preset, session_preset_version, policy_snapshot_version, policy_snapshot_sha256, idempotency_key_sha256, normalized_request_sha256, created_at_utc, updated_at_utc, terminal_at_utc, terminal_error_code, base_context_captured_at_utc, base_context_entry_count)
    SELECT run_id, session_id, workspace_id, state, history_wait_kind, version, last_sequence, budget_window, model_steps_in_window, tool_calls_in_window, preset, session_preset_version, policy_snapshot_version, policy_snapshot_sha256, idempotency_key_sha256, normalized_request_sha256, created_at_utc, updated_at_utc, terminal_at_utc, terminal_error_code, base_context_captured_at_utc, base_context_entry_count FROM agent_runs;
DROP TABLE agent_runs;
ALTER TABLE agent_runs_new RENAME TO agent_runs;
CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_runs_active_session
    ON agent_runs(session_id)
    WHERE terminal_at_utc IS NULL;

CREATE TABLE IF NOT EXISTS agent_file_recovery (
    recovery_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
    canonical_path TEXT NOT NULL CHECK(length(canonical_path) BETWEEN 1 AND 4096),
    content_sha256 TEXT NOT NULL CHECK(length(content_sha256) = 64),
    content_length INTEGER NOT NULL CHECK(content_length >= 0),
    created_at_utc TEXT NOT NULL,
    consumed_at_utc TEXT NULL
);
CREATE INDEX IF NOT EXISTS ix_agent_file_recovery_run ON agent_file_recovery(run_id);

DROP TABLE IF EXISTS workspace_mutations;

CREATE TABLE agent_session_settings_v8 (
    session_id TEXT PRIMARY KEY,
    preset TEXT NOT NULL CHECK(preset IN ('plan', 'standard', 'full_access')),
    permission_profile_version INTEGER NOT NULL CHECK(permission_profile_version > 0),
    version INTEGER NOT NULL CHECK(version > 0),
    updated_at_utc TEXT NOT NULL
);
INSERT INTO agent_session_settings_v8(
    session_id,
    preset,
    permission_profile_version,
    version,
    updated_at_utc)
    SELECT
        session_id,
        'plan',
        2,
        version,
        updated_at_utc
    FROM agent_session_settings;
DROP TABLE agent_session_settings;
ALTER TABLE agent_session_settings_v8 RENAME TO agent_session_settings;

CREATE TABLE agent_runs_v8 (
    run_id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL,
    workspace_id TEXT NOT NULL REFERENCES agent_workspaces(workspace_id) ON DELETE RESTRICT,
    state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 10),
    history_wait_kind INTEGER NULL CHECK(history_wait_kind IN (0, 1)),
    version INTEGER NOT NULL CHECK(version >= 0),
    last_sequence INTEGER NOT NULL CHECK(last_sequence >= 0),
    budget_window INTEGER NOT NULL CHECK(budget_window > 0),
    model_steps_in_window INTEGER NOT NULL CHECK(model_steps_in_window >= 0),
    tool_calls_in_window INTEGER NOT NULL CHECK(tool_calls_in_window >= 0),
    preset TEXT NOT NULL CHECK(preset IN ('plan', 'standard', 'full_access')),
    permission_profile_version INTEGER NOT NULL CHECK(permission_profile_version >= 0),
    session_preset_version INTEGER NOT NULL CHECK(session_preset_version > 0),
    policy_snapshot_version INTEGER NOT NULL CHECK(policy_snapshot_version > 0),
    policy_snapshot_sha256 TEXT NOT NULL CHECK(length(policy_snapshot_sha256) = 64),
    idempotency_key_sha256 TEXT NOT NULL CHECK(length(idempotency_key_sha256) = 64),
    normalized_request_sha256 TEXT NOT NULL CHECK(length(normalized_request_sha256) = 64),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    terminal_at_utc TEXT NULL,
    terminal_error_code TEXT NULL,
    base_context_captured_at_utc TEXT NULL,
    base_context_entry_count INTEGER NOT NULL DEFAULT 0 CHECK(base_context_entry_count >= 0),
    UNIQUE(session_id, idempotency_key_sha256)
);
INSERT INTO agent_runs_v8(
    run_id,
    session_id,
    workspace_id,
    state,
    history_wait_kind,
    version,
    last_sequence,
    budget_window,
    model_steps_in_window,
    tool_calls_in_window,
    preset,
    permission_profile_version,
    session_preset_version,
    policy_snapshot_version,
    policy_snapshot_sha256,
    idempotency_key_sha256,
    normalized_request_sha256,
    created_at_utc,
    updated_at_utc,
    terminal_at_utc,
    terminal_error_code,
    base_context_captured_at_utc,
    base_context_entry_count)
    SELECT
        run_id,
        session_id,
        workspace_id,
        state,
        history_wait_kind,
        version,
        last_sequence,
        budget_window,
        model_steps_in_window,
        tool_calls_in_window,
        'plan',
        0,
        session_preset_version,
        policy_snapshot_version,
        policy_snapshot_sha256,
        idempotency_key_sha256,
        normalized_request_sha256,
        created_at_utc,
        updated_at_utc,
        terminal_at_utc,
        terminal_error_code,
        base_context_captured_at_utc,
        base_context_entry_count
    FROM agent_runs;
DROP TABLE agent_runs;
ALTER TABLE agent_runs_v8 RENAME TO agent_runs;
CREATE UNIQUE INDEX ux_agent_runs_active_session
    ON agent_runs(session_id)
    WHERE terminal_at_utc IS NULL;

ALTER TABLE agent_workspaces
    ADD COLUMN trust_source TEXT NULL CHECK(trust_source IN ('explicit'));
ALTER TABLE agent_workspaces
    ADD COLUMN trust_profile_version INTEGER NOT NULL DEFAULT 1 CHECK(trust_profile_version = 1);
UPDATE agent_workspaces
SET trust_decision = NULL,
    trust_decided_at_utc = NULL,
    trust_source = NULL;

ALTER TABLE agent_events
    ADD COLUMN provider_call_id TEXT NULL CHECK(provider_call_id IS NULL OR length(provider_call_id) BETWEEN 1 AND 256);

CREATE TABLE IF NOT EXISTS agent_session_preset_snapshots (
    snapshot_token TEXT PRIMARY KEY CHECK(length(snapshot_token) = 36),
    session_id TEXT NOT NULL CHECK(length(session_id) BETWEEN 1 AND 64),
    had_previous_selection INTEGER NOT NULL CHECK(had_previous_selection IN (0, 1)),
    previous_preset TEXT NULL CHECK(previous_preset IS NULL OR previous_preset IN ('plan', 'standard', 'full_access')),
    previous_permission_profile_version INTEGER NULL CHECK(previous_permission_profile_version IS NULL OR previous_permission_profile_version > 0),
    previous_version INTEGER NULL CHECK(previous_version IS NULL OR previous_version > 0),
    previous_updated_at_utc TEXT NULL,
    applied_preset TEXT NOT NULL CHECK(applied_preset IN ('plan', 'standard', 'full_access')),
    applied_permission_profile_version INTEGER NOT NULL CHECK(applied_permission_profile_version > 0),
    applied_version INTEGER NOT NULL CHECK(applied_version > 0),
    expires_at_utc TEXT NOT NULL,
    consumed_at_utc TEXT NULL,
    created_at_utc TEXT NOT NULL,
    CHECK (
        (had_previous_selection = 0
            AND previous_preset IS NULL
            AND previous_permission_profile_version IS NULL
            AND previous_version IS NULL
            AND previous_updated_at_utc IS NULL)
        OR
        (had_previous_selection = 1
            AND previous_preset IS NOT NULL
            AND previous_permission_profile_version IS NOT NULL
            AND previous_version IS NOT NULL
            AND previous_updated_at_utc IS NOT NULL)
    )
);
CREATE INDEX IF NOT EXISTS ix_agent_session_preset_snapshots_expiry
    ON agent_session_preset_snapshots(expires_at_utc);
CREATE INDEX IF NOT EXISTS ix_agent_session_preset_snapshots_session
    ON agent_session_preset_snapshots(session_id, consumed_at_utc, expires_at_utc);

DROP TABLE IF EXISTS workspace_file_snapshots;

CREATE TABLE agent_run_provider_selections (
    run_id TEXT PRIMARY KEY REFERENCES agent_runs(run_id) ON DELETE CASCADE,
    backend TEXT NULL CHECK(backend IS NULL OR length(backend) BETWEEN 1 AND 128),
    alias TEXT NULL CHECK(alias IS NULL OR length(alias) BETWEEN 1 AND 64),
    protocol TEXT NULL CHECK(protocol IS NULL OR length(protocol) BETWEEN 1 AND 32),
    base_uri TEXT NULL CHECK(base_uri IS NULL OR length(base_uri) BETWEEN 1 AND 2048),
    model TEXT NULL CHECK(model IS NULL OR length(model) BETWEEN 1 AND 256),
    account TEXT NULL CHECK(account IS NULL OR length(account) BETWEEN 1 AND 128),
    request_options_json TEXT NULL CHECK(request_options_json IS NULL OR length(request_options_json) BETWEEN 2 AND 8192),
    maximum_output_tokens INTEGER NULL CHECK(maximum_output_tokens IS NULL OR maximum_output_tokens BETWEEN 1 AND 8192),
    CHECK (
        (backend IS NULL AND alias IS NULL AND protocol IS NULL AND base_uri IS NULL
            AND model IS NULL AND account IS NULL AND request_options_json IS NULL
            AND maximum_output_tokens IS NULL)
        OR
        (backend IS NOT NULL AND alias IS NOT NULL AND protocol IS NOT NULL AND base_uri IS NOT NULL
            AND model IS NOT NULL AND request_options_json IS NOT NULL)
    )
);

CREATE INDEX IF NOT EXISTS ix_agent_runs_session_created_run
    ON agent_runs(session_id, created_at_utc DESC, run_id DESC);

ALTER TABLE agent_run_provider_selections ADD COLUMN model_label TEXT NOT NULL DEFAULT 'unknown-model';
ALTER TABLE agent_run_provider_selections ADD COLUMN calling_mode TEXT NOT NULL DEFAULT 'text_selection';
ALTER TABLE agent_run_provider_selections ADD COLUMN context_window_tokens INTEGER NULL;
ALTER TABLE agent_run_provider_selections ADD COLUMN context_window_source TEXT NOT NULL DEFAULT 'unknown';
ALTER TABLE agent_run_provider_selections ADD COLUMN context_window_version TEXT NOT NULL DEFAULT 'unknown-v1';
ALTER TABLE agent_run_provider_selections ADD COLUMN output_token_reserve INTEGER NOT NULL DEFAULT 8192;
UPDATE agent_run_provider_selections
SET model_label = COALESCE(model, 'unknown-model'),
    calling_mode = CASE WHEN protocol = 'chat_completions' THEN 'native' ELSE 'text_selection' END;

CREATE INDEX IF NOT EXISTS ix_agent_events_provider_continuation
    ON agent_events(run_id, kind, sequence DESC)
    WHERE kind = 'model.provider_continuation_recorded';
UPDATE agent_run_provider_selections
SET model_label = COALESCE(model_label, model, 'unknown-model')
WHERE backend IS NOT NULL AND model_label IS NULL;
UPDATE agent_run_provider_selections
SET calling_mode = COALESCE(calling_mode, CASE WHEN protocol = 'chat_completions' THEN 'native' ELSE 'text_selection' END)
WHERE backend IS NOT NULL AND calling_mode IS NULL;

CREATE TABLE agent_session_work_states (
    session_id TEXT NOT NULL CHECK(length(session_id) = 32),
    workspace_id TEXT NOT NULL REFERENCES agent_workspaces(workspace_id) ON DELETE CASCADE,
    workspace_identity_sha256 TEXT NOT NULL CHECK(length(workspace_identity_sha256) = 64),
    source_run_id TEXT NOT NULL CHECK(length(source_run_id) = 36),
    source_event_id TEXT NOT NULL CHECK(length(source_event_id) = 36),
    version INTEGER NOT NULL CHECK(version > 0),
    protection_version INTEGER NOT NULL CHECK(protection_version = 1),
    nonce BLOB NOT NULL CHECK(length(nonce) = 12),
    ciphertext BLOB NOT NULL CHECK(length(ciphertext) BETWEEN 1 AND 16384),
    authentication_tag BLOB NOT NULL CHECK(length(authentication_tag) = 16),
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY(session_id, workspace_id)
);
CREATE INDEX ix_agent_session_work_states_workspace
    ON agent_session_work_states(workspace_id, session_id);
CREATE INDEX IF NOT EXISTS ix_agent_events_session_receipts
    ON agent_events(run_id, sequence DESC)
    WHERE kind = 'tool.execution_completed';

CREATE TABLE IF NOT EXISTS agent_session_tombstones (
    session_id TEXT PRIMARY KEY CHECK(length(session_id) BETWEEN 1 AND 64),
    deleted_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS business_migrations (name TEXT PRIMARY KEY NOT NULL);
CREATE TABLE IF NOT EXISTS business_settings (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    document TEXT NOT NULL CHECK (json_valid(document))
);
CREATE TABLE IF NOT EXISTS business_sessions (
    session_id TEXT PRIMARY KEY NOT NULL,
    document BLOB NOT NULL,
    summary TEXT NOT NULL CHECK (json_valid(summary)),
    updated_at_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS business_sessions_updated ON business_sessions(updated_at_utc DESC, session_id);
CREATE TABLE IF NOT EXISTS business_activity (
    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    event_id TEXT UNIQUE,
    timestamp REAL NOT NULL,
    document TEXT NOT NULL CHECK (json_valid(document))
);
CREATE INDEX IF NOT EXISTS business_activity_timestamp ON business_activity(timestamp DESC, sequence DESC);
PRAGMA user_version = 18;
