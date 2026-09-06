# Dead Runtime Scaffolding Retirement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove runtime-retirement code and compatibility-named tooling that no longer has a live execution path.

**Architecture:** Make managed C# startup unconditional by deleting the one-value migration selector. Keep one deterministic Harness with two real modes and rename current benchmark/Harness entry points without aliases.

**Tech Stack:** .NET 10/C#, Python 3.12+, pytest, PowerShell, WinUI 3 release build

**Status:** Completed 2026-09-06

## Global Constraints

- Preserve all unrelated dirty files and stage only files owned by this plan.
- Do not add aliases, fallback APIs, or special handling for retired selectors.
- Do not remove Worker v1 device/voice protocol or any persisted-data migration.
- Run heavyweight gates sequentially.
- Historical dated plans/specifications remain unchanged.

---

### Task 1: Remove the runtime migration selector

**Files:**
- Modify: `backend-dotnet/RuAgent.Hosting/WorkerBrokerStartupOptions.cs`
- Delete: `backend-dotnet/RuAgent.Hosting/RuntimeMigrationMode.cs`
- Delete: `backend-dotnet/RuAgent.Hosting.Tests/RuntimeMigrationModeTests.cs`
- Modify: `backend-dotnet/RuAgent.Hosting.Tests/WorkerBrokerLaunchPlanTests.cs`
- Modify: `backend-dotnet/RuBackend/Program.cs`

**Interfaces:**
- Consumes: existing `WorkerBrokerStartupOptions` startup contract
- Produces: mode-free constructor beginning with `AgentWorkerToken`

- [x] **Step 1: Change the constructor test call first**

  Replace the test construction with:

  ```csharp
  return new WorkerBrokerStartupOptions(
      "agent-token-" + Guid.NewGuid().ToString("N"),
      "device-token-" + Guid.NewGuid().ToString("N"),
      bootstrap,
      agentPort,
      devicePort,
      null,
      Environment.GetEnvironmentVariable("RU_AGENT_BROKER_EXE"));
  ```

- [x] **Step 2: Verify RED**

  Run: `dotnet test backend-dotnet/RuAgent.Hosting.Tests/RuAgent.Hosting.Tests.csproj -c Release --no-restore`

  Expected: compilation fails because the production record still requires `RuntimeMigrationMode`.

- [x] **Step 3: Remove the production selector**

  Delete the mode parameter and `Enum.IsDefined` check from
  `WorkerBrokerStartupOptions`; remove the configuration read/parser and mode
  constructor argument from `RuBackend/Program.cs`; delete
  `RuntimeMigrationMode.cs` and its selector-only test file.

- [x] **Step 4: Verify GREEN**

  Run: `dotnet test backend-dotnet/RuAgent.Hosting.Tests/RuAgent.Hosting.Tests.csproj -c Release --no-restore`

  Expected: all Hosting tests pass.

### Task 2: Replace compatibility Harness entry points

**Files:**
- Create: `scripts/ru_harness.py`
- Delete: `scripts/aspnetcore_compatibility_harness.py`
- Create: `scripts/benchmark_backend.py`
- Delete: `scripts/benchmark_aspnetcore_compat.py`
- Modify: `scripts/harness/session.py`
- Modify: `scripts/run_gui_voice_harness.ps1`
- Create: `tests/test_ru_harness.py`
- Delete: `tests/test_aspnetcore_compatibility_tools.py`
- Modify: `tests/test_agent_harness.py`
- Modify: `tests/test_harness_routes.py`
- Modify: `tests/test_harness_session.py`

**Interfaces:**
- Consumes: `HarnessSession.run`, the deterministic worker server, and the existing GUI scenarios
- Produces: `ru_harness.main(argv)` supporting only `backend-deterministic` and `gui-deterministic`

- [x] **Step 1: Write renamed-entry-point and removed-mode tests**

  Add assertions equivalent to:

  ```python
  def test_harness_has_only_live_modes() -> None:
      assert (ROOT / "scripts/ru_harness.py").is_file()
      assert not (ROOT / "scripts/aspnetcore_compatibility_harness.py").exists()
      source = (ROOT / "scripts/ru_harness.py").read_text(encoding="utf-8")
      assert 'choices=("backend-deterministic", "gui-deterministic")' in source
      assert "runtime-live" not in source
  ```

  Update benchmark imports to `benchmark_backend` and Harness references to
  `ru_harness.py` before creating those entry points.

- [x] **Step 2: Verify RED**

  Run: `D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_ru_harness.py tests/test_agent_harness.py tests/test_harness_routes.py tests/test_harness_session.py`

  Expected: collection/assertion failures because renamed files do not exist and `runtime-live` is still accepted.

- [x] **Step 3: Implement the deterministic-only Harness**

  Move the active CLI behavior to `ru_harness.py`; default to
  `backend-deterministic`; remove `runtime-live`, `--deterministic-agent`,
  `--require-voice-audio`, `--benchmark-samples`, and dead probe/helper
  functions. Keep `--serve-deterministic-worker` as a hidden internal switch.
  Rename the backend benchmark without an alias. Restrict `HarnessMode` and
  delete the retired branch in `HarnessSession._run_backend`.

- [x] **Step 4: Verify GREEN**

  Run the focused pytest command from Step 2.

  Expected: all selected tests pass.

### Task 3: Reconcile current documentation and prove removal

**Files:**
- Modify: `README.md`
- Modify: `backend-dotnet/README.md`
- Modify: `docs/README.md`
- Modify: `docs/architecture.md`
- Modify: `docs/operations/python-runtime-boundary.md`
- Modify: `docs/operations/runtime-behavior.md`
- Modify: `docs/operations/verification.md`
- Modify: `TODO.md`
- Modify: `tests/test_current_documentation.py`
- Modify: `tests/test_python_runtime_boundary.py`

**Interfaces:**
- Consumes: current managed topology and renamed commands
- Produces: current docs with no runnable retired interface and a source-level removal guard

- [x] **Step 1: Add a removal guard before updating code/docs**

  Assert that non-historical source contains none of:

  ```python
  forbidden = (
      "RuntimeMigrationMode",
      "python_compatibility_retired",
      "runtime-live",
      "aspnetcore_compatibility_harness",
      "benchmark_aspnetcore_compat",
  )
  ```

- [x] **Step 2: Verify RED**

  Run: `D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_current_documentation.py tests/test_python_runtime_boundary.py`

  Expected: failure listing the still-present retirement scaffolding.

- [x] **Step 3: Update current docs**

  Remove instructions for retired selectors and `runtime-live`; replace active
  command examples with `scripts/ru_harness.py` and
  `scripts/benchmark_backend.py`; record Wave 1 completion in `TODO.md`.
  Do not rewrite historical dated plans/specs.

- [x] **Step 4: Run focused verification**

  Run the focused pytest command from Step 2 and:

  ```powershell
  rg -n "RuntimeMigrationMode|python_compatibility_retired|runtime-live|aspnetcore_compatibility_harness|benchmark_aspnetcore_compat" backend-dotnet gui-winui3 scripts src tests README.md CONTEXT.md TODO.md docs --glob '!docs/superpowers/**' --glob '!**/bin/**' --glob '!**/obj/**'
  ```

  Expected: tests pass and the scan returns no matches.

### Task 4: Sequential repository verification

**Files:**
- Modify only if a gate exposes an owned regression.

**Interfaces:**
- Consumes: completed Tasks 1-3
- Produces: fresh verification evidence for the current checkout

- [x] **Step 1: Run Python gates**

  Run Ruff, Mypy, `pip check`, compileall, and the full pytest suite using
  `D:\LLM\.venv\Scripts\python.exe`.

- [x] **Step 2: Run the full .NET Release matrix**

  Discover every `*Tests.csproj` recursively and execute them sequentially.

- [x] **Step 3: Run contract and Harness gates**

  Run the route matrix, `ru_harness.py --mode backend-deterministic`, and the
  existing Agent GUI Harness scenarios sequentially.

- [x] **Step 4: Build and inspect the workspace**

  Run `build.bat`, `git diff --check`, the forbidden-symbol scan, and
  `git status --short --branch`. Confirm unrelated dirty files remain unstaged
  and unchanged by this plan.
