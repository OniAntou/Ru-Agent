# Ru Logging Closure Fixes Design

**Status:** Historical implementation record. The closure fixes shipped on
2026-08-20; use [`docs/operations/logging.md`](../../operations/logging.md)
for the current logging contract.

## Goal

Close the verified gaps in the logging upgrade without changing chat, voice,
STT, VTS, observation, memory, or public-route behavior. Diagnostic writes must
be safe and non-blocking; Activity Timeline writes must remain best effort and
owned by ASP.NET Core.

## Design

### One safe emitter per process

ASP.NET Core receives a small `IDiagnosticEmitter` facade over the existing
bounded writer. Endpoint failure/completion seams use event codes and scalar
fields only. Python installs a logging handler that ignores formatted messages,
arguments, and tracebacks, persisting only an allowlisted event code and scalar
fields. WinUI uses a bounded GUI writer under the resolved `RU_LOG_ROOT`, with
the same serializer policy. Existing console/bootstrap output remains a
fallback and is not read by the Diagnostics API.

Every emitter obtains the ambient correlation ID, sanitizes it against the
opaque-ID pattern, and performs only a bounded queue enqueue on the operation
path. Disk writes, rotation, retention, and retry work remain in background
workers.

### Activity reliability and privacy

The worker outbox reports drops and write failures through safe counters and
keeps failed batches available for retry. ASP.NET ingestion continues to use
atomic file claiming. `ActivityLogStore` sanitizes both writes and reads, while
the existing public activity contract remains unchanged. Chat activity is
submitted to a bounded background path and is never awaited before the chat
response is delivered.

### Diagnostics API and GUI

Diagnostic query values have explicit length, cursor, record, byte, and export
limits. Invalid `since`/cursor values return stable public 400 errors. Export
returns bounded NDJSON bytes rather than constructing an unbounded response
string.
The GUI applies component, event-code, level, and correlation filters to both
the list and export, and shows a warning when status or log requests fail.

### Benchmark and documentation

The benchmark redirects both activity and diagnostic writers to a temporary
root, reports p50/p95/p99, and gates both p95 and p99 against 2% and 2 ms. It
uses injectable operation seams rather than claiming that a synthetic loop is
the production C# ActivityLogStore path. Documentation records automated
evidence separately from user-run desktop/audio/VTS acceptance.

## Boundaries

- No FastAPI or alternate public backend is reintroduced.
- No raw transcript, prompt, response, credential, path, screenshot, audio,
  VTS private code, exception message, or stack trace is persisted.
- No broad persona, generation, VTS, or observation behavior refactor is part
  of this closure work.
- Existing unrelated dirty-worktree changes remain untouched and nothing is
  staged or committed by this task.

## Acceptance evidence

1. New regression tests cover emitter sanitization, writer failure/drop
   counters, activity read re-sanitization, API bounds/export, GUI filter/export
   propagation, and benchmark isolation.
2. Backend and GUI test projects, the full Python suite, route contract checks,
   Release publishes, and the isolated benchmark pass sequentially.
3. Source scans show no raw GUI crash file, no diagnostic writer bypass, and no
   production diagnostic handler that persists formatted log messages.
4. A manual desktop/audio/VTS smoke check remains explicitly user-owned and is
   not claimed as automated evidence.
