# Ru legacy removal and workspace hygiene implementation plan

> Plan 2 of the approved Architecture v2 refactor. Execute sequentially in
> `D:\LLM\.worktrees\ru-architecture-v2`; do not launch RuApp or VTube Studio.

**Goal:** Retire the tracked Llama 3.1/v6 implementation and approved local
artifacts without losing current Ru evaluation behavior, user-owned LFM2.5
work, operational decisions, or recoverability evidence.

**Architecture boundary:** ASP.NET Core remains the only public host. This plan
does not rename the Python package, change the worker protocol, migrate
business data, or replace the OmniVoice transport. Those are later plans.

**Protected exception:** The dirty main-checkout file
`src/mini_gpt/llama3_1_training.py` remains present until Plan 3 migrates its
generic LFM2.5 token-ending behavior. The active `llama_cpp_runtime.py`,
`tools/llama.cpp`, LFM2.5 models, `ru_v1` data, and every dirty/untracked main
path remain protected.

## Task 1: Revalidate the deletion boundary

**Files:**

- Verify: `D:\LLM\artifacts\cleanup\ru-architecture-v2-preflight.json`
- Generate: `artifacts/cleanup/ru-architecture-v2-plan2-before.json` (ignored)
- Verify: `docs/validation/architecture-v2-cleanup-preflight.md`

- [ ] Confirm the feature worktree is clean and the main checkout still has
  exactly the recorded dirty/untracked paths.
- [ ] Regenerate the read-only manifest against `D:\LLM`.
- [ ] Fail closed if the protected dirty file, any LFM2.5 path, scan errors, or
  the seven permanent legacy targets differ from the approved preflight.
- [ ] Record exact resolved paths; reject links, drive roots, broad globs, and
  any target outside `D:\LLM`.

## Task 2: Move generic evaluation behavior out of the legacy namespace

**Files:**

- Rename: `src/mini_gpt/ru_llama_evaluation.py` ->
  `src/mini_gpt/ru_evaluation_cases.py`
- Modify: `src/mini_gpt/ru_judge_evaluation.py`
- Modify: `tests/test_ru_judge_evaluation.py`
- Modify: `tests/test_ru_style.py`
- Test: `tests/test_ru_evaluation_cases.py`

- [ ] Add a characterization import test for the neutral module and run it to
  observe the expected failure.
- [ ] Move the implementation without changing suites, case IDs, routing, or
  deterministic assessment behavior; update current judge imports.
- [ ] Keep the active prompt/persona tests but remove the test that couples
  `ru_style` to the retired v6.3 authored corpus.
- [ ] Run focused evaluation/style tests, then commit the neutral seam.

## Task 3: Add a fail-closed legacy retirement contract

**Files:**

- Create: `tests/test_legacy_retirement.py`
- Modify: `.github/workflows/ci.yml`
- Modify: `tests/test_ci_contract.py`
- Modify: `tools/maintenance/cleanup_policy.py`
- Modify: `tests/test_cleanup_policy.py`
- Modify: `tools/maintenance/cleanup_inventory.py`
- Modify: `tests/test_cleanup_inventory.py`

- [ ] Add an exact tracked legacy path set and prove the test fails while the
  files still exist.
- [ ] Assert current Python and CI no longer import or execute v6 corpus and
  Llama-specific evaluation code after retirement.
- [ ] Explicitly allow the protected training file and active llama.cpp runtime
  instead of relying on a broad name grep.
- [ ] Retain cleanup inventory as a read-only maintenance tool, but remove the
  resolved shared-judge review exception and protect both Architecture v2 plans.

## Task 4: Delete the tracked Llama 3.1/v6 implementation

**Files:**

- Delete: the exact tracked config/script/module/test set in
  `docs/validation/architecture-v2-cleanup-preflight.md`
- Modify: `.github/workflows/ci.yml`
- Modify: `README.md`

- [ ] Delete only the approved tracked set with patch-based Git-recoverable
  changes; keep `src/mini_gpt/llama3_1_training.py` untouched.
- [ ] Remove corpus-byte verification from CI in the same change as the corpus
  builder/verifier.
- [ ] Replace README's historical v6 inventory/training claims with the current
  LFM2.5 runtime, active evaluation command, and explicit Plan 3 training seam.
- [ ] Run the retirement, CI-contract, judge, style, and complete Python suites.
- [ ] Commit tracked legacy retirement only after green tests.

## Task 5: Consolidate current documentation and retire dated records

**Files:**

- Create: `docs/operations/runtime-behavior.md`
- Modify: `docs/README.md`
- Modify: `docs/architecture.md`
- Modify: `docs/operations/verification.md`
- Modify: `tests/test_current_documentation.py`
- Delete: the 34 dated records listed in the cleanup preflight

- [ ] Capture still-current LFM2.5, observation-disabled, voice native-speed,
  playback/subtitle isolation, VTS privacy, ownership, and human-acceptance
  rules in current documents.
- [ ] Exclude rejected pitch/style experiments, unimplemented Discord plans,
  FastAPI migration staging, old route relays, and historical Llama metrics.
- [ ] Add a documentation contract that links every current document and
  rejects stale FastAPI/Llama/v6 operating instructions in README/current docs.
- [ ] Delete the 34 non-authoritative implementation records only after their
  current decisions are represented in the current spine.
- [ ] Keep the umbrella design, Plan 1, this plan, ADRs, and validation evidence.

## Task 6: Resolve reviewed local clutter and apply permanent cleanup

**Files/paths:**

- Review: `gui-winui3.bak`, `.tmp/vcpkg`, `eval_results/judge`
- Delete if proven obsolete: one-off probe scripts, duplicate reference text,
  unused voice sample, runtime logs, approved rebuildable outputs
- Permanently delete: the seven ignored legacy targets in the preflight
- Generate: `artifacts/cleanup/ru-architecture-v2-plan2-after.json` (ignored)

- [ ] Compare backup-only source against current/Git history before deciding;
  preserve any unique behavior that cannot be proven obsolete.
- [ ] Do not delete or move `.tmp/vcpkg` until its active build reference has an
  intentional replacement and a verified build path; otherwise keep it with a
  documented reason.
- [ ] Preserve current LFM2.5 judge evidence; remove only reports proven to be
  legacy by metadata.
- [ ] Immediately before permanent deletion, rerun the manifest and main dirty
  check, resolve every target, prove it is under `D:\LLM`, and verify it is not
  a filesystem link.
- [ ] Use exact PowerShell `-LiteralPath` targets. Report bytes/files removed
  and distinguish Git-recoverable tracked changes from permanent local removal.
- [ ] Remove approved generated outputs only; never remove `.venv`,
  `omnivoice_env`, models, active LFM2.5 checkpoints/data, credentials, or app
  data.

## Task 7: Verify and record Plan 2

**Files:**

- Create: `docs/validation/architecture-v2-plan2-retirement.md`
- Modify: `docs/README.md`
- Modify: this plan

- [ ] Run Python, backend .NET, WinUI .NET, route matrix, `pip check`, NuGet
  vulnerability audit, and `build.bat` sequentially.
- [ ] Confirm route counts remain 38 HTTP and 2 WebSocket and builds create both
  executables without launching them.
- [ ] Regenerate the post-cleanup inventory and record every deliberately kept
  review item.
- [ ] Confirm the feature worktree has only intended commits and the main dirty
  set is unchanged except for explicitly approved ignored-artifact deletion.
- [ ] Commit validation evidence. Do not merge or push.
