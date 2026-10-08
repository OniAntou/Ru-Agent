# A failed schema migration leaves half-applied objects and a misleading version

The SQLite migration helper executes a migration's statements one by one and updates `PRAGMA user_version` outside a transaction. If a later statement fails, earlier DDL remains in the database. A rerun then encounters those leftover objects even though the schema version still claims the migration never ran.

## Reproduction

Start with `sqlite3.connect(":memory:", isolation_level=None)`, insert two customer rows with the same email, and call `migrate(connection)`. Migration 2 creates its audit table before adding a unique email index. The index creation fails on the duplicate data. The audit table is left behind and the database reports version 1. The next call fails because the migration tries to create the audit table again.

## Expected behavior

- Each migration's statements and its `user_version` update form one atomic unit. If any statement fails, every schema/data/version change made by that migration is rolled back.
- Previously completed migrations remain intact. After the caller fixes the invalid duplicate data, retrying applies the failed migration once and advances the version.
- The helper works with both an idle connection and one already inside a caller-owned transaction. On failure it preserves the caller's earlier uncommitted work and leaves that transaction active.
- The helper does not close the connection or alter its `isolation_level`, `row_factory`, or `foreign_keys` setting.
- A database already at the latest supported version is unchanged. A database claiming a future version fails clearly without mutation.

Preserve `migrate(connection)` and the migration data module's public names. Use only the Python standard library and keep migration statements ordered and reviewable.
