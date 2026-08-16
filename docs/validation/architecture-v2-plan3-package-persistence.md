# Architecture v2 Plan 3: Package and persistence migration validation

Validated 2026-08-17 in the isolated `ru-architecture-v2` worktree
(branch `codex/ru-architecture-v2`). No application was launched; main's
dirty source and untracked files were not touched.

## Gates (all green)

| Gate | Result |
| --- | --- |
| Python `pytest -q` | 464 passed, 1 warning |
| Backend .NET `RuBackend.Tests` | 227 passed (219 after temporary code removal) |
| WinUI `gui-winui3.Tests` | 94 passed |
| ASP.NET Core route matrix | 38 HTTP + 2 WebSocket, contract OK |
| `pip check` | No broken requirements |
| NuGet vulnerability audit (RuBackend) | No vulnerable packages |
| NuGet vulnerability audit (RuApp) | No vulnerable packages |
| `build.bat` | Backend + GUI self-contained publishes succeeded; `RuApp.pri` created |
| Self-contained executables | `RuBackend.exe` and `RuApp.exe` present, valid PE (`MZ`) |

## Identity and compatibility

- Distribution identity: `ru-runtime` (`pyproject.toml`), package `ru_runtime`;
  import verified through the test suite (`pythonpath = ["src", "scripts"]`).
- No `src/mini_gpt` exists; no active `mini_gpt` import/service/path in
  production Python or C# source (only a generic store unit test uses the
  string as an arbitrary service name).
- Worker launch identity is `-m ru_runtime.worker.v1.server`
  (`gui-winui3/Services/BackendLaunchPlan.cs`), covered by
  `gui-winui3.Tests/BackendLaunchPlanTests.cs`.
- Protected training seam retired: `src/mini_gpt/llama3_1_training.py` and
  `tests/test_training_compatibility.py` deleted; replacement is
  `tools/training/lfm25/` + `tools/evaluation/lfm25/smoke.py` +
  `configs/ru_lfm2_5_v1.json`.

## Migration execution (Task 6)

Preflight recheck matched the recorded metadata exactly: legacy
`C:\Users\USER\.minigpt` had 22 files / 95,231 bytes, 0 reparse points,
0 unexpected files, 0 nested directories; canonical `%LOCALAPPDATA%\Ru`
existed and was empty; credential presence matched (OpenCode + Kilo present
under `mini_gpt`, Gemini absent; `Ru` service absent).

- Business migration staged and atomically activated all 22 records
  (4 root files + 18 sessions); stores read the canonical files
  (settings backend, 18 sessions, 2 history messages, 1 activity entry).
- SHA-256 of every canonical file matched its legacy source (22/22, 0 mismatches).
- Credentials migrated `mini_gpt` -> `Ru` for `api_key` and
  `kilo_gateway_api_key` with read/write/read/constant-time compare;
  legacy entries deleted only after equality. `gemini_vision_api_key` absent
  both before and after, as preflight recorded.
- Legacy directory retired to `.minigpt.migrated-2026-08-17` (backup retained
  for recovery; no longer an active path). Temporary `--migrate-local-state`,
  `--restore-legacy-credentials`, and `--credential-preflight` commands plus
  the migrator implementations and their tests were removed.
- Evidence (counts, hashes, statuses only; no contents or values):
  `docs/validation/plan3-task6-migration-evidence.md`.

## Overlap map for integration review (main -> branch)

| Main (dirty/untracked) | Branch replacement | Status |
| --- | --- | --- |
| `src/mini_gpt/*` (8 modified) | `src/ru_runtime/**` (committed) | Replaced (rename + domain reorganization) |
| `src/mini_gpt/llama3_1_training.py` | `tools/training/lfm25/`, `tools/evaluation/lfm25/`, `configs/ru_lfm2_5_v1.json` | Replaced (training seam retired) |
| `tests/test_llama_cpp_runtime.py` | same path on branch | Branch version wins; main differs by 2 lines/encoding |
| `scripts/probe_gemini_vision.py` | same path on branch | Content equivalent; encoding-only difference |
| `gui-winui3/App.xaml` + 12 UI files | same paths on branch | Branch has Task 1-3 versions; main has user-local edits (e.g. App.xaml ~55 differing lines) - manual review required |
| `configs/ru_lfm2_5_v1.json` (untracked) | same path on branch | Identical SHA-256 |
| `scripts/evaluate_ru_lfm2_5_v1.py`, `scripts/merge_ru_v1.py` (untracked) | absent on branch | Evaluation workflow inputs; add to branch when integrating |

## Residual

- Legacy backup `.minigpt.migrated-2026-08-17` remains on the machine for
  recovery; delete manually once the canonical data is confirmed stable.
- Push/merge intentionally not performed.