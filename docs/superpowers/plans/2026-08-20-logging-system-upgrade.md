# Ru Logging System Upgrade Implementation Plan

> Historical implementation record. Its completed checkboxes describe the
> earlier upgrade pass; closure fixes and current verification evidence are in
> `docs/superpowers/plans/2026-08-20-logging-closure-fixes.md`.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Upgrade Ru's Activity Timeline and diagnostic logging so that activity records are privacy-safe, diagnostic records are structured and queryable, process boundaries share correlation IDs, and logging adds no measurable latency regression to chat, voice, STT, VTS, or observation hot paths.

**Architecture:** ASP.NET Core remains the only canonical Activity Timeline writer. The Python worker publishes safe activity events to a bounded local outbox, and an ASP.NET hosted service ingests them asynchronously with event-ID de-duplication. ASP.NET Core, Python worker, and WinUI each own an independent bounded structured-JSONL diagnostic writer under the configured AppData log root. The ASP.NET host reads and merges diagnostic files only for bounded API requests; the GUI never opens log files directly.

**Tech Stack:** ASP.NET Core/.NET 10, C# xUnit integration tests, Python 3/pytest, WinUI 3, JSONL, `System.Text.Json`, Python `contextvars`, bounded in-process queues, and existing Worker Contract messages. No third-party logging package and no FastAPI fallback are introduced.

**Execution note:** The repository owner explicitly requested that sub-agents remain disabled. Execute this plan inline, one task at a time, with the verification checkpoints below.

---

## Scope and invariants

- Preserve the existing `GET /activity-log` and `DELETE /activity-log` contracts.
- Keep the Activity Timeline as a protected business record, but replace chat bodies with a normalized, masked preview of at most 160 characters.
- Never persist request bodies, user text, transcripts, credentials, bearer values, raw model responses, audio, screenshots, session IDs in diagnostics, or private VTS codes/markers.
- Store diagnostics under `%LOCALAPPDATA%\Ru\logs` by default, overridable with `RU_LOG_ROOT`; move bootstrap stdout/stderr files under that root as well.
- Rotate diagnostic JSONL by UTC date, retain at most seven days and 100 MB in aggregate, and keep business activity retention capped at 5,000 events.
- Use bounded queues and background writers. A hot-path log call may allocate a small event and perform a non-blocking enqueue only; it may not await disk, perform rotation, use cross-process locks, call the network, or scan files.
- Propagate an opaque `correlation_id` through `RuApp -> ASP.NET Core -> Worker Contract -> Python worker -> child operation`.
- Treat log failures as best effort: user operations continue, failures increment safe counters, and queue overflow drops low-severity records first.
- Keep unrelated dirty-worktree files untouched and stage only files belonging to this logging upgrade at each checkpoint.

## File map

Create or modify the following groups of files. Exact existing symbols may be adjusted during implementation, but no task may silently move the canonical business data root or reintroduce a second public backend.

- ASP.NET Core: `backend-dotnet/RuBackend/Business/DataPaths.cs`, `backend-dotnet/RuBackend/Business/Activity/*`, `backend-dotnet/RuBackend/Diagnostics/*`, `backend-dotnet/RuBackend/Worker/*`, `backend-dotnet/RuBackend/Conversation/*`, `backend-dotnet/RuBackend/Program.cs`, and the related `backend-dotnet/RuBackend.Tests/*` files.
- Python worker: `src/ru_runtime/persistence/activity_log.py`, new `src/ru_runtime/persistence/activity_outbox.py`, new `src/ru_runtime/persistence/diagnostic_log.py`, new `src/ru_runtime/persistence/diagnostic_schema.py`, `src/ru_runtime/worker/v1/*`, and high-value event call sites under `src/ru_runtime/voice`, `src/ru_runtime/vts`, `src/ru_runtime/observation`, `src/ru_runtime/runtime*`, and `src/ru_runtime/generation`.
- WinUI: `gui-winui3/Services/BackendLaunchPlan.cs`, `gui-winui3/Services/ApiService.cs`, `gui-winui3/Services/ChatWebSocketClient.cs`, `gui-winui3/Services/VoiceStreamClient.cs`, `gui-winui3/Models/Models.cs`, `gui-winui3/Views/LogView.xaml`, `gui-winui3/Views/LogView.xaml.cs`, `gui-winui3/App.xaml.cs`, and focused files under `gui-winui3.Tests/*`.
- Contracts/docs: `backend-dotnet/public-route-contract.json`, `README.md`, `docs/operations/runtime-behavior.md`, `docs/operations/logging.md`, and a benchmark/verification note under `docs/verification/`.

## Task 1: Establish canonical log roots and bootstrap fallback paths

### Tests first

- [x] Add `backend-dotnet/RuBackend.Tests/Business/DataPathsTests.cs` cases for the default `%LOCALAPPDATA%\Ru\logs` root, an explicit `RU_LOG_ROOT` override, and the derived `bootstrap` directory. Assert that the business activity file remains under the resolved business data root and is not moved into diagnostics.
- [x] Extend `gui-winui3.Tests/BackendLaunchPlanTests.cs` to assert that child processes receive `RU_LOG_ROOT`, that stdout/stderr fallback files are under `<logRoot>\bootstrap`, and that no path under `<projectRoot>\logs` is selected.
- [x] Add a startup configuration test proving an unwritable/missing diagnostic directory does not make `DataPaths` resolution or backend startup fail.

### Implementation

- [x] Extend `DataPaths` with `LogRoot` and `BootstrapLogRoot` while preserving all existing positional/constructor call sites or providing an additive compatibility constructor. Resolve `RU_LOG_ROOT` as the highest-priority diagnostic-root override, then use `%LOCALAPPDATA%\Ru\logs`; never infer the diagnostic root from the repository checkout.
- [x] Register the resolved paths once in `Program.cs` and pass the same root to ASP.NET diagnostic writers, activity-outbox ingestion, retention maintenance, and test fixtures.
- [x] Update `BackendLaunchPlan` and the backend process manager to create/use the canonical root, add `RU_LOG_ROOT` to child environments, and place bounded bootstrap output files under `bootstrap`. Keep bootstrap redirection as a crash/startup fallback only.
- [x] Add a safe directory-creation helper that tolerates `IOException`, `UnauthorizedAccessException`, and locked/missing paths without writing an exception path or recursively retrying.

### Verification

- [x] Run `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --filter "FullyQualifiedName~DataPathsTests|FullyQualifiedName~BackendLaunchPlan"`.
- [x] Run `dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --filter "FullyQualifiedName~BackendLaunchPlanTests"`.
- [x] Inspect the resolved test paths and confirm no generated log file appears in the repository checkout.

## Task 2: Add shared safe event schemas, redaction, and correlation context primitives

### Tests first

- [x] Add C# tests for diagnostic serialization that accept only the documented fields: `event_id`, `ts`, `iso`, `level`, `component`, `event_code`, `correlation_id`, `duration_ms`, `status`, `error_code`, and `exception_kind`, plus explicitly bounded numeric status counters where required by the status contract.
- [x] Add C# tests proving unknown fields, arbitrary `message`, exception text, stack traces, credentials, bearer values, URLs, filesystem paths, request bodies, session IDs, VTS codes, and model text are omitted or replaced with bounded safe values.
- [x] Add Python tests in `tests/test_diagnostic_schema.py` for the same allowlist, recursive redaction, maximum lengths, bounded enum values, and fail-closed behavior when a new field is supplied.
- [x] Add correlation tests proving valid opaque IDs are preserved, invalid/oversized IDs are replaced, and IDs are never derived from session IDs, prompts, credentials, or machine identity.

### Implementation

- [x] Create `backend-dotnet/RuBackend/Diagnostics/DiagnosticEvent.cs` as a typed, immutable event model with no free-form persistent message field.
- [x] Create `backend-dotnet/RuBackend/Diagnostics/DiagnosticEventPolicy.cs` and a centralized sanitizer/serializer. Use bounded allowlists for levels, components, event codes, statuses, error codes, and exception kinds; emit only known properties and omit nulls.
- [x] Create `backend-dotnet/RuBackend/Diagnostics/CorrelationContext.cs` using an async-flow-safe context. Add generation/validation helpers that produce opaque IDs suitable for display and search.
- [x] Create Python counterparts in `src/ru_runtime/persistence/diagnostic_schema.py` and `src/ru_runtime/persistence/diagnostic_context.py`, using `contextvars` for correlation scope and a single fail-closed serializer.
- [x] Add shared activity-safe preview helpers with an explicit boundary between protected activity data and diagnostics. Keep chat preview transformation in the activity pipeline, not in diagnostic records.

### Verification

- [x] Run `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --filter "FullyQualifiedName~DiagnosticEvent|FullyQualifiedName~Correlation"`.
- [x] Run `python -m pytest -q tests/test_diagnostic_schema.py tests/test_correlation_context.py`.
- [x] Run repository privacy scans against serialized test fixtures and assert forbidden values never occur in output JSONL.

## Task 3: Implement bounded asynchronous JSONL writers, readers, rotation, and retention

### Tests first

- [x] Add C# tests for a bounded queue with non-blocking `TryEnqueue`, severity-aware overflow, dropped-event counters, and bounded shutdown flush.
- [x] Add C# tests for batch size 64, periodic flush at or below 250 ms, no `WriteThrough` file option, malformed-line skipping, deterministic merge ordering, and cursor continuity across daily file rotation.
- [x] Add C# tests for seven-day retention and 100 MB aggregate cleanup, including missing files, locked files, partial final lines, and an unavailable log directory.
- [x] Add Python tests in `tests/test_diagnostic_log.py` for the same queue/drop/flush/rotation/failure behavior and for the worker outbox writer.

### Implementation

- [x] Create `backend-dotnet/RuBackend/Diagnostics/BoundedDiagnosticQueue.cs`, `StructuredDiagnosticLogWriter.cs`, `DiagnosticLogReader.cs`, `DiagnosticRetentionService.cs`, and `DiagnosticStatusSnapshot.cs`.
- [x] Use a process-local bounded queue and a background hosted writer. The enqueue path must not await, block on disk, acquire a cross-process lock, or run retention/rotation. Batch up to 64 events and flush periodically; use asynchronous file I/O without per-event `WriteThrough`.
- [x] Define a stable cursor containing file date plus event ID/sequence so incremental readers do not duplicate rows after rotation. Merge `aspnetcore-*`, `worker-*`, and `gui-*` deterministically by timestamp then event ID.
- [x] Make write failures and drops observable only as safe counters. Throttle any internal warning and prevent recursive logging of a logging failure.
- [x] Run retention in a separate maintenance service. Enforce both age and aggregate-size limits, tolerate locks/missing files, and never expose filesystem paths through status or errors.
- [x] Keep the reader bounded: scan only the configured log root, cap bytes/records per request, skip malformed and incomplete lines, re-sanitize every record, and never read bootstrap stdout/stderr as diagnostic data.

### Verification

- [x] Run `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --filter "FullyQualifiedName~DiagnosticLogWriter|FullyQualifiedName~DiagnosticLogReader|FullyQualifiedName~DiagnosticRetention"`.
- [x] Run `python -m pytest -q tests/test_diagnostic_log.py`.
- [x] Confirm with a targeted code search that no new hot-path writer uses `WriteThrough`, synchronous file append, or an unbounded queue.

## Task 4: Move Python diagnostics and activity writes behind bounded worker-local pipelines

### Tests first

- [x] Update `tests/test_activity_log.py` to assert that chat events contain `preview`, `original_length`, and `redacted`, never raw `content`, and that emotion/VTS/observation payloads contain only their documented safe fields.
- [x] Add `tests/test_activity_outbox.py` covering event IDs, bounded queue overflow, complete-line reads, malformed-line recovery, retry after ingestion failure, and preservation of valid neighboring records.
- [x] Extend `tests/test_diagnostic_log.py` to assert file names `worker-YYYY-MM-DD.jsonl` and the separate worker activity-outbox file under `RU_LOG_ROOT`.

### Implementation

- [x] Create `src/ru_runtime/persistence/diagnostic_log.py` with a bounded, non-blocking writer thread/loop, severity drop policy, batch flush, UTC rotation, safe counters, and bounded shutdown.
- [x] Create `src/ru_runtime/persistence/activity_outbox.py` with the same non-blocking discipline, event-ID assignment, safe activity serialization, and a separate JSONL outbox under `RU_LOG_ROOT`.
- [x] Refactor `src/ru_runtime/persistence/activity_log.py` into a compatibility facade that publishes to the outbox instead of opening the canonical `activity_log.jsonl`. Remove direct canonical writes from `src/ru_runtime/worker/v1/vts.py`, `observation.py`, and every other Python activity call site.
- [x] Install the Python diagnostic context at worker request boundaries and make the worker diagnostic file the only persistent source for Python technical events.
- [x] Keep existing runtime/voice/VTS/observation behavior unchanged except for replacing unsafe log/activity persistence with the new event helpers.

### Verification

- [x] Run `python -m pytest -q tests/test_activity_log.py tests/test_activity_outbox.py tests/test_diagnostic_log.py tests/test_observation_bounds.py`.
- [x] Start the worker with a temporary `RU_LOG_ROOT`, emit chat/emotion/VTS/observation events, and assert that only the outbox and worker diagnostic files change; the canonical activity file must remain untouched until host ingestion.
- [x] Run a privacy scan over all generated worker files and assert that no prompt, transcript, raw VTS code, screenshot, audio, credential, or exception text is present.

## Task 5: Make ASP.NET Core the sole canonical Activity Timeline writer

### Tests first

- [x] Update `backend-dotnet/RuBackend.Tests/Business/ActivityLogStoreTests.cs` for event IDs, duplicate suppression, 5,000-entry retention, safe allowlisted data, malformed-line handling, and non-blocking enqueue semantics.
- [x] Add `ActivityPreviewTests.cs` covering whitespace normalization, private control metadata removal, secret/URL/path masking, 160-character truncation, `original_length`, and `redacted` status.
- [x] Add `ActivityLogMigrationTests.cs` with a legacy JSONL containing full chat content. Assert atomic replacement removes `content`, preserves valid neighboring events, adds event IDs, and leaves the original untouched when transformation fails.
- [x] Add `ActivityOutboxIngestionTests.cs` covering complete records, malformed records, duplicate IDs, retry after a canonical-store failure, and a worker outbox that remains available while ingestion is down.
- [x] Add/adjust route tests to prove the existing activity response shape and clear behavior remain compatible.

### Implementation

- [x] Extend `ActivityEvent` with an additive `event_id` field while preserving existing test/helper constructors and accepting legacy records without an ID only during migration/read compatibility.
- [x] Create `backend-dotnet/RuBackend/Business/Activity/ActivityPreview.cs` and centralize chat/emotion/VTS/observation allowlists there. The host must re-sanitize every event from its own coordinator and from the worker outbox before canonical append.
- [x] Refactor `ActivityLogStore` so the user-operation path calls a bounded non-blocking enqueue method. Background persistence performs JSONL append, trim, and de-duplication; direct `WriteThrough` append from chat/voice/STT/VTS/observation is removed.
- [x] Create `ActivityLogMigrationService` that transforms old records through a temporary file, flushes/closes it, atomically replaces the canonical file only after success, and keeps no raw backup. Make the operation idempotent and safe when the file is missing.
- [x] Create `ActivityOutboxIngestionService` as an ASP.NET hosted service. Drain complete worker outbox lines asynchronously, skip malformed/incomplete lines, retry failures, and feed only sanitized records into the canonical store. Ingestion must never be awaited by chat, voice, STT, VTS, or observation operations.
- [x] Update `ConversationCoordinator` and all ASP.NET activity producers to emit safe previews and use the non-blocking sink. Ensure a logging/activity failure cannot turn a successful response into an error.

### Verification

- [x] Run `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --filter "FullyQualifiedName~ActivityLogStore|FullyQualifiedName~ActivityPreview|FullyQualifiedName~ActivityLogMigration|FullyQualifiedName~ActivityOutboxIngestion|FullyQualifiedName~ActivityLogEndpoint"`.
- [x] Run an integration fixture with concurrent host and worker producers and assert one canonical record per event ID, no cross-process append/trim race, and a stable 5,000-event cap.
- [x] Inspect a migrated activity file and confirm no raw chat body remains anywhere in the file or in a backup path.

## Task 6: Propagate correlation IDs across GUI, ASP.NET Core, Worker Contract, and Python

### Tests first

- [x] Add C# tests for correlation generation/validation at REST, WebSocket, voice, STT, VTS, and observation boundaries, including missing, malformed, oversized, and duplicate caller values.
- [x] Extend Worker Contract tests to round-trip an additive top-level `correlation_id` in `WorkerEnvelope` without breaking existing envelopes or sequence validation.
- [x] Add Python tests proving the request correlation context is set during handler execution, inherited by child operations, and cleared after each request.
- [x] Add GUI tests for correlation IDs in REST headers, chat WebSocket payloads, and voice start frames.

### Implementation

- [x] Add an optional `correlation_id` field to `backend-dotnet/RuBackend/Worker/WorkerEnvelope.cs` and the mirrored Python protocol model. Keep it additive and opaque; do not encode session IDs or user data.
- [x] Update public ASP.NET endpoints, `ConversationCoordinator`, `WorkerConnection`, and `LoopbackWorkerClient` to create/validate one ID per operation, place it in the logging context, and carry it through every worker request.
- [x] Update `src/ru_runtime/worker/v1/server.py`, `handlers.py`, and request execution helpers to enter the Python context from the envelope and to attach it to all diagnostic/activity events emitted during that operation.
- [x] Update `gui-winui3/Services/ApiService.cs`, `ChatWebSocketClient.cs`, and `VoiceStreamClient.cs` to generate a fresh ID at each operation boundary and send it through the existing request metadata without changing user-visible chat content.
- [x] Cover streaming acknowledgements, voice chunks, STT, VTS actions, and observation handoffs so no child event silently loses correlation.

### Verification

- [x] Run `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --filter "FullyQualifiedName~Correlation|FullyQualifiedName~WorkerEnvelope"`.
- [x] Run `python -m pytest -q tests/test_correlation_context.py tests/test_worker_protocol.py`.
- [x] Run `dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --filter "FullyQualifiedName~Correlation"`.
- [x] Execute one end-to-end chat and one voice fixture, then assert the same opaque ID appears in the host, worker, and GUI diagnostic records without any session ID or prompt text.

## Task 7: Replace unsafe free-form diagnostics with bounded high-value event codes

### Tests first

- [x] Add C# tests for the ASP.NET event-code map covering worker unavailable/timeout, STT failure, voice start/stream failure, memory queue failure, VTS action status, observation status, request duration, and startup/shutdown status.
- [x] Add Python tests for high-value event helpers in runtime, generation, voice, VTS, observation, search, and persistence modules. Assert only event code plus allowlisted bounded fields are persisted.
- [x] Add a regression test that routes a legacy `logging` record without an approved event code to `diagnostic_unclassified` without persisting the formatted message, arguments, exception text, or stack trace.

### Implementation

- [x] Create a small C# `IDiagnosticEmitter`/event-code catalog and replace the existing `.Log...` calls in `Speech/SttEndpoints.cs`, `Memory/MemoryLearningQueue.cs`, `Runtime/RuntimeEndpointRelay.cs`, `Memory/MemoryEndpoints.cs`, `Business/BusinessEndpoints.cs`, and `Vts/VtsEndpoints.cs`.
- [x] Add a Python safe logging handler that persists only `record.ru_event_code` and explicitly allowlisted scalar fields. Legacy/free-form records become bounded `diagnostic_unclassified` events with component and level only.
- [x] Convert high-value events in `src/ru_runtime/worker/v1/runtime_services.py`, `core.py`, `server.py`, `status.py`, `voice.py`, `vts.py`, `observation.py`, `src/ru_runtime/generation/openai.py`, `runner.py`, `orchestration.py`, `search.py`, `src/ru_runtime/voice/stream.py`, `gpu_lifecycle.py`, `omnivoice_server.py`, `src/ru_runtime/vts/manager.py`, `overlay.py`, `persistence/credential_store.py`, `atomic_write.py`, `system/pc_tools.py`, and vision modules.
- [x] Ensure diagnostics record durations/statuses/error codes rather than arbitrary exception strings. Keep credentials and private control metadata out of both diagnostics and Activity Timeline.
- [x] Add throttling/sampling for VTS sway and other high-frequency loops; do not emit one persistent diagnostic/activity record per frame.

### Verification

- [x] Run focused C# and Python diagnostic event tests, then run `python -m pytest -q tests/test_activity_log.py tests/test_diagnostic_schema.py tests/test_privacy_logging.py`.
- [x] Run a source/privacy scan for `logger.*`, `.Log`, `print`, exception serialization, and activity append call sites; manually classify every remaining persistent path as safe, bootstrap-only, or test-only.
- [x] Generate representative chat, voice, STT, VTS, and observation failures and inspect JSONL to confirm event codes are bounded and messages/stack traces are absent.

## Task 8: Add bounded Diagnostics API routes and route-contract coverage

### Tests first

- [x] Add ASP.NET integration tests for `GET /diagnostics/logs` filtering by `limit`, `since`, `level`, `component`, and `correlation_id`, including lower/upper bounds and invalid-query rejection.
- [x] Add tests for deterministic cursor pagination, file rotation without duplicates, malformed-line skipping, re-sanitization, and no access outside the configured log root.
- [x] Add tests for `GET /diagnostics/export` content type, current filters, bounded output, safe failure code, and absence of filesystem paths/raw bootstrap output.
- [x] Add tests for `GET /diagnostics/status` queue depth, drop counts, write failures, file sizes, retention state, and privacy-safe response shape.
- [x] Add authentication/authorization tests and update `backend-dotnet/RuBackend.Tests/PublicRouteContractTests.cs` plus `backend-dotnet/public-route-contract.json` for all three routes.

### Implementation

- [x] Create `backend-dotnet/RuBackend/Diagnostics/DiagnosticEndpoints.cs` and typed query/response models. Register the three authenticated routes without changing existing Activity Timeline routes.
- [x] Bound every query and response: cap records, bytes, filter lengths, cursor size, and export duration; parse `since` with a fixed UTC format; reject invalid values with a stable public error code.
- [x] Use `DiagnosticLogReader` to read only configured files, merge deterministically, re-sanitize records, and return a cursor suitable for incremental GUI refresh.
- [x] Return `application/x-ndjson` from export and never return raw stdout/stderr, arbitrary paths, exception text, or unbounded file contents.
- [x] Return status counters from writer/retention services without making a diagnostic status request recursively emit another diagnostic event.

### Verification

- [x] Run `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --filter "FullyQualifiedName~DiagnosticEndpoints|FullyQualifiedName~PublicRouteContract"`.
- [x] Exercise all routes against a temporary `RU_LOG_ROOT` containing valid, malformed, partial, and bootstrap files; compare returned records with the allowlisted schema.
- [x] Verify pre-existing `/activity-log` route tests remain green.

## Task 9: Add the WinUI Diagnostics surface while preserving Activity Timeline

### Tests first

- [x] Add diagnostic DTOs and bounded filter/status/export models in `gui-winui3/Models/Models.cs`.
- [x] Add `ApiService` methods for diagnostics logs, status, and export. Use cancellation tokens/timeouts and do not open local log files from the GUI.
- [x] Refactor `LogView.xaml` and `LogView.xaml.cs` into two clearly separated surfaces/tabs: existing Activity Timeline and a Diagnostics view with level/component/event-code/correlation filters, pause/resume, incremental refresh, export, and bounded warning state.
- [x] Keep Activity filters and explicit confirmed clear behavior unchanged. Render only safe preview/allowlisted details; do not display private VTS metadata or diagnostic internals in chat/history.
- [x] Add a GUI diagnostic writer for `gui-YYYY-MM-DD.jsonl` and replace `App.xaml.cs` raw `app_crash.txt` persistence with a safe crash event while preserving recovery behavior. Bootstrap fallback may remain only under the canonical bootstrap directory.
- [x] Ensure refresh and export work remain off the UI thread and do not cause full-file reloads.

### Verification

- [x] Run `dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --filter "FullyQualifiedName~Log|FullyQualifiedName~Diagnostic|FullyQualifiedName~ApiService"`.
- [x] Build the WinUI project in the existing repository-supported configuration and verify no compiler warnings/errors were introduced by the new surface.
- [x] Perform a user-run desktop smoke check separately: Activity rows, Diagnostics filters, pause/resume, export, and VTS/voice display. Record this as manual acceptance, not as automated evidence.

## Task 10: Add the latency gate, run sequential verification, and update documentation

### Tests first / benchmark harness

- [x] Add `tests/test_logging_performance.py` for queue enqueue cost, saturation/drop behavior, and no-blocking guarantees using a fake unavailable/slow directory.
- [x] Add `scripts/benchmark_logging.py` with paired logging-disabled and production-level logging modes for chat streaming, voice start/stream/ack, STT, VTS activity/action, high-frequency sway sampling, and observation capture/reaction/ack.
- [x] The benchmark must warm both modes, collect at least 30 paired steady-state samples per path, detect synchronous file I/O in hot-path spans, and report p50/p95/p99 plus absolute and percentage deltas.
- [x] Make the acceptance assertion explicit: logging-enabled p95 and p99 must each be within both 2% and 2 ms of the disabled baseline for every path. A functional pass alone is insufficient.

### Implementation and verification

- [x] Add instrumentation only around existing operation boundaries and queue calls; do not add timing work that itself changes the hot path or persists user data.
- [x] Run heavyweight suites sequentially to avoid Windows pagefile exhaustion:
  1. `python -m pytest -q tests/test_activity_log.py tests/test_activity_outbox.py tests/test_diagnostic_schema.py tests/test_diagnostic_log.py tests/test_correlation_context.py tests/test_logging_performance.py`.
  2. `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj`.
  3. `dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj`.
  4. `python scripts/benchmark_logging.py --samples 30 --output docs/verification/logging-benchmark-2026-08-20.md`.
  5. Run the repository-supported build/check commands from `build.bat` one at a time and record environment/import failures separately from code failures.
- [x] Verify queue saturation, log-directory-unavailable, malformed-line, rotation, retention, and shutdown scenarios.
- [x] Review `git diff --check`, inspect all generated diagnostic/activity files, and confirm no raw transcript or secret data is present.

### Documentation and final handoff

- [x] Update `README.md` with canonical log locations, `RU_LOG_ROOT`, Diagnostics usage, bootstrap fallback scope, and privacy boundaries.
- [x] Update `docs/operations/runtime-behavior.md` with Activity ownership, worker outbox behavior, diagnostic allowlists, correlation IDs, queue/drop failure behavior, and the measured latency gate.
- [x] Update `docs/architecture.md` with the Public Host/Runtime Worker/GUI logging ownership boundary and outbox flow; create it only if the repository has no existing architecture document.
- [x] Document architecture and decisions in `docs/operations/logging.md` documenting why ASP.NET owns canonical activity persistence and why diagnostics remain per-process JSONL.
- [x] Add the benchmark result and remaining user-run desktop/audio/VTS acceptance boundaries under `docs/verification/` without claiming live acceptance that was not observed.
- [x] Re-scan project documents for outdated statements about FastAPI, repository-root logs, raw Activity Timeline content, or logs containing transcripts; update or remove only statements contradicted by the completed implementation.
- [x] Stage only logging-upgrade files, verify `git diff --cached --name-only`, and preserve all unrelated dirty-worktree changes before any commit or handoff.

## Final acceptance checklist

- [x] `GET /activity-log` and `DELETE /activity-log` remain compatible and Activity Timeline contains previews, never raw chat content.
- [x] ASP.NET is the only canonical activity-file writer; worker activity uses a retryable bounded outbox with de-duplication.
- [x] `aspnetcore-YYYY-MM-DD.jsonl`, `worker-YYYY-MM-DD.jsonl`, and `gui-YYYY-MM-DD.jsonl` are structured, redacted, rotated, retained, and independently writable.
- [x] Diagnostics API filters, cursor, export, status, and auth contract tests pass.
- [x] GUI Diagnostics works through the API and never reads files directly.
- [x] Correlation IDs are visible across one end-to-end operation without exposing session IDs or user text.
- [x] Logging failures and queue overflow do not fail user operations and are visible only through safe status counters/warnings.
- [x] Sequential automated suites pass, the 30-sample latency gate passes for all listed paths, and manual desktop/audio/VTS acceptance is clearly separated from automated evidence.
- [x] No FastAPI public fallback or alternate backend mode is reintroduced.
