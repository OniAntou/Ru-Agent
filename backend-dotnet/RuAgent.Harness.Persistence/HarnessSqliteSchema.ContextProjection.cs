namespace RuAgent.Harness.Persistence;

public static partial class HarnessSqliteSchema
{
    // Append watermarks are safe only while existing source bytes/receipt metadata
    // stay unchanged. These triggers also catch updates made outside the write set.
    private const string ContextProjectionMigration = """
        ALTER TABLE agent_runs ADD COLUMN context_revision INTEGER NOT NULL DEFAULT 0 CHECK(context_revision >= 0);
        CREATE INDEX ix_business_session_messages_session_source
            ON business_session_messages(session_id, source_run_id);
        CREATE INDEX ix_agent_context_compactions_run_version
            ON agent_context_compactions(run_id, expected_run_version, compaction_id);
        CREATE TRIGGER context_payload_updated AFTER UPDATE ON agent_payloads BEGIN
            UPDATE agent_runs SET context_revision=context_revision+1 WHERE run_id=OLD.run_id;
            UPDATE agent_runs SET context_revision=context_revision+1 WHERE run_id=NEW.run_id AND NEW.run_id<>OLD.run_id;
        END;
        CREATE TRIGGER context_payload_deleted AFTER DELETE ON agent_payloads BEGIN
            UPDATE agent_runs SET context_revision=context_revision+1 WHERE run_id=OLD.run_id;
        END;
        CREATE TRIGGER context_event_updated AFTER UPDATE ON agent_events BEGIN
            UPDATE agent_runs SET context_revision=context_revision+1 WHERE run_id=OLD.run_id;
            UPDATE agent_runs SET context_revision=context_revision+1 WHERE run_id=NEW.run_id AND NEW.run_id<>OLD.run_id;
        END;
        CREATE TRIGGER context_event_deleted AFTER DELETE ON agent_events BEGIN
            UPDATE agent_runs SET context_revision=context_revision+1 WHERE run_id=OLD.run_id;
        END;
        CREATE TRIGGER context_compaction_updated AFTER UPDATE ON agent_context_compactions BEGIN
            UPDATE agent_runs SET context_revision=context_revision+1 WHERE run_id=OLD.run_id;
            UPDATE agent_runs SET context_revision=context_revision+1 WHERE run_id=NEW.run_id AND NEW.run_id<>OLD.run_id;
        END;
        CREATE TRIGGER context_compaction_deleted AFTER DELETE ON agent_context_compactions BEGIN
            UPDATE agent_runs SET context_revision=context_revision+1 WHERE run_id=OLD.run_id;
        END;
        SELECT 1;
        """;
}
