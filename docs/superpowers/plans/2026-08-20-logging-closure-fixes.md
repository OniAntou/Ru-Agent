# Ru Logging Closure Fixes Implementation Plan

> **Status:** Historical implementation record. The closure fixes shipped on
> 2026-08-20; use `docs/operations/logging.md` for the current contract.

> **For agentic workers:** Execute inline in this session. Sub-agents are disabled. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the verified logging/privacy/API/benchmark gaps while preserving the current Ru runtime behavior and latency contract.

**Architecture:** Add small safe emitters around the existing bounded writers, keep ASP.NET as the sole Activity Timeline writer, and make Python/GUI diagnostics process-local under the canonical log root. Tighten API bounds and use a temporary root for benchmark writes so verification is isolated from user data.

**Tech Stack:** ASP.NET Core/.NET 10, C# xUnit, WinUI 3, Python 3/pytest, JSONL, `System.Text.Json`, `contextvars`, and the existing bounded queues.

---

## Task 1: Lock down the process-local diagnostic emitter contract

**Files:**
- Create: `backend-dotnet/RuBackend/Diagnostics/IDiagnosticEmitter.cs`
- Create: `backend-dotnet/RuBackend/Diagnostics/DiagnosticEmitter.cs`
- Modify: `backend-dotnet/RuBackend/Program.cs`
- Test: `backend-dotnet/RuBackend.Tests/Diagnostics/DiagnosticEmitterTests.cs`

- [x] Write tests that enqueue a valid event without awaiting disk, sanitize an invalid correlation ID, and omit unapproved fields.
- [x] Run `dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --filter FullyQualifiedName~DiagnosticEmitter`; verify the new tests fail before implementation.
- [x] Implement `IDiagnosticEmitter.TryEmit` as a small facade over `StructuredDiagnosticLogWriter.TryEnqueue`; construct only allowlisted scalar fields and use `CorrelationContext.Current` when no ID is supplied.
- [x] Register the emitter as a singleton beside the existing writer.
- [x] Run the focused emitter tests and confirm they pass.

## Task 2: Add Python safe logging and activity counters

**Files:**
- Modify: `src/ru_runtime/persistence/diagnostic_log.py`
- Modify: `src/ru_runtime/__init__.py`
- Modify: `src/ru_runtime/persistence/activity_outbox.py`
- Modify: `src/ru_runtime/persistence/activity_log.py`
- Test: `tests/test_diagnostic_log.py`, `tests/test_activity_outbox.py`

- [x] Add a public `DiagnosticLoggingHandler` that persists only an allowlisted event code and scalar fields, never `record.getMessage()` or `exc_text`.
- [x] Add process-local dropped/write-failure counters to the Activity outbox and keep the hot path lock-only and non-blocking; failed batches remain retryable.
- [x] Install the handler once without replacing the existing console handler and expose reset/remove seams for tests.
- [x] Write one failing test for message/traceback redaction and one for activity overflow/failure counters, run them, then implement the minimal behavior and rerun.

## Task 3: Complete production diagnostic seams and GUI writer

**Files:**
- Modify: `backend-dotnet/RuBackend/Conversation/ConversationCoordinator.cs`
- Modify: `backend-dotnet/RuBackend/Speech/SttEndpoints.cs`
- Modify: `backend-dotnet/RuBackend/Vts/VtsEndpoints.cs`
- Modify: `backend-dotnet/RuBackend/Runtime/RuntimeEndpointRelay.cs`
- Create: `backend-dotnet/RuBackend/Diagnostics/DiagnosticRequestMiddleware.cs`
- Modify: `backend-dotnet/RuBackend/Memory/MemoryEndpoints.cs`
- Modify: `backend-dotnet/RuBackend/Business/BusinessEndpoints.cs`
- Create: `gui-winui3/Services/GuiDiagnosticWriter.cs`
- Modify: `gui-winui3/App.xaml.cs`, `gui-winui3/MainWindow.xaml.cs`, `gui-winui3/Services/BackendLaunchPlan.cs`
- Test: focused C# endpoint/writer tests and GUI contract tests

- [x] Route endpoint success/failure boundaries through `IDiagnosticEmitter` with event codes and durations; retain existing public responses and logger behavior.
- [x] Add a bounded GUI writer using the resolved `RU_LOG_ROOT`; replace both raw crash-file paths with a safe `gui_crash` event.
- [x] Write failing tests proving GUI crash paths contain no exception text and honor an explicit log root, then implement and rerun.
- [x] Source-scan production code to ensure diagnostic events are emitted through the facade/writer and no raw crash file remains.

## Task 4: Harden Activity persistence and ingestion failure behavior

**Files:**
- Modify: `backend-dotnet/RuBackend/Business/Activity/ActivityLogStore.cs`
- Modify: `backend-dotnet/RuBackend/Business/Activity/ActivityOutboxIngestionService.cs`
- Modify: `backend-dotnet/RuBackend/Diagnostics/StructuredDiagnosticLogWriter.cs`
- Modify: `src/ru_runtime/persistence/activity_outbox.py`, `src/ru_runtime/persistence/diagnostic_log.py`
- Test: `backend-dotnet/RuBackend.Tests/Business/ActivityLogStoreTests.cs`, `ActivityOutboxIngestionTests.cs`, `tests/test_activity_outbox.py`, `tests/test_diagnostic_log.py`

- [x] Add tests for re-sanitizing legacy activity reads and retaining failed diagnostic/outbox batches for a later retry.
- [x] Implement retry-safe batch handling without blocking the request path; preserve bounded queues and bounded shutdown.
- [x] Keep existing atomic claim and de-duplication semantics, then run focused activity/diagnostic tests.

## Task 5: Add strict Diagnostics API bounds and complete GUI export filters

**Files:**
- Modify: `backend-dotnet/RuBackend/Diagnostics/DiagnosticLogReader.cs`
- Modify: `backend-dotnet/RuBackend/Diagnostics/DiagnosticEndpoints.cs`
- Modify: `backend-dotnet/RuBackend.Tests/Diagnostics/DiagnosticEndpointsTests.cs`, `DiagnosticLogReaderTests.cs`
- Modify: `gui-winui3/Views/DiagnosticsView.xaml.cs`
- Test: `gui-winui3.Tests/DiagnosticsViewContractTests.cs`

- [x] Add failing tests for invalid `since`/cursor, filter length, byte cap, deterministic cursor continuation, and export filter propagation.
- [x] Implement invariant UTC parsing with stable 400 error results, bounded filter/cursor/record/byte limits, and a bounded NDJSON response.
- [x] Pass component and event-code filters from the GUI export action and show a warning when API methods return `null`.
- [x] Run the focused API/GUI tests and verify all existing route contracts remain compatible.

## Task 6: Isolate and strengthen the latency benchmark

**Files:**
- Modify: `scripts/benchmark_logging.py`
- Modify: `tests/test_logging_performance.py`
- Modify: `docs/verification/logging-benchmark-2026-08-20.md`

- [x] Add a failing test that proves benchmark activity writes use a temporary root and that the report contains p50/p95/p99 plus both p95/p99 gates.
- [x] Inject temporary activity/diagnostic writers, remove direct writes to the canonical user root, and report the actual p50/p95/p99 values.
- [x] Run the benchmark with 30 paired samples per path and confirm its exit code reflects both percentile gates.

## Task 7: Reconcile documents and verify the whole change

**Files:**
- Modify: `README.md`, `docs/operations/runtime-behavior.md`, `docs/operations/logging.md`
- Modify: `docs/verification/logging-benchmark-2026-08-20.md`
- Review: `docs/adr/*`, `docs/superpowers/plans/2026-08-20-logging-system-upgrade.md`

- [x] Remove claims that are not supported by the implementation or automated evidence; record manual desktop/audio/VTS acceptance as pending.
- [x] Run sequentially: focused tests, full backend tests, full GUI tests, full Python pytest, route matrix, Release publishes, benchmark, and `git diff --check`.
- [x] Re-scan for raw crash files, production writer bypasses, formatted diagnostic messages, repository-root logs, and staged files.
- [x] Report exact evidence, remaining manual acceptance, and the unchanged dirty-worktree boundary; do not stage or commit.
