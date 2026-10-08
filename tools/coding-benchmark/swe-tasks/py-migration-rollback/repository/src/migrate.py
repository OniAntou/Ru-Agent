import sqlite3

from .migrations import MIGRATIONS


def migrate(connection: sqlite3.Connection) -> list[int]:
    current_version = connection.execute("PRAGMA user_version").fetchone()[0]
    applied: list[int] = []
    for migration in MIGRATIONS:
        if migration.version <= current_version:
            continue
        for statement in migration.statements:
            connection.execute(statement)
        connection.execute(f"PRAGMA user_version = {migration.version}")
        applied.append(migration.version)
        current_version = migration.version
    return applied
