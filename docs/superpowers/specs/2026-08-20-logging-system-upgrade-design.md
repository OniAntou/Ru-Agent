# Ru logging system upgrade

**Status:** Historical implementation record. The logging design was implemented
and closed by the 2026-08-20 logging plans; use
[`docs/operations/logging.md`](../../operations/logging.md) for the current
contract.

**Date:** 2026-08-20

## Goal

Upgrade Ru's two related but distinct logging surfaces without increasing the
latency of chat, streaming voice, STT, VTS, or screen observation:

1. The user-facing Activity Timeline becomes useful while no longer storing
   full chat transcript content.
2. Diagnostic logging becomes structured, searchable, correlated across the
   WinUI app, ASP.NET Core Public Host, and Python Runtime Worker, and useful
   during local troubleshooting.

The implementation must preserve the current public backend boundary: ASP.NET
Core remains the only public backend, while Python remains a private runtime
implementation boundary.

## Approved decisions

- Upgrade both Activity Timeline and technical diagnostic logging.
- Keep Activity Timeline content as a bounded, redacted preview rather than a
  full transcript.
- Provide diagnostic logs both as local files and through a Diagnostics view in
  Ru.
- Use one structured JSONL format per process rather than centralizing every
  diagnostic write through ASP.NET Core or storing diagnostics in SQLite.
- Store diagnostic files under `%LOCALAPPDATA%\\Ru\\logs`; use `RU_LOG_ROOT`
  for test/staging isolation.
- Rotate diagnostic files for a maximum of seven days or 100 MB.
- Generate and propagate a `correlation_id` for every operation.
- Do not perform synchronous disk or network I/O on a user-operation hot path.
- ASP.NET Core is the sole canonical writer for the Activity Timeline. Worker
  activity events use a bounded local outbox and are ingested asynchronously.

## Current context and problems

The current implementation has several useful pieces but they are not one
coherent system:

- `backend-dotnet/RuBackend/Business/Activity/ActivityLogStore.cs` owns the
  public activity-log read/clear contract and writes JSONL with a 5,000-entry
  bound and key-fragment sanitization.
- `backend-dotnet/RuBackend/Conversation/ConversationCoordinator.cs` appends
  user and assistant activity after the response path has completed.
- `src/ru_runtime/persistence/activity_log.py` and worker modules also write
  activity events, so two processes can currently touch the canonical activity
  file without one cross-process ownership protocol.
- `src/ru_runtime/__init__.py` configures Python logging as plain message-only
  output.
- ASP.NET Core uses `ILogger`, while `gui-winui3/Services/BackendLaunchPlan.cs`
  redirects process stdout/stderr into project-root `logs` files.
- `gui-winui3/Views/LogView.xaml` and `LogView.xaml.cs` provide an Activity
  Timeline with type filters, periodic refresh, expandable details, and clear.

The upgrade must eliminate full chat content from the activity log, avoid
cross-process append/trim races, move durable diagnostic data out of the
repository checkout, and make failures in logging non-fatal.

## Scope

### In scope

- A shared diagnostic event schema and redaction policy.
- Structured per-process JSONL writers for ASP.NET Core, Runtime Worker, and
  GUI.
- Bounded asynchronous queues, batching, rotation, retention, and drop
  counters.
- Correlation ID propagation across GUI, Public Host, Worker Contract, and
  worker operation logs.
- Activity preview generation and one-time migration of existing activity
  entries.
- Worker activity outbox plus asynchronous ASP.NET ingestion with de-duplication.
- Diagnostic API routes, bounded filtering, safe export, and status counters.
- Activity/Diagnostics GUI experience and incremental refresh.
- Unit, integration, privacy, protocol, migration, and latency verification.
- Updates to the route contract, runtime behavior documentation, README, and
  relevant architecture/ADR documentation.

### Out of scope

- A remote telemetry service or cloud log upload.
- Full-text transcript search in Activity Timeline or Diagnostics.
- Persistent raw stack traces, request bodies, audio, screenshots, model
  responses, credentials, or VTS private codes.
- Replacing the existing conversation history/session store.
- Introducing a third-party logging framework solely for this feature.
- Changing model, voice, STT, VTS, or observation behavior beyond the logging
  seams needed to emit safe events.

## Architecture

### 1. Activity Timeline pipeline

Activity Timeline remains a protected business record and continues to be
served by `/activity-log`. Its canonical file remains the activity JSONL file
under the resolved business data root.

ASP.NET Core is the only component allowed to append to that canonical file.
The public host owns validation, sanitization, bounded retention, de-duplication,
and API reads/clear operations.

The Runtime Worker does not append directly to the canonical activity file. It
publishes safe activity records to a bounded in-process queue and a worker
activity outbox writer. The outbox is a separate JSONL file under the configured
log root. A hosted ASP.NET ingestion service periodically drains complete
outbox records into `IActivityLogStore` and records the event ID as consumed.
Ingestion is best-effort and retryable; it is never awaited by chat, voice, STT,
VTS, or observation operations.

Each activity event receives an `event_id`. The canonical store ignores an
event that has already been ingested. Incomplete final lines and malformed
records are skipped without discarding the rest of the outbox.

### 2. Diagnostic pipeline

Each process owns its own diagnostic writer and writes structured JSONL files:

- `aspnetcore-YYYY-MM-DD.jsonl`
- `worker-YYYY-MM-DD.jsonl`
- `gui-YYYY-MM-DD.jsonl`

The files share a common directory and schema but are not written through a
shared synchronous service. This keeps process boundaries independent and
prevents a worker or GUI logging failure from blocking the Public Host.

The Public Host reads the bounded tail of these files for Diagnostics API
requests. The API merges records by timestamp/event ID, applies the same
redaction policy again, and enforces all query bounds. The GUI never opens the
files directly.

Existing stdout/stderr redirection remains only as a bootstrap/crash fallback
while structured writers become the supported diagnostic source. It must not be
treated as an API or export source, and its paths must move under the canonical
log root rather than the repository checkout.

### 3. Correlation

The Public Host creates or validates an opaque correlation ID at every public
operation boundary. It is carried through:

`RuApp -> ASP.NET Core -> Worker Contract -> Runtime Worker -> child operation`

The ID is not derived from a session ID, user text, credential, or machine
identity. It is safe to display and search in Diagnostics. ASP.NET uses an
`ILogger` scope/diagnostic context; Python uses a context-local value so all
records emitted during one operation carry the same ID.

## Event schemas

### Activity event

The existing wire-compatible envelope remains:

```json
{
  "event_id": "opaque-id",
  "ts": 1720000000.123,
  "iso": "2026-08-20T00:00:00.123Z",
  "type": "chat",
  "session_id": "protected-business-link-or-null",
  "data": {}
}
```

The `data` object is type-specific and allowlisted:

- `chat`: `role`, `preview`, `original_length`, `redacted`.
- `emotion`: bounded emotion label and status.
- `vts_action`: action category and success/status only; no private catalog
  codes, marker strings, or raw action payload.
- `observation`: status, bounded counters, and latency only; no screenshot,
  model reaction text, or private observation payload.

Chat preview generation normalizes whitespace, removes private control
metadata, masks secrets/URLs/paths where applicable, and truncates to 160
characters. The original content is never written to the activity record.
Session linkage may remain in this protected business record for the existing
timeline contract, but it is not copied into diagnostic records.

### Diagnostic event

Persistent diagnostic records contain only allowlisted structured fields:

```json
{
  "event_id": "opaque-id",
  "ts": 1720000000.123,
  "iso": "2026-08-20T00:00:00.123Z",
  "level": "warning",
  "component": "worker.voice",
  "event_code": "voice_unavailable",
  "correlation_id": "opaque-id",
  "duration_ms": 42.5,
  "status": "failed",
  "error_code": "worker_unavailable",
  "exception_kind": "timeout"
}
```

`message` is not a free-form persistent field. Event codes, statuses,
exception kinds, and component names come from bounded allowlists. Diagnostic
records must not contain credentials, bearer values, request bodies, user text,
transcripts, session IDs, raw model responses, audio, screenshots, VTS codes,
or arbitrary exception text/stack traces.

## Performance contract

The following are hard implementation constraints:

1. A log call may allocate a small event and perform a non-blocking queue write,
   but it may not await disk, acquire a cross-process file lock, call the
   network, or perform rotation on the user-operation path.
2. Writers use bounded queues and background tasks. Writes are batched and
   flushed periodically rather than using per-event `WriteThrough` semantics.
3. Queue overflow drops the lowest-severity records first and increments a
   process-local drop counter. It never waits for disk space.
4. High-frequency loops such as VTS sway use sampling/throttling and do not
   emit one persistent record per frame.
5. Activity append and worker outbox ingestion remain best-effort and cannot
   change a successful user response into a failure.
6. Rotation, retention cleanup, API export, and diagnostics file scans execute
   off the request/voice/worker hot path.
7. Shutdown flushes for a bounded duration and must not hold application exit
   indefinitely.

The acceptance gate compares logging enabled at the production level with a
logging-disabled baseline across chat streaming, voice, STT, VTS, and
observation. Each comparison uses the same warmed environment and at least 30
paired steady-state samples. Any synchronous file I/O in a hot-path span fails
the gate. The logging-enabled p95 and p99 must each remain within both 2% and
2 ms of the baseline; otherwise the benchmark is repeated and the regression
must be fixed before acceptance. “Logging enabled” is not accepted merely
because the functional tests pass.

## Retention and storage

- Diagnostic files rotate by UTC date and are removed when older than seven
  days or when the aggregate diagnostic budget exceeds 100 MB.
- Cleanup is performed by a background maintenance service and is safe when a
  file is missing, locked, or partially written.
- `RU_LOG_ROOT` overrides the canonical location for tests and staging.
- Business activity retention remains capped at 5,000 events.
- No raw activity-log backup is kept after migration because it would preserve
  the transcript data that the new policy explicitly removes.

## API design

Existing routes remain compatible:

- `GET /activity-log?limit=...`
- `DELETE /activity-log`

New authenticated public routes:

- `GET /diagnostics/logs?limit=...&since=...&level=...&component=...&correlation_id=...`
  returns bounded, redacted records plus a cursor for incremental reads.
- `GET /diagnostics/export?...` returns the currently filtered safe JSONL
  records as a download.
- `GET /diagnostics/status` returns writer queue depth, dropped-event counts,
  write failures, current file sizes, and retention status without exposing
  private data.

All query values are bounded. The API reads only the configured log directory,
skips malformed lines, merges per-process streams deterministically, and
re-sanitizes records before serialization. No API exposes raw stdout/stderr
files or arbitrary filesystem paths.

The route inventory and compatibility tests must be updated with the new
diagnostic routes.

## GUI design

The current Log page becomes a two-surface experience while preserving the
existing Activity Timeline:

### Activity

- Existing Chat, Emotion, and VTS filters remain.
- Rows display safe preview text rather than full transcript content.
- Expanded details show only the allowlisted event fields.
- Clear remains an explicit confirmed action.

### Diagnostics

- Realtime incremental refresh using the API cursor; never reloads every file.
- Filters for level and component.
- Search by event code and correlation ID.
- Pause/resume auto-refresh.
- Export the current filtered result through the backend.
- Show a bounded warning when events were dropped or a writer is unavailable.

Client-side display is defense in depth only; the backend remains responsible
for redaction and bounds.

## Failure behavior

- An unavailable log directory does not prevent Ru startup.
- A failed diagnostic write increments a safe counter and emits at most a
  throttled internal warning; it never recursively logs the same failure.
- A full queue drops according to severity policy and exposes the drop count
  through `/diagnostics/status`.
- A malformed JSONL line is ignored; valid neighboring records remain readable.
- An ingestion failure leaves the worker outbox available for retry.
- Migration writes a temporary file and atomically replaces the canonical
  activity file only after the transformed file is complete. On failure, the
  original remains untouched.
- Diagnostics export failure returns a public error code without revealing a
  filesystem path or exception message.

## Migration and rollout

1. Add schema/redaction and writer tests before changing runtime behavior.
2. Add the diagnostic writers and route/API contracts behind the existing
   ASP.NET Core process boundary.
3. Introduce activity preview transformation and migrate old activity events
   atomically.
4. Move worker activity writes to the bounded outbox and start host ingestion.
5. Move process log roots from the repository checkout to canonical AppData,
   while retaining bounded bootstrap fallback files during the transition.
6. Add the GUI Diagnostics surface and preserve Activity route compatibility.
7. Run all automated suites, benchmark logging on/off, and perform the user's
   live desktop/audio/VTS acceptance separately from automated evidence.

No old FastAPI public fallback or alternate backend mode is reintroduced.

## Verification plan

### Unit tests

- Activity preview length, normalization, secret/URL/path/private-marker
  redaction, and event schema.
- Diagnostic allowlist and recursive redaction.
- Correlation context propagation in both runtimes.
- Queue capacity, severity drop ordering, drop counters, and bounded shutdown.
- JSONL rotation, retention, malformed-line handling, and atomic trim.
- Activity event ID de-duplication and worker outbox retry.
- Existing activity route response compatibility.

### Integration tests

- ASP.NET diagnostic routes, filters, cursors, export, status, and auth.
- Worker-to-host activity outbox ingestion.
- Cross-process log directory resolution under `RU_LOG_ROOT`.
- GUI API model parsing and incremental refresh behavior.
- Migration of a legacy activity file containing full chat content.
- Privacy scans proving forbidden fields do not reach activity or diagnostic
  output.

### Performance tests

- Chat streaming with logging disabled versus production-level logging.
- Voice start/stream/acknowledgement path.
- STT request path.
- VTS activity/action path and high-frequency sway sampling.
- Observation capture/reaction/ack path.
- Queue saturation and log-directory-unavailable scenarios.

Heavyweight repository gates must run sequentially because the project can
exhaust Windows pagefile capacity when Python, .NET, frontend, and build checks
run concurrently.

## Documentation updates

After implementation, update and cross-check:

- `README.md` for log locations, Diagnostics usage, and privacy boundaries.
- `docs/operations/runtime-behavior.md` for activity/diagnostic invariants and
  latency acceptance.
- `backend-dotnet/public-route-contract.json` for diagnostic routes.
- `docs/architecture.md` and a focused ADR if the ownership/outbox boundary
  becomes a lasting architecture decision.
- Verification documentation with automated benchmark results and the
  remaining user-run live acceptance boundaries.

## Implementation guidance

The implementation plan should preserve these concrete constraints:

- Prefer the platform logging abstractions and a small in-repository JSONL
  provider/handler over adding a third-party logging package.
- Keep event creation helpers centralized so arbitrary interpolated log
  messages cannot bypass the allowlist.
- Use a stable cursor/event ID strategy that handles file rotation without
  duplicate GUI rows.
- Keep privacy tests close to the serializers and make them fail closed when a
  new field is added without an allowlist entry.
