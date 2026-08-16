# Ru Python package and persistence migration implementation plan

> Plan 3 of the approved Architecture v2 refactor. Execute sequentially in
> `D:\LLM\.worktrees\ru-architecture-v2`; do not use sub-agents, launch RuApp,
> or expose credential/data contents.

**Goal:** Replace the legacy `mini_gpt`/`ru-local-llama` package identity with
`ru_runtime`/`ru-runtime`, organize Python by owned domain, preserve active
LFM2.5 work from the dirty main checkout, and migrate business data plus
credentials transactionally to `%LOCALAPPDATA%\Ru`.

**Boundaries:** ASP.NET Core remains the sole public host; the 38 HTTP plus 2
WebSocket public contract is unchanged. Worker protocol v1 and OmniVoice HTTP
remain until Plans 4 and 5. Structured memory under `data\conversations` is not
moved. `RU_DATA_ROOT` remains the complete test/isolation override.

## Task 1: Freeze package and overlap contracts

**Files:**

- Create: `tests/test_python_package_architecture.py`
- Verify: dirty main Python/GUI files and untracked LFM2.5 config/scripts
- Modify: this plan

- [x] Record the exact main dirty set and the behavior in every overlapping
  Python diff without modifying main.
- [x] Add a failing package contract requiring distribution `ru-runtime`,
  import root `src/ru_runtime`, new worker launch module, and no active
  `mini_gpt` imports/path.
- [x] Explicitly distinguish package identity from the legacy Credential
  Manager service name, which is migrated only after a read/write/compare gate.

## Task 2: Cut over the package identity and preserve active LFM2.5 work

**Files:**

- Rename: `src/mini_gpt` -> `src/ru_runtime`
- Modify: `pyproject.toml`
- Modify: all active Python imports, monkeypatch module paths, scripts, tests,
  `gui-winui3/Services/BackendLaunchPlan.cs`, and its tests
- Modify: renamed runtime modules overlapping dirty main
- Modify: `scripts/probe_gemini_vision.py`

- [x] Mechanically rename package imports and worker entrypoint in one atomic
  green slice; do not add a long-lived `mini_gpt` shim.
- [x] Rename the distribution to `ru-runtime`, build its wheel, and prove an
  isolated install/import without repointing main's shared editable environment.
- [x] Port the intended dirty-main behaviors with clean formatting and tests:
  optional LFM2.5 LoRA GGUF, default v1 adapter when present, memory listing,
  OmniVoice existing-process wait, bounded Google STT, tokenizer-aware turn
  terminator, and API-key redaction.
- [x] Keep main's original dirty/untracked files untouched and record the branch
  destinations for final reconciliation.
- [x] Run focused tests, full Python, both affected .NET test projects, and
  commit the identity cutover.

## Task 3: Move the flat package into owned domains

**Target package:**

```text
src/ru_runtime/
  evaluation/
  generation/
  memory/
  models/
  observation/
  persistence/
  policy/
  speech/
  voice/
  vts/
  worker/v1/
```

- [x] Add a failing architecture test requiring these packages and rejecting
  production modules at the `ru_runtime` root other than `__init__.py`.
- [x] Move policy/text, persistence, memory, generation/models, and evaluation
  modules first; update imports and focused tests.
- [x] Move voice, speech, VTS, and observation modules; preserve process paths,
  model assets, native-speed audio, VTS privacy, and test seams.
- [x] Move protocol-v1 server/dispatch/runtime-state modules under
  `worker/v1`; update the C# process launch and compatibility harness.
- [x] Use no `shared`, `common`, `helpers`, or `compatibility` dumping package.
- [x] Run all Python and .NET tests, route matrix, and commit each green domain
  slice.

## Task 4: Replace the protected training seam with LFM2.5 tooling

**Files:**

- Create: `tools/training/lfm25/`
- Create: `tools/evaluation/lfm25/`
- Add: `configs/ru_lfm2_5_v1.json` from the protected untracked main input
- Delete: renamed `llama3_1_training.py` compatibility file
- Test: `tests/test_lfm25_training.py`

- [x] Add failing tests for the approved config, LFM2.5 symbol names, Qwen
  `<|im_end|>` preference with EOS fallback, dataset fingerprints, and LoRA
  target modules.
- [x] Move generic training safeguards, rename Llama symbols/messages, remove
  v6-only adapter validators, and add a supported bounded LFM2.5 CLI.
- [x] Preserve the existing `ru_v1` dataset; do not revive deleted v6 source
  merge code. Keep the user's obsolete untracked merge script untouched on main.
- [x] Consolidate useful v1 smoke evaluation behavior with the current
  model-neutral evaluator rather than creating competing adoption gates.
- [x] Run tests without starting training or loading model weights; commit the
  protected-seam retirement.

## Task 5: Implement transactional business-data and credential migration

**Files:**

- Modify: `backend-dotnet/RuBackend/Business/DataPaths.cs`
- Create: `backend-dotnet/RuBackend/Business/Migration/BusinessDataMigrator.cs`
- Create: `backend-dotnet/RuBackend/Business/Settings/CredentialMigrator.cs`
- Modify temporarily: `backend-dotnet/RuBackend/Program.cs`
- Add tests under `backend-dotnet/RuBackend.Tests/Business/`
- Modify Python persistence/VTS paths to the canonical root

- [x] Add failing tests for `%LOCALAPPDATA%\Ru` defaults and unchanged
  `RU_DATA_ROOT` isolation.
- [x] Stage only `settings.json`, `history.json`, `activity_log.jsonl`,
  `pyvts_token.txt`, and `sessions/*.json`; reject links and unexpected nesting.
- [x] Validate JSON/JSONL, file sizes, session count, and SHA-256 equality before
  atomically activating a unique staging directory. Treat an existing empty
  canonical directory as replaceable; never overwrite non-empty canonical data.
- [x] Read the activated files through current Settings/Session/History/Activity
  stores before deleting the legacy directory.
- [x] Migrate expected credentials from service `mini_gpt` to `Ru` by
  read/write/read/constant-time compare; never log values. Delete an old entry
  only after its new entry compares equal. On failure, preserve old state.
- [x] Add a temporary `--migrate-local-state` backend command that performs the
  bounded one-time migration and exits without opening a listener.
- [x] Update Python runtime-state, activity, VTS token, and credential service
  paths to the canonical identity; structured memory stays project-owned.

## Task 6: Execute the local migration and remove temporary compatibility

**Current metadata-only preflight:**

- Legacy `C:\Users\USER\.minigpt`: 22 files, 95,231 bytes.
- Canonical `C:\Users\USER\AppData\Local\Ru`: existing empty directory.
- Legacy credential service: OpenCode and Kilo entries present; Gemini absent.
- New credential service: all three expected entries absent.

- [ ] Recheck these counts/states immediately before execution and fail closed
  on drift, links, a non-empty canonical directory, or unexpected file names.
- [ ] Build and run only the migration command; record counts/hashes/statuses,
  never file contents, credential values, or user text.
- [ ] Prove canonical stores are readable and credential presence moved to
  `Ru`; verify legacy data/service entries are absent only after success.
- [ ] Remove the temporary command, migrator implementations, and every active
  legacy storage/service identifier after the one-time gate passes.
- [ ] Keep validation evidence sufficient to recover from canonical data or Git
  without retaining compatibility code.

## Task 7: Verify and document Plan 3

**Files:**

- Create: `docs/validation/architecture-v2-plan3-package-persistence.md`
- Modify: `README.md`, `CONTEXT.md`, `docs/architecture.md`, operations docs,
  ADR 0004, docs index, and this plan

- [ ] Run full Python, backend .NET, WinUI .NET, route matrix, `pip check`, both
  NuGet vulnerability audits, and `build.bat` sequentially.
- [ ] Verify installed distribution/import identity, no `src/mini_gpt`, no
  active `mini_gpt` import/service/path, and no protected training seam.
- [ ] Verify 38 HTTP plus 2 WebSocket routes and both self-contained executables
  without launching the application.
- [ ] Confirm main's dirty source/untracked files remain untouched and map all
  overlaps to their branch replacements for integration review.
- [ ] Commit validation evidence. Do not merge or push.
