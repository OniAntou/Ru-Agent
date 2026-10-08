import sqlite3

from .migrations import MIGRATIONS


def migrate(connection: sqlite3.Connection) -> list[int]:
    current_version = connection.execute("PRAGMA user_version").fetchone()[0]
    latest_version = MIGRATIONS[-1].version if MIGRATIONS else 0
    if current_version > latest_version:
        raise ValueError(f"database version {current_version} is newer than supported version {latest_version}")

    applied: list[int] = []
    for migration in MIGRATIONS:
        if migration.version <= current_version:
            continue
        savepoint = f"ru_migration_{migration.version}"
        connection.execute(f"SAVEPOINT {savepoint}")
        try:
            for statement in migration.statements:
                connection.execute(statement)
            connection.execute(f"PRAGMA user_version = {migration.version}")
            connection.execute(f"RELEASE SAVEPOINT {savepoint}")
        except BaseException:
            connection.execute(f"ROLLBACK TO SAVEPOINT {savepoint}")
            connection.execute(f"RELEASE SAVEPOINT {savepoint}")
            raise
        applied.append(migration.version)
        current_version = migration.version
    return applied
