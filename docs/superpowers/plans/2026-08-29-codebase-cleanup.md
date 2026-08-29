# Ru Codebase Cleanup and Organization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the approved dead source and rebuildable output while keeping Ru's active Agent-only runtime topology stable and maintainable.

**Architecture:** Preserve the existing seams between the ASP.NET Core Public Host, RuAgent Broker, Runtime Worker, WinUI app, and native overlay. Cleanup concentrates locality by deleting disconnected entrypoints and disposable generated mass, then makes `.gitignore` describe those outputs explicitly; no active namespace or project file moves are needed.

**Tech Stack:** Python 3.11/3.13, pytest, Ruff, mypy, .NET 10, WinUI 3, PowerShell, Git, existing cleanup inventory tools.

## Global Constraints

- Work in the user-authorized `main` checkout at `D:\LLM`; do not create a worktree or reset unrelated changes.
- Preserve every pre-existing dirty path, especially the documentation refresh changes.
- Delete only the three approved tracked source paths and the exact rebuildable directories present in the read-only manifest.
- Never delete or move models, `.venv`, `omnivoice_env`, `%LOCALAPPDATA%\Ru`, `data`, logs, `.tmp`, package caches, or active voice assets.
- Keep the Public Host, Agent Broker, Runtime Worker, GUI, and native overlay in their current paths and namespaces.
- Run heavyweight D:\LLM suites sequentially and do not claim completion before all gates pass.

---

### Task 1: Establish the deletion baseline

**Files:**
- Read: `artifacts/cleanup/cleanup-manifest-2026-08-29.json`, `git status --short --branch`
- Test: `tests/test_cleanup_inventory.py`, `tests/test_cleanup_policy.py`, `tests/test_workspace_hygiene.py`, `tests/test_current_documentation.py`

**Interfaces:**
- Consumes: the approved cleanup manifest and current dirty-path set.
- Produces: a baseline that guards every later deletion.

- [x] **Step 1: Verify checkout and branch.** Run `git rev-parse --show-toplevel`, `git branch --show-current`, `git rev-parse --git-dir`, `git rev-parse --git-common-dir`, and `git status --short --branch`. Expected: root `D:\LLM`, branch `main`, and only known dirty documentation plus this plan/spec.

- [x] **Step 2: Rebuild the read-only inventory.** Run `& D:\LLM\.venv\Scripts\python.exe -m tools.maintenance.cleanup_inventory --workspace-root D:\LLM --output D:\LLM\artifacts\cleanup\cleanup-manifest-2026-08-29.json`. Expected: the approved source/output candidates are still present and protected model/runtime/app-data/log/`.tmp` paths are not deletion targets.

- [x] **Step 3: Run focused baseline tests.** Run `& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_cleanup_inventory.py tests/test_cleanup_policy.py tests/test_workspace_hygiene.py tests/test_current_documentation.py`. Expected: PASS before cleanup changes.

- [x] **Step 4: Check active build/test processes read-only.** Run `Get-Process dotnet,msbuild,vstest,testhost,cmake,ninja -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,Path`. Expected: no process owns an approved generated directory; if one exists, stop and report it without terminating anything.

### Task 2: Add regression coverage and normalize ignore rules

**Files:**
- Modify: `tests/test_workspace_hygiene.py`
- Modify: `.gitignore`

**Interfaces:**
- Consumes: the existing workspace-hygiene tests and ignore policy.
- Produces: a guard against reintroducing deleted entrypoints and a single explicit rule for each disposable cache.

- [x] **Step 1: Add the failing absence test.** Add `DEAD_CODE_TRACKED_PATHS = ("src/ru_runtime/policy/safety_patterns.py", "run_tests.py", "scripts/probe_gemini_vision.py")` and assert that no path in the tuple exists.

- [x] **Step 2: Run the test before deletion.** Run `& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_workspace_hygiene.py`. Expected: FAIL because the three source paths still exist.

- [x] **Step 3: Normalize `.gitignore`.** Keep one `.venv/` rule, add `.mypy_cache/` and `.ruff_cache/` beside the Python cache rules, and preserve all existing model, runtime, data, overlay, and artifact rules.

- [x] **Step 4: Check the scoped diff.** Run `git diff --check -- .gitignore tests/test_workspace_hygiene.py`. Expected: no whitespace errors or unrelated changes.

### Task 3: Remove disconnected tracked source

**Files:**
- Delete: `src/ru_runtime/policy/safety_patterns.py`
- Delete: `run_tests.py`
- Delete: `scripts/probe_gemini_vision.py`

**Interfaces:**
- Consumes: the absence guard from Task 2.
- Produces: an active source tree without stale safety-pattern or manual probe entrypoints.

- [x] **Step 1: Run the final reference scan.** Run `git grep -n -I -E 'safety_patterns|AI_DISCLAIMER|LIST_PATTERN|GRAPHIC_CONTENT|VI_WRONG_SELF|run_tests\.py|probe_gemini_vision' -- ':!docs/superpowers/**'`. Expected: no active importer or build command depends on these paths.

- [x] **Step 2: Delete exactly the three files with `apply_patch`.** Do not remove neighboring policy modules, benchmark scripts, or observation adapters.

- [x] **Step 3: Run source tests.** Run `& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_workspace_hygiene.py tests/test_python_package_architecture.py`. Expected: PASS and no import error.

### Task 4: Remove exact rebuildable output directories

**Files:**
- Delete (ignored/generated): `.pytest_cache`, `.mypy_cache`, `.ruff_cache`, all current Python `__pycache__` directories, and `src/ru_runtime.egg-info`.
- Delete (ignored/generated): current `bin` and `obj` directories under `backend-dotnet/*`, `gui-winui3`, `gui-winui3.Tests`, and `gui-winui3.Harness`.
- Delete (ignored/generated): `tools/ru_desktop_overlay/out`.

**Interfaces:**
- Consumes: the refreshed inventory entries classified `rebuildable`.
- Produces: a source-only checkout whose outputs can be recreated by existing commands.

- [x] **Step 1: Resolve and validate targets.** Build an explicit PowerShell array from `rebuildable` manifest entries. Verify every resolved path is below `D:\LLM`, is not a link, is not Git-dirty, and is not protected; abort on any mismatch and never widen to a parent path.

- [x] **Step 2: Delete only validated targets.** Use the validated .NET directory API once per explicit target because the shell deletion cmdlet was blocked before execution. Do not use wildcards or target `.tmp`, models, runtimes, data, or logs. Record target paths and byte total.

- [x] **Step 3: Confirm protected roots.** Check `Test-Path D:\LLM\models`, `Test-Path D:\LLM\.venv`, `Test-Path D:\LLM\omnivoice_env`, `Test-Path D:\LLM\data`, then inspect `git status --short --branch`. Expected: all roots and pre-existing dirty paths remain.

### Task 5: Audit documentation and repository shape

**Files:**
- Read: `README.md`, `CONTEXT.md`, `docs/README.md`, `docs/architecture.md`, `docs/operations/*.md`, `docs/adr/*.md`
- Modify only if needed: current documentation or `.gitignore`

**Interfaces:**
- Consumes: the cleaned source tree and removed-path list.
- Produces: current docs with no live references to deleted entrypoints and a stable domain-oriented tree.

- [x] **Step 1: Scan docs and commands.** Run `git grep -n -I -E 'safety_patterns|run_tests\.py|probe_gemini_vision|\.mypy_cache|\.ruff_cache' -- '*.md' '*.bat' '*.ps1' '*.yml' '*.toml'`. Update only a current-contract reference found by this scan; historical records and ADRs stay intact.

- [x] **Step 2: Verify no dumping directory was introduced.** Run `git ls-files 'backend-dotnet/**' 'gui-winui3/**' 'src/ru_runtime/**' 'scripts/**' 'tools/**' | Select-String -Pattern '(^|/)(common|shared|helpers|compatibility)/'`. Expected: no match.

### Task 6: Full verification and handoff

**Files:**
- Read: final cleanup inventory, Git diff/status, test/build output
- Modify: this plan to record completed steps and verification results

**Interfaces:**
- Consumes: the cleaned source tree and all prior task outputs.
- Produces: evidence-backed completion status without staging unrelated work.

- [x] **Step 1: Run `& D:\LLM\.venv\Scripts\python.exe -m pytest -q`.** Expected: all Python tests pass. Result: `829 passed in 26.47s`.

- [x] **Step 2: Run .NET test projects sequentially:** `dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj --nologo`; `dotnet test D:\LLM\backend-dotnet\RuAgent.Connectors.Tests\RuAgent.Connectors.Tests.csproj --nologo`; `dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Core.Tests\RuAgent.Harness.Core.Tests.csproj --nologo`; `dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Persistence.Tests\RuAgent.Harness.Persistence.Tests.csproj --nologo`; `dotnet test D:\LLM\backend-dotnet\RuAgent.Hosting.Tests\RuAgent.Hosting.Tests.csproj --nologo`; `dotnet test D:\LLM\backend-dotnet\RuAgent.Client.Tests\RuAgent.Client.Tests.csproj --nologo`; `dotnet test D:\LLM\backend-dotnet\RuAgent.Cli.Tests\RuAgent.Cli.Tests.csproj --nologo`; `dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --nologo`; `dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo`. Stop on the first failure. Result: all 9 projects passed, `1,852` tests total, no failures/skips.

- [x] **Step 3: Run contract and diff checks.** Run `& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py`, `git diff --check`, and a repository-wide reference scan for the three removed names. Expected: route contract and diff checks pass and no active file references a deleted path. Result: `54 HTTP + 1 WebSocket (/voice/stream)`, diff clean, active references absent.

- [x] **Step 4: Generate `D:\LLM\artifacts\cleanup\cleanup-manifest-2026-08-29-final.json` with the inventory tool.** Expected: the three dead paths are absent, generated outputs are rebuildable, and protected paths remain untouched. Result: `rebuildable=0`, protected roots present; `review_required` remains intentionally unmodified.

- [x] **Step 5: Review `git status --short --branch` and `git diff --name-status`.** Expected: only cleanup files, this plan/spec, and the already-dirty documentation refresh are present; no unrelated file is staged. Result: cleanup changes are limited to the three approved deletions, `.gitignore`, hygiene test, and this plan; pre-existing documentation refresh remains untouched.

## Verification record

- Python: `829 passed in 26.47s`.
- .NET: 9 projects, `1,852` passed, `0` failed, `0` skipped.
- Route contract: `54 HTTP + 1 WebSocket (/voice/stream)`.
- Final inventory: `0` rebuildable entries; protected and review-required paths retained.
- Final checks: no active references to deleted paths, no dumping directory, and `git diff --check` passed.
