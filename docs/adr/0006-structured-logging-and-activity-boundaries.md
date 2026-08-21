---
status: accepted
date: 2026-08-20
---

# Structured diagnostics and Activity Timeline boundaries

Ru keeps ASP.NET Core as the only public backend and the owner of canonical
Activity Timeline persistence. The Python worker publishes sanitized activity
events to a bounded outbox; ASP.NET claims and ingests that outbox
asynchronously with event-ID de-duplication. Activity reads are sanitized
again so legacy files cannot return raw chat content.

Technical diagnostics are independent bounded JSONL writers for ASP.NET Core,
the private Python worker, and WinUI. The operation path performs only safe
field construction and queue enqueue; file I/O, batching, retention, and
retry run in background workers. A diagnostic emitter and HTTP boundary use
fixed event codes, durations, statuses, opaque correlation IDs, and safe error
categories. Free-form messages, exception text, stack traces, request bodies,
audio, transcripts, credentials, paths, and private VTS metadata are never
serialized.

The authenticated diagnostics API validates limits, filters, timestamps, and
cursors, skips oversized records, and caps NDJSON exports. The GUI reads the
API rather than local files and writes only bounded `gui_crash` events under
the resolved `RU_LOG_ROOT`. Latency acceptance uses an isolated temporary-root
benchmark with p50/p95/p99 reporting and independent 2 ms/2% p95 and p99
gates. Live desktop, microphone, speaker, and VTube Studio smoke acceptance
remains user-owned. Diagnostics API requests are excluded from HTTP boundary
events so polling cannot create a self-referential stream or force needless
diagnostics list refreshes.
