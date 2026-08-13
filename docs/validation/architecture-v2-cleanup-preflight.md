# Architecture v2 cleanup preflight

Status: completed snapshot taken before Plan 2. Final retirement and cleanup
evidence is recorded in
[`architecture-v2-plan2-retirement.md`](architecture-v2-plan2-retirement.md).

Status: verified, non-destructive preflight on 2026-08-13.

## Scan identity and privacy

- Scanned workspace: `D:\LLM`.
- Scanned Git commit: `b799cf472415e7bd8581eb36eb05e5349321e242`.
- Manifest schema: `1`.
- Ignored machine manifest: `artifacts/cleanup/ru-architecture-v2-preflight.json`
  in the isolated `codex/ru-architecture-v2` worktree.
- Dirty or untracked Git paths observed: 16. Their contents are not present in
  this document or the manifest.
- Filesystem scan errors: 0.
- Duplicate or out-of-workspace entries: 0.
- The manifest contains paths, classifications, sizes, timestamps, active
  reference results, and recoverability only. It contains no prompts, dataset
  rows, credentials, tokens, transcripts, audio, request bodies, session IDs,
  or private voice/VTS metadata.
- Files deleted by this plan: 0.

## Classification totals

| Classification | Entries | Files | Bytes | Approximate size |
| --- | ---: | ---: | ---: | ---: |
| `legacy_llama` | 56 | 72 | 40,233,528 | 38.37 MiB |
| `protected` | 1 | 1 | 28,373 | 0.03 MiB |
| `rebuildable` | 26 | 40,416 | 2,324,198,071 | 2.165 GiB |
| `review_required` | 63 | 19,024 | 1,738,729,706 | 1.619 GiB |
| **Total** | **146** | **59,513** | **4,103,189,678** | **3.822 GiB** |

These totals are inventory size, not an immediately deletable-byte claim.
Protected and review-required entries are excluded from automatic deletion.

## Verification results

All gates ran sequentially from the isolated worktree on 2026-08-13:

| Gate | Result |
| --- | --- |
| New/current repository contracts | 75 passed |
| Full Python suite | 475 passed, 1 known third-party `standard-aifc` deprecation warning |
| ASP.NET Core backend | 219 passed |
| WinUI | 94 passed |
| Public route contract | 38 HTTP routes, 2 WebSocket routes |
| Python dependency consistency | No broken requirements |
| NuGet vulnerability audit | No vulnerable packages |
| Self-contained Release build | Passed; `RuBackend.exe`, `RuApp.exe`, and `RuApp.pri` produced |

The clean worktree initially exposed two v6 manifest-hash failures because the
legacy gate hashed an absolute checkout path. The verifier now canonicalizes
only the `base_adapter` checkout prefix; train, validation, and held-out hashes
were unchanged, and all four legacy corpus versions passed afterward. No app
or user-facing process was launched.

## Protected groups

The policy protects these roots before applying any legacy-name rule:

- `.git`, `.worktrees`, `.venv`, and `omnivoice_env`;
- active LFM2.5 HF/GGUF assets, OmniVoice, vision, and voice references;
- `checkpoints/ru-lfm2.5-2.6b-v1` and `data/conversations/ru_v1`;
- approved examples, settings, sessions, history, activity, credentials,
  `data/memory.db`, and structured-memory databases and sidecars;
- the architecture-v2 umbrella spec and this Plan 1;
- every path that is dirty/untracked, contains one, or is contained by one.

The actual candidate promoted to protected was
`src/mini_gpt/llama3_1_training.py`, because that tracked legacy file has an
uncommitted user change. Plan 2 must preserve it and any dependent decision
until the overlap is explicitly resolved.

## Exact legacy set approved for Plan 2

Tracked entries are recoverable from Git. Ignored checkpoints, datasets, and
evaluation files are permanent deletion targets and require a fresh
active-reference/dirty-path check immediately before deletion.

### Ignored local assets

- `checkpoints/ru-hard-evaluation-v1`
- `checkpoints/ru-llama3.1-8b-instruct-v6.2-fresh`
- `data/conversations/ru_v6_2`
- `data/conversations/ru_v6_2_fresh`
- `data/conversations/ru_v6_3`
- `eval_results/ru_llama3_1_v6_style.json`
- `eval_results/ru_v6_eval.json`

### Tracked configs and scripts

- `configs/ru_llama3_1_8b.json`
- `configs/ru_llama3_1_8b_v6.json`
- `configs/ru_llama3_1_8b_v6_1.json`
- `configs/ru_llama3_1_8b_v6_1_smoke.json`
- `configs/ru_llama3_1_hard_eval.json`
- `configs/ru_llama3_1_v6_2_fresh.json`
- `configs/ru_llama3_1_v6_3.json`
- `scripts/audit_ru_v6_1_naturalness.py`
- `scripts/benchmark_ru_v6_gguf.py`
- `scripts/build_corpus.py`
- `scripts/build_ru_v6_gguf.py`
- `scripts/chat_ru_llama3_1.py`
- `scripts/corpus_baseline_hashes.json`
- `scripts/download_llama3_1_hf.py`
- `scripts/evaluate_ru_llama3_1_hard.py`
- `scripts/evaluate_ru_llama3_1_prompt_routing.py`
- `scripts/evaluate_ru_llama3_1_qlora.py`
- `scripts/evaluation_support.py`
- `scripts/merge_approved_into_ru_v6_1.py`
- `scripts/train_ru_llama3_1.py`
- `scripts/verify_corpus_byte_identical.py`

### Tracked Python modules and tests

- `src/mini_gpt/_base_corpus.py`
- `src/mini_gpt/_corpus_quality.py`
- `src/mini_gpt/approved_v6_1_import.py`
- `src/mini_gpt/corpus_builder.py`
- `src/mini_gpt/corpus_common.py`
- `src/mini_gpt/corpus_validate.py`
- `src/mini_gpt/llama3_1_runtime.py`
- `src/mini_gpt/ru_hard_evaluation.py`
- `src/mini_gpt/ru_v6_1_corpus.py`
- `src/mini_gpt/ru_v6_1_dialogues.py`
- `src/mini_gpt/ru_v6_1_naturalness.py`
- `src/mini_gpt/ru_v6_2_corpus.py`
- `src/mini_gpt/ru_v6_2_dialogues.py`
- `src/mini_gpt/ru_v6_2_fresh_corpus.py`
- `src/mini_gpt/ru_v6_2_fresh_dialogues.py`
- `src/mini_gpt/ru_v6_2_fresh_import.py`
- `src/mini_gpt/ru_v6_3_corpus.py`
- `src/mini_gpt/ru_v6_3_dialogues.py`
- `src/mini_gpt/ru_v6_corpus.py`
- `tests/test_build_corpus_cli.py`
- `tests/test_corpus_byte_identical.py`
- `tests/test_ru_hard_evaluation.py`
- `tests/test_ru_v6_2_fresh_corpus.py`
- `tests/test_ru_v6_2_fresh_dialogues.py`
- `tests/test_ru_v6_2_fresh_import.py`
- `tests/test_ru_v6_3_corpus.py`
- `tests/test_ru_v6_3_dialogues.py`
- `tests/test_training_config.py`

`llama_cpp_runtime.py`, its tests, `tools/llama.cpp`, and the active LFM2.5
GGUF are not legacy targets: the current LFM2.5 runtime materially uses
llama.cpp. `src/mini_gpt/ru_llama_evaluation.py` is also not in the deletion
set yet because `ru_judge_evaluation.py` imports it; it remains review-required
until its generic judge cases are renamed or moved.

## Exact rebuildable set

These are generated/cache outputs, not user data. Plan 2 may recreate and then
remove them in a scoped cleanup commit or local cleanup step:

- `.pytest_cache`
- `.venv311`
- `__pycache__`
- `backend-dotnet/RuBackend.Tests/bin`
- `backend-dotnet/RuBackend.Tests/obj`
- `backend-dotnet/RuBackend/bin`
- `backend-dotnet/RuBackend/obj`
- `gui-winui3.Tests/bin`
- `gui-winui3.Tests/obj`
- `gui-winui3/bin`
- `gui-winui3/obj`
- `gui-winui3/pri_staging`
- `pytest_v.log`
- `resources.pri.xml`
- `rustup-init.exe`
- `scripts/__pycache__`
- `src/bilingual_mini_gpt.egg-info`
- `src/mini_gpt/__pycache__`
- `src/ru_local_llama.egg-info`
- `tests/__pycache__`
- `tmp_verify_fresh`
- `tmp_verify_fresh2`
- `tmp_verify_fresh3`
- `tools/llama.cpp/conversion/__pycache__`
- `tools/llama.cpp/gguf-py/gguf/__pycache__`
- `tools/ru_desktop_overlay/out`

## Exact review-required set

None of these paths is authorized for automatic deletion by Plan 1.

### Active build/cache seam

- `.tmp/vcpkg` — the native overlay build still defaults to this path. Plan 2
  must first move it to an intentional cache or prove clean bootstrap/build.

### Runtime logs that may contain private text

- `.tmp/lfm25-auth-chat.err.log`
- `.tmp/lfm25-auth-chat.out.log`
- `.tmp/lfm25-auth.err.log`
- `.tmp/lfm25-auth.out.log`
- `.tmp/lfm25-budget-zero.err.log`
- `.tmp/lfm25-budget-zero.out.log`
- `.tmp/lfm25-native-parser.err.log`
- `.tmp/lfm25-native-parser.out.log`
- `.tmp/lfm25-reasoning-off.err.log`
- `.tmp/lfm25-reasoning-off.out.log`
- `.tmp/lfm25-smoke-ascii.err.log`
- `.tmp/lfm25-smoke-ascii.out.log`
- `.tmp/lfm25-smoke.err.log`
- `.tmp/lfm25-smoke.out.log`
- `logs`

### Backup, mixed evaluation, and shared dependency

- `gui-winui3.bak` — five non-generated source-only paths require review:
  `Program.cs` and `Views/ChatView.xaml.full` have no Git history;
  `Services/ScreenObserverService.cs` is recoverable at `f016068`; and
  `Views/ObservationControlView.xaml` plus its `.xaml.cs` are recoverable at
  `3526dc0`. Generated `obj` copies are not source-only evidence.
- `eval_results/judge` — mixed judge outputs; classify individual reports only
  after current LFM2.5 judge evidence has a canonical destination.
- `src/mini_gpt/ru_llama_evaluation.py` — shared by the current generic judge
  evaluator and must be refactored before any legacy-name cleanup.

### Broken one-off probes and duplicate/unused assets

- `scripts/test_inject.py`
- `scripts/test_keys.py`
- `scripts/test_keys_raw.py`
- `scripts/test_post_settings.py`
- `scripts/test_rest_key.py`
- `scripts/test_rest_key2.py`
- `scripts/test_settings.py`
- `scripts/test_tts.py`
- `src/mini_gpt/reference_text.txt`
- `tyc_calm.wav`

### Dated implementation records

These 34 records are non-authoritative. Plan 2 may remove one only after any
still-current decision has been captured in the current architecture, ADRs, or
operations documents.

- `docs/superpowers/plans/2026-08-07-cloud-voice-first-audio.md`
- `docs/superpowers/plans/2026-08-07-omnivoice-measurable-pitch.md`
- `docs/superpowers/plans/2026-08-07-omnivoice-native-speed-recovery.md`
- `docs/superpowers/plans/2026-08-07-omnivoice-voice-style-presets.md`
- `docs/superpowers/plans/2026-08-07-runtime-hardening.md`
- `docs/superpowers/plans/2026-08-07-voice-quality-latency.md`
- `docs/superpowers/plans/2026-08-09-disable-local-observation.md`
- `docs/superpowers/plans/2026-08-09-lfm2-5-model-replacement.md`
- `docs/superpowers/plans/2026-08-09-reliability-repair.md`
- `docs/superpowers/plans/2026-08-09-ru-discord-voice.md`
- `docs/superpowers/plans/2026-08-09-ru-vts-subtitle-voice-isolation.md`
- `docs/superpowers/plans/2026-08-10-ru-aspnetcore-backend-migration.md`
- `docs/superpowers/plans/2026-08-10-ru-aspnetcore-chat-orchestration.md`
- `docs/superpowers/plans/2026-08-10-ru-aspnetcore-deterministic-business-stores.md`
- `docs/superpowers/plans/2026-08-10-ru-aspnetcore-fastapi-decommission.md`
- `docs/superpowers/plans/2026-08-10-ru-aspnetcore-full-compatibility-and-cutover.md`
- `docs/superpowers/plans/2026-08-10-ru-aspnetcore-memory-ownership-migration.md`
- `docs/superpowers/plans/2026-08-10-ru-aspnetcore-worker-protocol.md`
- `docs/superpowers/plans/2026-08-11-ru-judge-evaluation.md`
- `docs/superpowers/plans/2026-08-11-ru-stt-vts-runtime-ownership.md`
- `docs/superpowers/specs/2026-08-07-cloud-voice-first-audio-design.md`
- `docs/superpowers/specs/2026-08-07-omnivoice-measurable-pitch-design.md`
- `docs/superpowers/specs/2026-08-07-omnivoice-native-speed-recovery-design.md`
- `docs/superpowers/specs/2026-08-07-omnivoice-voice-style-presets-design.md`
- `docs/superpowers/specs/2026-08-07-voice-quality-latency-design.md`
- `docs/superpowers/specs/2026-08-08-ru-persona-rework-design.md`
- `docs/superpowers/specs/2026-08-09-disable-local-observation-design.md`
- `docs/superpowers/specs/2026-08-09-reliability-repair-design.md`
- `docs/superpowers/specs/2026-08-09-ru-discord-voice-design.md`
- `docs/superpowers/specs/2026-08-09-ru-vts-subtitle-design.md`
- `docs/superpowers/specs/2026-08-10-ru-aspnetcore-backend-migration-design.md`
- `docs/superpowers/specs/2026-08-10-ru-aspnetcore-full-compatibility-and-cutover-design.md`
- `docs/superpowers/specs/2026-08-10-ru-aspnetcore-memory-business-migration-design.md`
- `docs/superpowers/specs/2026-08-11-ru-judge-evaluation-design.md`

## Plan 2 deletion gate

Immediately before any deletion, Plan 2 must regenerate this manifest and
fail closed if the Git dirty set, active references, protected roots, or
classification changes. It must treat tracked deletion as Git-recoverable and
ignored checkpoint/dataset/evaluation deletion as permanent. It must not use a
recursive delete against a computed path until every resolved absolute target
has been checked to remain under `D:\LLM` and matched to the approved manifest.
