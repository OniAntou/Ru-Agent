# Agent Tool Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Harden Ru's file, terminal, and recovery tools against newline mismatches, unbounded search work, stdin hangs, and unsafe recovery sizing.

**Architecture:** Keep the existing public tool contracts and add small internal options/helpers at the adapter boundaries. File search will enumerate a bounded candidate set, process files through a clamped worker limit, then sort by ordinal path and line before applying the result cap. Terminal execution will redirect and close stdin in the shared process runner; canonical command policy remains the authority for TTY/editor rejection. Recovery stores and host preparation share one validated cap.

**Tech Stack:** .NET 8, C#, xUnit, `Parallel.ForEachAsync`, `CancellationToken`, SQLite-backed `FileRecoveryStore`.

---

### Task 1: CRLF-safe file editing

**Files:**
- Modify: `backend-dotnet/RuAgent.Broker/Tools/Files/FileEditAdapter.cs`
- Modify: `backend-dotnet/RuBackend/Harness/Broker/HarnessToolGateway.cs`
- Test: `backend-dotnet/RuAgent.Broker.Tests/FileEditAdapterTests.cs`
- Test: `backend-dotnet/RuBackend.Tests/Harness/HarnessReadOnlyTracerTests.cs` (or the existing gateway mutation test file)

- [ ] Add tests where a CRLF file is edited with LF `old_string`/`new_string`, assert one replacement, CRLF bytes remain, and the gateway path has the same result.
- [ ] Run the focused tests and verify the new tests fail against the gateway's exact-only implementation.
- [ ] Centralize fallback normalization so exact matching is attempted first, LF matching is used only when needed, and the original CRLF style is restored before hashing/writing.
- [ ] Run both focused test classes and commit the owned files.

### Task 2: Bounded concurrent deterministic search

**Files:**
- Modify: `backend-dotnet/RuAgent.Broker/Tools/Files/FileSearchAdapter.cs`
- Test: `backend-dotnet/RuAgent.Broker.Tests/FileDiscoveryAdapterTests.cs`

- [ ] Add a cancellation/ordering test using the adapter's cancellation-aware overload and multiple matching files; assert ordinal path/line order and prompt cancellation.
- [ ] Run the focused test and verify it fails because search currently has no cancellation-aware overload and reads sequentially.
- [ ] Add `Execute(JsonObject, CancellationToken)` while retaining the existing overload, enumerate bounded candidates, process them with a clamped `MaxSearchConcurrency`, collect at most the bounded per-file matches, and sort before applying caps.
- [ ] Ensure skipped counts and time/byte caps remain bounded and cancellation is propagated instead of converted to a skipped file.
- [ ] Run discovery tests and commit the owned files.

### Task 3: EOF-safe terminal process execution

**Files:**
- Modify: `backend-dotnet/RuAgent.Broker/Tools/Terminal/StreamingProcessRunner.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Tools/Terminal/TerminalAdapter.cs` only if a canonical interactive rejection helper is required
- Test: `backend-dotnet/RuAgent.Broker.Tests/StreamingProcessRunnerTests.cs`
- Test: existing terminal canonicalization tests for the stable rejection code

- [ ] Add a test command that reads one line from stdin and assert the runner returns promptly with no input supplied; add/retain a test for `--tty`/editor-style rejection before launch.
- [ ] Run focused terminal tests and verify the stdin test hangs or fails before the change.
- [ ] Set `RedirectStandardInput = true`, close `process.StandardInput` immediately after the child is assigned to the job, and preserve existing output draining/timeout cleanup.
- [ ] Run terminal tests and commit the owned files.

### Task 4: Configurable bounded recovery cap

**Files:**
- Modify: `backend-dotnet/RuAgent.Broker/Tools/Files/FileToolLimits.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Tools/Files/FileDeleteAdapter.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Tools/Files/FileRestoreAdapter.cs`
- Modify: `backend-dotnet/RuAgent.Harness.Persistence/Recovery/FileRecoveryStore.cs`
- Modify: `backend-dotnet/RuBackend/Harness/Broker/HarnessToolGateway.cs`
- Test: `backend-dotnet/RuAgent.Broker.Tests/FileDeleteRestoreTests.cs`
- Test: `backend-dotnet/RuAgent.Harness.Persistence.Tests/FileRecoveryStoreTests.cs`

- [ ] Add tests constructing adapters/store with a small configured cap and assert oversized capture is rejected while the default remains 4 MiB.
- [ ] Run focused tests and verify the configurable constructor/options do not yet compile or enforce the cap.
- [ ] Introduce one validated `FileRecoveryOptions` value (default 4 MiB, hard maximum 4 MiB), inject it into delete/restore/store/gateway paths, and replace all static cap reads with the instance value.
- [ ] Run focused broker/persistence tests and commit the owned files.

### Task 5: Documentation and regression verification

**Files:**
- Modify: `TODO.md`
- Modify: `docs/operations/agent-tools.md`
- Modify: `docs/architecture.md`

- [ ] Document CRLF preservation, bounded search concurrency/cancellation, EOF-closed terminal stdin, and the configured recovery cap.
- [ ] Mark the four hardening bullets complete only after tests pass; do not change unrelated dirty sections.
- [ ] Run all .NET test projects sequentially, docs tests, route matrix, and build checks; inspect `git diff` and `git status` for unrelated changes.
