import contextlib
import importlib
import json
import os
import sqlite3
import sys
from pathlib import Path

candidate = Path(os.environ["BENCHMARK_CANDIDATE_ROOT"]).resolve()
sys.path.insert(0, str(candidate))
migrate = importlib.import_module("src").migrate


def failed_migration_rolls_back_all_statements_and_version():
    connection = fresh_at_v1()
    add_duplicate_emails(connection)
    expect_integrity_error(lambda: migrate(connection))
    assert user_version(connection) == 1
    assert not table_exists(connection, "migration_audit")
    assert not index_exists(connection, "ux_customers_email")


def retry_applies_failed_migration_after_data_repair():
    connection = fresh_at_v1()
    add_duplicate_emails(connection)
    expect_integrity_error(lambda: migrate(connection))
    connection.execute("DELETE FROM customers WHERE id = 2")
    assert migrate(connection) == [2]
    assert migrate(connection) == []
    assert user_version(connection) == 2
    assert table_exists(connection, "migration_audit")
    assert index_exists(connection, "ux_customers_email")


def caller_transaction_survives_migration_failure():
    connection = fresh_at_v1()
    connection.execute("CREATE TABLE caller_data (value TEXT)")
    connection.execute("BEGIN")
    connection.execute("INSERT INTO caller_data VALUES ('uncommitted')")
    add_duplicate_emails(connection)
    expect_integrity_error(lambda: migrate(connection))
    assert connection.in_transaction
    assert connection.execute("SELECT value FROM caller_data").fetchone()[0] == "uncommitted"
    assert connection.execute("SELECT COUNT(*) FROM customers").fetchone()[0] == 2
    assert not table_exists(connection, "migration_audit")
    assert user_version(connection) == 1
    connection.rollback()
    assert connection.execute("SELECT COUNT(*) FROM caller_data").fetchone()[0] == 0


def future_schema_version_fails_without_mutation():
    connection = sqlite3.connect(":memory:", isolation_level=None)
    connection.execute("CREATE TABLE keep_me (value TEXT)")
    connection.execute("PRAGMA user_version = 99")
    before = schema_snapshot(connection)
    try:
        migrate(connection)
    except ValueError:
        pass
    else:
        raise AssertionError("future schema versions must be rejected")
    assert schema_snapshot(connection) == before


def successful_migration_preserves_connection_configuration():
    connection = sqlite3.connect(":memory:", isolation_level=None)
    def marker_row_factory(cursor, row):
        return tuple(row)
    connection.row_factory = marker_row_factory
    connection.execute("PRAGMA foreign_keys = ON")
    before_isolation = connection.isolation_level
    assert migrate(connection) == [1, 2]
    assert connection.isolation_level == before_isolation
    assert connection.row_factory is marker_row_factory
    assert connection.execute("PRAGMA foreign_keys").fetchone()[0] == 1
    assert not connection.in_transaction
    assert connection.execute("SELECT COUNT(*) FROM customers").fetchone() == (0,)


def current_schema_is_idempotent_and_complete():
    connection = sqlite3.connect(":memory:")
    assert migrate(connection) == [1, 2]
    snapshot = schema_snapshot(connection)
    assert migrate(connection) == []
    assert schema_snapshot(connection) == snapshot
    assert user_version(connection) == 2
    assert table_exists(connection, "customers") and table_exists(connection, "migration_audit")
    assert index_exists(connection, "ux_customers_email")


def fresh_at_v1():
    connection = sqlite3.connect(":memory:", isolation_level=None)
    connection.execute("CREATE TABLE customers (id INTEGER PRIMARY KEY, email TEXT NOT NULL)")
    connection.execute("PRAGMA user_version = 1")
    return connection


def add_duplicate_emails(connection):
    connection.executemany("INSERT INTO customers(id, email) VALUES (?, ?)", [(1, "same@example.test"), (2, "same@example.test")])


def user_version(connection):
    return connection.execute("PRAGMA user_version").fetchone()[0]


def table_exists(connection, name):
    return connection.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name=?", (name,)).fetchone() is not None


def index_exists(connection, name):
    return connection.execute("SELECT 1 FROM sqlite_master WHERE type='index' AND name=?", (name,)).fetchone() is not None


def schema_snapshot(connection):
    return (user_version(connection), tuple(connection.execute("SELECT type, name, sql FROM sqlite_master ORDER BY type, name")))


def expect_integrity_error(action):
    completed = False
    with contextlib.suppress(sqlite3.IntegrityError):
        action()
        completed = True
    assert not completed, "duplicate emails should make the unique index migration fail"


def run_group(name, function):
    try:
        function()
        return {"name": name, "passed": True, "checks": 1, "failures": []}
    except Exception as error:
        return {"name": name, "passed": False, "checks": 1, "failures": [f"{type(error).__name__}: {error}"]}


groups = [
    run_group("failed_migration_rolls_back_all_statements_and_version", failed_migration_rolls_back_all_statements_and_version),
    run_group("retry_applies_failed_migration_after_data_repair", retry_applies_failed_migration_after_data_repair),
    run_group("caller_transaction_survives_migration_failure", caller_transaction_survives_migration_failure),
    run_group("future_schema_version_fails_without_mutation", future_schema_version_fails_without_mutation),
    run_group("successful_migration_preserves_connection_configuration", successful_migration_preserves_connection_configuration),
    run_group("current_schema_is_idempotent_and_complete", current_schema_is_idempotent_and_complete),
]
print(json.dumps({"groups": groups}, separators=(",", ":")))
sys.exit(0 if all(group["passed"] for group in groups) else 1)
