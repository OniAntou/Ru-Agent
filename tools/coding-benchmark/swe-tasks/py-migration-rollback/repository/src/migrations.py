from dataclasses import dataclass


@dataclass(frozen=True)
class Migration:
    version: int
    statements: tuple[str, ...]


MIGRATIONS = (
    Migration(
        1,
        (
            "CREATE TABLE customers (id INTEGER PRIMARY KEY, email TEXT NOT NULL)",
        ),
    ),
    Migration(
        2,
        (
            "CREATE TABLE migration_audit (customer_id INTEGER NOT NULL, migrated_at TEXT NOT NULL)",
            "CREATE UNIQUE INDEX ux_customers_email ON customers(email)",
        ),
    ),
)
