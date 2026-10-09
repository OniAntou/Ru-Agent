---
status: accepted
date: 2026-10-09
---

# Consolidate business persistence in the existing SQLite database

Settings, conversation sessions and Activity Timeline previously used JSON or
JSONL alongside the durable Harness SQLite database. File replacement, metadata
caches, listing budgets, in-memory deduplication and a legacy activity poller
duplicated persistence concerns.

Schema v18 adds four focused tables to `agent_harness.db`: `business_settings`,
`business_sessions`, `business_activity` and `business_migrations`. Existing
Host-owned stores keep their APIs and validation. Session documents and their
source-message ledgers remain bounded serialized records, with indexed summary
metadata for listing. Settings and sanitized activity are JSON payloads in
SQLite rows. Separate message tables are unnecessary for current access patterns.
Session read/modify/write operations use one immediate transaction; activity
insert, event-ID deduplication and retention share a transaction. Settings writes
are atomic, while Credential Manager updates retain their existing compensation.

Each store imports legacy files once, in the same transaction as its migration
marker. Sessions import all readable, valid documents without the old sidebar
scan budget, preserving existing destination rows and respecting Harness session
deletion tombstones; activity additionally imports legacy outboxes and `.processing`
files. Cancellation and I/O errors roll back the import and leave it retryable.
Malformed session documents and malformed/oversized activity lines remain
excluded by the previous validation rules. Corrupt/oversized settings return
defaults without marking migration complete; repair the legacy settings before
retrying, or explicitly save replacement settings.

Legacy files remain untouched as backups. After a successful import, hand edits
to these files are ignored, and deleting a session or clearing activity cannot
reimport old records. No ongoing dual read/write path exists. Repair excluded
session data through the supported session-import interface after correcting
the document. There is no automatic downgrade/export to the old stores.

Stop the old Ru Host before upgrading so it cannot write to the legacy files
after the new build has imported them. New-build stores coordinate writes
through SQLite transactions.

API keys stay in Windows Credential Manager. Existing encrypted Harness payloads
stay encrypted; this decision does not introduce database-wide encryption.
Diagnostics, terminal output spools, runtime descriptors and small GUI preferences
retain their existing file stores. The conversation outbox remains necessary for
history/bootstrap/run-transition recovery; sharing a database alone does not
make those operations one transaction. The unused activity-file poller is removed.

Back up the database through SQLite's backup API or stop all Ru Host processes
before copying the database and any outstanding WAL. Copying only a live `.db`
file is insufficient. Technical-log cleanup excludes legacy activity backups.
The public HTTP/SSE contracts and Broker execution authority stay unchanged.
