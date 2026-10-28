namespace RuAgent.Harness.Persistence;

public static partial class HarnessSqliteSchema
{
    // The schema migration and its one-time data normalization run in the same
    // transaction. A crash therefore leaves either the original v29 database
    // or a complete v30 store; SQLite rolls both DDL and copied rows back.
    private const string SessionHistorySchemaMigration = """
        ALTER TABLE business_sessions ADD COLUMN title TEXT NOT NULL DEFAULT 'Cuộc trò chuyện mới';
        ALTER TABLE business_sessions ADD COLUMN created_at_utc TEXT NOT NULL DEFAULT '1970-01-01T00:00:00.0000000+00:00';
        ALTER TABLE business_sessions ADD COLUMN parent_session_id TEXT;
        ALTER TABLE business_sessions ADD COLUMN forked_from_message_id TEXT;
        ALTER TABLE business_sessions ADD COLUMN last_agent_activity_at_utc TEXT;
        ALTER TABLE business_sessions ADD COLUMN client_source TEXT;
        ALTER TABLE business_sessions ADD COLUMN total_message_count INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE business_sessions ADD COLUMN history_epoch INTEGER NOT NULL DEFAULT 0;

        CREATE TABLE business_session_messages (
            message_id INTEGER PRIMARY KEY,
            session_id TEXT NOT NULL REFERENCES business_sessions(session_id) ON DELETE CASCADE,
            message_index INTEGER NOT NULL,
            role TEXT NOT NULL,
            content TEXT NOT NULL,
            source_message_id TEXT,
            source_run_id TEXT,
            CONSTRAINT session_history_migration_invalid_message CHECK (
                message_index >= 0
                AND role IN ('user', 'ai')
                AND ((source_message_id IS NULL AND source_run_id IS NULL)
                    OR (source_message_id IS NOT NULL AND source_run_id IS NOT NULL
                        AND length(source_message_id) BETWEEN 1 AND 128
                        AND source_message_id NOT GLOB '*[^a-zA-Z0-9_.:-]*'
                        AND length(source_run_id) BETWEEN 1 AND 128
                        AND source_run_id NOT GLOB '*[^a-zA-Z0-9_.:-]*'))),
            UNIQUE (session_id, message_index)
        );
        CREATE INDEX ix_business_session_messages_session_index
            ON business_session_messages(session_id, message_index);

        CREATE TABLE business_session_source_receipts (
            session_id TEXT NOT NULL REFERENCES business_sessions(session_id) ON DELETE CASCADE,
            source_message_id TEXT NOT NULL,
            fingerprint TEXT NOT NULL,
            CONSTRAINT session_history_migration_invalid_receipt CHECK (
                length(source_message_id) BETWEEN 1 AND 128
                AND source_message_id NOT GLOB '*[^a-zA-Z0-9_.:-]*'
                AND length(fingerprint) = 64
                AND fingerprint NOT GLOB '*[^0-9a-f]*'),
            PRIMARY KEY (session_id, source_message_id)
        );

        INSERT INTO business_session_messages(session_id, message_index, role, content)
        SELECT session_id, -1, 'user', '' FROM business_sessions
        WHERE json_valid(CAST(document AS TEXT))
            AND COALESCE(json_type(
                CASE WHEN json_valid(CAST(document AS TEXT)) THEN CAST(document AS TEXT) ELSE '{"messages":[]}' END,
                '$.messages'), '') <> 'array';
        INSERT INTO business_session_messages(session_id, message_index, role, content)
        SELECT s.session_id, -1, 'user', ''
        FROM business_sessions AS s,
            json_each(CASE WHEN json_valid(CAST(s.document AS TEXT)) THEN CAST(s.document AS TEXT) ELSE '{"messages":[]}' END, '$.messages') AS m
        WHERE m.type <> 'object';
        INSERT INTO business_session_messages(session_id, message_index, role, content)
        SELECT s.session_id, -1, 'user', ''
        FROM business_sessions AS s,
            json_each(CASE WHEN json_valid(CAST(s.document AS TEXT)) THEN CAST(s.document AS TEXT) ELSE '{"messages":[]}' END, '$.messages') AS m
        WHERE m.type = 'object'
            AND (json_type(m.value, '$.role') IS NOT 'text'
                OR json_extract(m.value, '$.role') NOT IN ('user', 'ai')
                OR json_type(m.value, '$.content') IS NOT 'text');
        INSERT INTO business_session_source_receipts(session_id, source_message_id, fingerprint)
        SELECT s.session_id, '', '' FROM business_sessions AS s
        WHERE json_valid(CAST(s.document AS TEXT))
            AND json_type(CAST(s.document AS TEXT), '$.source_message_ledger') IS NOT NULL
            AND json_type(CAST(s.document AS TEXT), '$.source_message_ledger') NOT IN ('object', 'null');
        INSERT INTO business_session_source_receipts(session_id, source_message_id, fingerprint)
        SELECT s.session_id, r.key, ''
        FROM business_sessions AS s,
            json_each(CASE WHEN json_type(
                CASE WHEN json_valid(CAST(s.document AS TEXT)) THEN CAST(s.document AS TEXT) ELSE '{}' END,
                '$.source_message_ledger') = 'object'
                THEN json_extract(CAST(s.document AS TEXT), '$.source_message_ledger') ELSE '{}' END) AS r
        WHERE r.type IS NOT 'text'
            OR length(r.key) NOT BETWEEN 1 AND 128
            OR r.key GLOB '*[^a-zA-Z0-9_.:-]*'
            OR length(r.value) <> 64
            OR r.value GLOB '*[^0-9a-f]*';
        INSERT INTO business_session_source_receipts(session_id, source_message_id, fingerprint)
        SELECT s.session_id, r.key, ''
        FROM business_sessions AS s,
            json_each(CASE WHEN json_type(
                CASE WHEN json_valid(CAST(s.document AS TEXT)) THEN CAST(s.document AS TEXT) ELSE '{}' END,
                '$.source_message_ledger') = 'object'
                THEN json_extract(CAST(s.document AS TEXT), '$.source_message_ledger') ELSE '{}' END) AS r
        GROUP BY s.session_id, r.key
        HAVING count(*) > 1;

        CREATE VIRTUAL TABLE business_session_message_search USING fts5(
            session_id UNINDEXED,
            message_index UNINDEXED,
            role UNINDEXED,
            content,
            content='business_session_messages',
            content_rowid='message_id',
            tokenize='unicode61'
        );
        CREATE TRIGGER business_session_messages_search_insert AFTER INSERT ON business_session_messages BEGIN
            INSERT INTO business_session_message_search(rowid, session_id, message_index, role, content)
            VALUES (new.message_id, new.session_id, new.message_index, new.role, new.content);
        END;
        CREATE TRIGGER business_session_messages_search_delete AFTER DELETE ON business_session_messages BEGIN
            INSERT INTO business_session_message_search(business_session_message_search, rowid, session_id, message_index, role, content)
            VALUES ('delete', old.message_id, old.session_id, old.message_index, old.role, old.content);
        END;
        CREATE TRIGGER business_session_messages_search_update AFTER UPDATE ON business_session_messages BEGIN
            INSERT INTO business_session_message_search(business_session_message_search, rowid, session_id, message_index, role, content)
            VALUES ('delete', old.message_id, old.session_id, old.message_index, old.role, old.content);
            INSERT INTO business_session_message_search(rowid, session_id, message_index, role, content)
            VALUES (new.message_id, new.session_id, new.message_index, new.role, new.content);
        END;

        UPDATE business_sessions
        SET title = CASE WHEN json_valid(CAST(document AS TEXT))
                THEN COALESCE(json_extract(CAST(document AS TEXT), '$.title'), 'Cuộc trò chuyện mới') ELSE 'Cuộc trò chuyện mới' END,
            created_at_utc = CASE WHEN json_valid(CAST(document AS TEXT))
                THEN COALESCE(json_extract(CAST(document AS TEXT), '$.created_at'), '1970-01-01T00:00:00.0000000+00:00') ELSE '1970-01-01T00:00:00.0000000+00:00' END,
            parent_session_id = CASE WHEN json_valid(CAST(document AS TEXT)) THEN json_extract(CAST(document AS TEXT), '$.parent_session_id') ELSE NULL END,
            forked_from_message_id = CASE WHEN json_valid(CAST(document AS TEXT)) THEN json_extract(CAST(document AS TEXT), '$.forked_from_message_id') ELSE NULL END,
            last_agent_activity_at_utc = CASE WHEN json_valid(CAST(document AS TEXT)) THEN json_extract(CAST(document AS TEXT), '$.last_agent_activity_at') ELSE NULL END,
            client_source = CASE CASE WHEN json_valid(CAST(document AS TEXT)) THEN json_extract(CAST(document AS TEXT), '$.client_source') ELSE NULL END
                WHEN 'gui' THEN 'gui' WHEN 'cli' THEN 'cli' ELSE NULL END,
            total_message_count = CASE WHEN json_valid(CAST(document AS TEXT))
                THEN COALESCE(json_array_length(CAST(document AS TEXT), '$.messages'), 0) ELSE 0 END;

        INSERT INTO business_session_messages(session_id, message_index, role, content, source_message_id, source_run_id)
        SELECT s.session_id, CAST(m.key AS INTEGER), json_extract(m.value, '$.role'),
            json_extract(m.value, '$.content'), json_extract(m.value, '$.source_message_id'),
            json_extract(m.value, '$.source_run_id')
        FROM business_sessions AS s,
            json_each(CASE WHEN json_valid(CAST(s.document AS TEXT)) THEN CAST(s.document AS TEXT) ELSE '{"messages":[]}' END, '$.messages') AS m;

        INSERT INTO business_session_source_receipts(session_id, source_message_id, fingerprint)
        SELECT s.session_id, r.key, r.value
        FROM business_sessions AS s,
            json_each(CASE WHEN json_type(
                CASE WHEN json_valid(CAST(s.document AS TEXT)) THEN CAST(s.document AS TEXT) ELSE '{}' END,
                '$.source_message_ledger') = 'object'
                THEN json_extract(CAST(s.document AS TEXT), '$.source_message_ledger') ELSE '{}' END) AS r;

        INSERT INTO business_session_message_search(business_session_message_search) VALUES ('rebuild');

        UPDATE business_sessions
        SET summary = json_set(summary, '$.MessageCount', total_message_count),
            document = CASE WHEN json_valid(CAST(document AS TEXT))
                THEN CAST(json_remove(json_set(CAST(document AS TEXT), '$.messages', json('[]')), '$.source_message_ledger') AS BLOB)
                ELSE document END;
        """;
}
