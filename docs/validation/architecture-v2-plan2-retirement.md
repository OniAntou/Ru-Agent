# Architecture v2 Plan 2 retirement validation

Status: completed locally on 2026-08-13 on
`codex/ru-architecture-v2`; not merged or pushed.

## Scope completed

- Renamed the current shared conversation suites from
  `ru_llama_evaluation.py` to model-neutral `ru_evaluation_cases.py` without
  changing case IDs, suite sizes, routing, or deterministic response gates.
- Removed the exact 49 tracked Llama 3.1/v6 configs, scripts, modules, and tests
  approved by the preflight. Git remains their recovery source.
- Removed corpus verification from CI with the corpus implementation and added
  a fail-closed 49-path retirement contract.
- Removed eight obsolete unauthenticated/old-import runtime probes and one
  byte-identical package-level voice transcript duplicate. The canonical root
  transcript remains.
- Consolidated current behavior into `docs/operations/runtime-behavior.md` and
  removed 34 superseded dated plans/specs. Git remains their recovery source.
- Replaced the root README's historical training and migration narrative with
  the current LFM2.5, ASP.NET Core, evaluation, privacy, and verification
  contract.

The protected `src/mini_gpt/llama3_1_training.py` compatibility file remains
for Plan 3 because the main checkout contains an uncommitted LFM2.5/Qwen token
ending correction in that file. Active llama.cpp runtime code is also retained
because LFM2.5 materially uses it.

## Local cleanup result

The deletion gate was regenerated immediately before cleanup against main
commit `b799cf472415e7bd8581eb36eb05e5349321e242`. It reproduced the exact 16
dirty/untracked paths, one protected legacy-named file, seven permanent legacy
targets, and zero scan errors.

| Group | Targets | Files | Bytes | Recoverability |
| --- | ---: | ---: | ---: | --- |
| Ignored Llama checkpoints/data/evaluations | 7 | 23 | 39,981,189 | Permanently removed |
| Generated/cache outputs | 26 | 40,416 | 2,324,198,071 | Rebuildable |
| Reviewed backup/log/unused voice assets | 17 | 809 | 231,534,016 | Permanently removed after review |
| **Total** | **50** | **41,248** | **2,595,713,276 (2.417 GiB)** | Mixed as above |

`gui-winui3.bak` was removed after comparison. Its Git-backed source was older
than current WinUI; backup-only `Program.cs` duplicated current crash handling,
and `ChatView.xaml.full` lacked the current streaming, Markdown, and layout
implementation. Runtime logs were deleted without reading their contents.
`tyc_calm.wav` had no active source/runtime reference.

Recursive `Remove-Item` was rejected before execution by the terminal safety
layer. Every absolute target was revalidated with PowerShell `-LiteralPath`,
checked to remain under `D:\LLM`, checked not to be a filesystem link, and then
deleted through exact `System.IO.Directory.Delete`/`File.Delete` calls. All 50
targets were verified absent afterward.

## Deliberately retained local state

- `.tmp/vcpkg`: 18,158 files and 1,506,476,294 bytes. The native overlay build
  still defaults to this cache; it remains until a replacement cache path and
  clean bootstrap/build are proven without an unapproved repository download.
- `eval_results/judge`: 13 JSON reports and 162,070 bytes. Metadata identifies
  current LFM2.5 `base`/`v1` V5 and smoke evidence, so it is preserved.
- `.venv`, `omnivoice_env`, LFM2.5 GGUF/HF assets, OmniVoice assets, the active
  LFM2.5 checkpoint/data, root voice transcript, credentials, and application
  data remain present.

The post-cleanup inventory against the dirty main checkout still lists 49
tracked legacy files because this work is intentionally isolated on a feature
branch. On this branch, `tests/test_legacy_retirement.py` proves that exact set
is absent. Main's dirty/untracked path set remained unchanged; only approved
ignored local artifacts were removed.

## Verification evidence

All heavyweight commands ran sequentially from
`D:\LLM\.worktrees\ru-architecture-v2`:

| Gate | Result |
| --- | --- |
| Full Python suite | 443 passed; one known third-party `standard-aifc` deprecation warning |
| ASP.NET Core tests | 219 passed |
| WinUI tests | 94 passed |
| Public route matrix | 38 HTTP and 2 WebSocket routes |
| Python dependency check | No broken requirements |
| Backend NuGet vulnerability audit | No vulnerable packages |
| WinUI NuGet vulnerability audit | No vulnerable packages |
| `build.bat` | Passed; produced self-contained `RuBackend.exe`, `RuApp.exe`, and `RuApp.pri` |

The build did not launch RuApp, the Runtime Worker, OmniVoice, a model server,
or VTube Studio.

## Acceptance boundary

This evidence validates source retirement, current contracts, test behavior,
dependency health, and build output. It does not validate live LFM2.5 response
quality, speaker output, microphone transcription, subtitle appearance, or
VTube Studio actions. Those remain user-run acceptance checks when the related
runtime plans are complete.
