---
status: accepted
date: 2026-08-20
---

# Structured diagnostics and Activity Timeline boundaries

Ru keeps ASP.NET Core as the only public backend and the owner of canonical
Activity Timeline persistence. The managed Host/Core path publishes sanitized
activity transitions through the durable conversation outbox; its hosted
ingestion claims and persists them with event-ID de-duplication. The retired
Python Worker is not a production activity publisher. Activity reads are
sanitized again so legacy files cannot return raw chat content.

Amendment (2026-10-09): Activity Timeline records now use the shared SQLite
store, with persistent event-ID deduplication; the conversation-outbox projection
remains. Legacy file outboxes are imported once and their poller is removed
([ADR 0013](0013-consolidate-business-persistence-in-sqlite.md)).

Current bounded JSONL diagnostics are owned by ASP.NET Core
(`StructuredDiagnosticLogWriter`) and WinUI (`GuiDiagnosticWriter`). The
retired Python Worker is not a current diagnostic producer; the managed
Worker's private protocol errors are not a replacement Python JSONL stream.
The operation path performs only safe
field construction and queue enqueue; file I/O, batching, retention, and
retry run in background workers. A diagnostic emitter and HTTP boundary use
fixed event codes, durations, statuses, opaque correlation IDs, and safe error
categories. Free-form messages, exception text, stack traces, request bodies,
credentials, paths, and private marker metadata are never
serialized.

The authenticated diagnostics API validates limits, filters, timestamps, and
cursors, skips oversized records, and caps NDJSON exports. The GUI reads the
API rather than local files and writes only bounded `gui_crash` events under
the resolved `RU_LOG_ROOT`. Latency acceptance uses an isolated temporary-root
benchmark with p50/p95/p99 reporting and independent 2 ms/2% p95 and p99
gates. Live provider smoke acceptance
remains user-owned. Diagnostics API requests are excluded from HTTP boundary
events so polling cannot create a self-referential stream or force needless
diagnostics list refreshes.
