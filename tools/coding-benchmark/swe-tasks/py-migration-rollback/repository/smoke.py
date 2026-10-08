import sqlite3

from src import migrate

connection = sqlite3.connect(":memory:")
assert migrate(connection) == [1, 2]
assert connection.execute("PRAGMA user_version").fetchone()[0] == 2
assert connection.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='customers'").fetchone()
print("migration smoke passed")
