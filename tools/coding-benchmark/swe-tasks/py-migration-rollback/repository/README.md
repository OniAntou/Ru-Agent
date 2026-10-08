# Migration runner

This standard-library-only package owns two SQLite schema migrations. Call `migrate(connection)` with an open `sqlite3.Connection`; it returns the version numbers applied during that call. The connection remains owned by the caller.

Run the visible contract checks with `python smoke.py`. The migration history lives in `src/migrations.py`; preserve its exported `Migration` and `MIGRATIONS` names and the `migrate(connection)` API.
