# Test-suite cleanup implementation plan

> **For agentic workers:** Use superpowers:executing-plans for the main sequence and superpowers:dispatching-parallel-agents for the disjoint CI and GUI tasks.

**Goal:** Remove obsolete or misleading tests and orphaned Python VTS code while preserving current runtime regression coverage.

**Architecture:** Managed C# remains the runtime owner. Keep the isolated Python OmniVoice provider and tools that still have consumers. Test script behavior and stable contracts, not historical file lists or exact human prose.

**Tech Stack:** Python/pytest, PowerShell, .NET 10/xUnit, WinUI 3.

## Global constraints

- Work in `D:\LLM` on `codex/test-suite-cleanup`; preserve the existing TODO/verification audit edits.
- The initial cleanup excluded commit/push. The user subsequently authorized
  committing and pushing `codex/test-suite-cleanup`; no merge or PR is included.
  Deleted tracked files remain recoverable from Git.
- No GUI automation, real VTS connection, audio mutation, paid provider calls, or environment/package removal.
- Run heavy test/build commands sequentially. Sub-agents use Luna only.
- No changes to current managed runtime behavior, settings migration, or public routes (56 HTTP + 1 WebSocket).

## Task 1: CI executes all current .NET test projects

- [x] Replace the hand-maintained project list in `.github/workflows/ci.yml` with `scripts/test_dotnet.ps1`.
- [x] Discover test projects in the backend and GUI test roots, execute them sequentially, and propagate the first failure.
- [x] Replace stale assertions in `tests/test_ci_contract.py`; exercise the runner against temporary test projects and a fake `dotnet` executable.
- [x] Verify newly discovered projects run and failures stop subsequent execution. Keep GUI harness opt-in.

## Task 2: Remove cosmetic GUI source snapshots

- [x] Remove `gui-winui3.Tests/EditorialReadingRoomContractTests.cs` and `MeteorShowerFieldContractTests.cs`.
- [x] Replace `ResponsiveLayoutContractTests.cs` source-marker checks with a small structural contract only where it protects actual resource/accessibility wiring.
- [x] Preserve `ResponsiveLayoutPolicyTests.cs`, existing accessibility/automation contracts, session deletion, playback/subtitle and agent behavior tests.
- [x] Verify GUI tests without starting RuApp.

## Task 3: Python test ownership and retirement

- [x] Remove `tests/test_phase2_file_tools_matrix.py` and the no-op orchestration test.
- [x] Consolidate backend/package/Phase 3 retirement guards; remove one-off deletion manifests and duplicate build-string checks, preserving real build-script tests.
- [x] Replace the vacuous runtime inventory reachability assertion with tests of the inventory's actual bounded classification behavior; describe inventory honestly.
- [x] Replace `tests/test_current_documentation.py` exact prose/ADR-count checks with small local-link and ADR metadata integrity checks.
- [x] Verify no active consumer outside `src/ru_runtime/vts` or its nine test files imports Python VTS. Remove that package and those tests; remove only dependencies exclusively owned by it.
- [x] Keep active voice assets/helpers and memory/speech tooling. Ensure full pytest cannot execute the retired VTS connection test.

## Task 4: Verification and handoff

Run from `D:\LLM` in this order:

```powershell
.\.venv\Scripts\python.exe -m pytest -q
.\.venv\Scripts\python.exe -m ruff check src tests scripts tools
.\.venv\Scripts\python.exe -m mypy src
.\.venv\Scripts\python.exe scripts/check_aspnetcore_route_matrix.py
./scripts/test_dotnet.ps1 -Configuration Release
```

- [x] Update `TODO.md`, `docs/operations/verification.md`, `docs/operations/python-runtime-boundary.md`, and the documentation index with actual completion and retained boundaries.
- [x] Review the integrated diff, resolve actionable findings, and run affected tests after fixes.
- [x] Record final test counts and `git diff --check`; do not claim live-device acceptance or a freshly published GUI.

## Completion evidence

- Full Python suite: `402 passed` (pre-cleanup audit: `510`).
- Full managed Release matrix: `2,009 passed` in 13 projects, including
  `633` backend and `252` GUI cases. No GUI was launched.
- Ruff passed; Mypy passed for `39` source files; `pip check` passed.
- Public route contract remains `56 HTTP + 1 WebSocket`.
- Runtime inventory reports schema 2 and two known managed entrypoints;
  the cross-language golden fixture itself remains schema 1 and unchanged.
- Runner review regressions were observed failing before the discovery fix,
  then passing with nested project discovery, invalid-root rejection,
  discovery-error propagation, and the first child failure code preserved.
- Build tests assert the specific missing-artifact diagnostic; GUI names use
  the same validation predicate in real-source and mutation checks.
- Thirty tracked obsolete files were deleted (17 Python test files, two GUI
  test files, and 11 Python VTS source files). Git retains their recovery path.
- A business test's JSONL cleanup failure was isolated by a test-only emitter;
  dedicated diagnostics tests still exercise the real writer. The production
  writer shutdown risk is separately recorded in TODO, not declared fixed.
- Generated `src/ru_runtime/vts/__pycache__` (11 bytecode files) remains:
  the cleanup tool rejected its removal. No alternate deletion was attempted.
  Environment packages, provider/model assets, app data and tokens are untouched.
- Initial cleanup verification did not commit or push; Git publication is a
  subsequently authorized handoff. No fresh GUI publication or native-device
  acceptance was done.
