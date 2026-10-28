---
status: accepted
date: 2026-10-26
---

# Relational conversation history and explicit database maintenance

Ru retains one authoritative SQLite database and the existing Harness event,
payload and outbox commit boundaries. The conversation JSON document introduced
in ADR0013 is replaced by indexed message rows and separate source-receipt rows.
Session metadata and summaries remain small. Model context and UI windows are
bounded reads of durable history, rather than retention rules that delete older
messages. Previously pruned content cannot be reconstructed by migration.

Schema upgrades retain the released v18 floor. The v30 migration and session
normalization preserve retained data and source fingerprints, are safe to retry,
and never mark data as migrated before its transaction commits. Rewind removes
visible tail rows but retains idempotency receipts. Cursor pages bind the session
and history epoch; appends preserve earlier-page navigation, while rewind makes
an obsolete cursor fail explicitly. Message/content search uses SQLite FTS5 as a
projection maintained with message writes. It does not grant model access to the
profile database or change tool authority.

The existing Backend binary also provides an explicit maintenance command mode;
it does not become a second model execution service. Online backup uses SQLite's
backup API and includes the existing opaque profile key and a digest manifest.
Restore/compact/prune require the database-owner lease also held by a normal
Host. Restore validates and stages the bundle, keeps a safety backup and records
a recovery journal for DB/key replacement. An incomplete operation is recovered
or refused before normal database use, not hidden by a fresh empty database.
DPAPI-bound backups have the documented same-user/machine recovery constraint;
Credential Manager secrets are not exported.

Automatic conversation/run deletion is disabled. Pruning is a user operation
with a dry-run default and an explicit apply option; active sessions and pending
outbox delivery remain protected. Page reclamation is explicit offline maintenance,
not a request-path full VACUUM. Status reports bounded metadata and sizes without
running an unbounded integrity scan on every request. Backup verification and
restore drills are separate from ordinary startup checks.

Alternatives considered: retaining JSON/session leaves whole-document writes and
history truncation; JSONL plus multiple derived databases adds a second storage
format and cross-store consistency work; replacing SQLite introduces a service
and migration without evidence it solves the measured query and locking issues.
The chosen change keeps aggregate atomicity local, makes history queries
indexable, and preserves explicit recovery rather than inferring success.

ADR0013 remains effective for SQLite ownership, one-time legacy imports and the
credential boundary. This ADR supersedes only its whole-session JSON storage
choice. Current interfaces, limits and lifecycle instructions belong in the
Harness and runtime operations pages.
