# Ru OmniVoice Isolated Benchmark Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Determine whether OmniVoice can synthesize Ru's authorized cloned voice at least 25% faster than Gwen on the RTX 4050 6 GB GPU, with no GUI/default-runtime/GPU-handoff change.

**Architecture:** A dependency-free benchmark core owns a fixed manifest, timings, WAV validation, resource records, and JSON reports. Gwen and OmniVoice conform to one explicit engine protocol. A PowerShell coordinator runs exactly one engine at a time, refuses a competing Ru Llama process, and releases Gwen after baseline.

**Tech Stack:** Python 3.11, standard-library wave/json/subprocess/ctypes, pytest, Flask, current Gwen HTTP API, OmniVoice local Python API, PyTorch CUDA, nvidia-smi, PowerShell.

## Global constraints

- Gwen remains default. Add no GUI selector, setting, fallback, VTube Studio change, or application-startup service.
- Use only `D:\LLM\tyc_talk.wav` and exact `D:\LLM\reference_text.txt`; never copy, alter, upload, or save another reference recording.
- Create `D:\LLM\omnivoice_env`, never install OmniVoice into `.venv` or `gwen_env`, and confine cache/model data to `D:\LLM\models\omnivoice`.
- The runner may call owned Gwen `/release`, but must never stop arbitrary processes. It reports and aborts if a named Ru Llama process is resident on the GPU.
- Generated WAVs and real reports remain in ignored `eval_results\omnivoice\`; commit only source, tests, manifest, and docs.
- Import/download/CUDA/WAV/synthesis failure is an OmniVoice failure record. Never fall back to Gwen.
- First WAV means a complete playable WAV returned by these non-streaming engines; do not call it streaming/first-token latency.
- Add targeted tests only; do not restore removed legacy tests.

## File structure

- Create `src/mini_gpt/tts_benchmark.py` for manifest validation, engine protocol, trial records, WAV inspection, metrics, and summaries.
- Create `tests/test_tts_benchmark.py` for core contracts using fake engines and temporary WAVs.
- Create `data/evaluations/ru_tts_benchmark_v1.jsonl` for the fixed twelve prompts.
- Create `scripts/benchmark_tts.py` for Gwen/OmniVoice adapters and report writing, with heavy imports lazy.
- Create `tests/test_benchmark_tts_cli.py` for adapter and script contracts without real models, HTTP, GPU, or downloads.
- Modify `gwen_tts_server.py` and create `tests/test_gwen_tts_server.py` for a local service status endpoint.
- Create `scripts/setup_omnivoice_env.ps1` and `scripts/run_omnivoice_benchmark.ps1` for explicit isolated setup and safe sequential runs.
- Modify `README.md` and `docs/ANTIGRAVITY_HANDOFF.md` for the reproducible gate and current candidate status.

## Task 1: Benchmark core and frozen manifest

**Files:** `src/mini_gpt/tts_benchmark.py`, `tests/test_tts_benchmark.py`, `data/evaluations/ru_tts_benchmark_v1.jsonl`.

**Interfaces:**

- `BenchmarkUtterance(id, language, text)` where language is exactly `vi`, `en`, or `vi-en`.
- `TtsBenchmarkEngine.load(reference_audio, reference_text)`, `synthesize(text, output_path)`, and `release()`.
- `load_manifest`, `inspect_wav`, `real_time_factor`, `run_single_sample`, and `summarize`.
- Successful records contain schema version, engine/phase/trial/utterance identity, output path, first playable WAV seconds, total synthesis seconds, audio seconds, RTF, nullable process RAM/GPU peak, and `status: ok`.
- Failed records preserve identity and emit `status: error`, error type, and scrubbed message without invented metrics.

- [ ] Write failing tests that reject malformed JSONL, duplicate IDs, invalid languages, blank text, any count other than 12, and any mix other than six Vietnamese, three English, and three mixed prompts. Test valid-WAV duration, 0.5-second first-WAV timing over two seconds of audio producing RTF 0.25, empty WAV handling, null probes, median warm calculations excluding failures, one cold plus five warm passes, and no fallback after an engine error.
- [ ] Run `& .\.venv\Scripts\python.exe -m pytest tests\test_tts_benchmark.py -q`; it must fail because the module does not exist.
- [ ] Implement strict standard-library JSONL/WAV handling. Reject unreadable, zero-frame, zero-rate, and zero-duration WAVs. Keep first-WAV and total synthesis equal for these non-streaming engines while keeping model/prompt load timing separate.
- [ ] Commit exactly these twelve prompts, in this order: six Vietnamese: `Chào anh, hôm nay anh muốn em giúp việc gì?`; `Em đã ghi nhớ điều đó rồi, để lần sau không hỏi lại anh nha.`; `Ngoài trời có vẻ nóng, anh nhớ uống nước cho em yên tâm.`; `Nếu anh mệt thì mình làm chậm lại một chút cũng được.`; `Em kiểm tra xong rồi, kết quả ổn và không có lỗi mới.`; `Anh gửi thêm chi tiết nhé, em sẽ xử lý ngay.` Three English: `Hey, I am ready whenever you are.`; `That sounds good; I will keep the important details in mind.`; `Give me one moment and I will check it carefully.` Three mixed: `Anh cứ send me the file, em will take a look right away.`; `Em đã finish phần này rồi, now mình kiểm tra kết quả nha.`; `Nếu cần, anh can tell me the priority and em will handle it first.`
- [ ] Rerun the focused test and expect PASS using only fakes and temporary files.
- [ ] Commit exact paths with message `feat: add TTS benchmark core`.

## Task 2: Gwen and OmniVoice adapters plus measurements

**Files:** `scripts/benchmark_tts.py`, `tests/test_benchmark_tts_cli.py`, `gwen_tts_server.py`, `tests/test_gwen_tts_server.py`.

**Interfaces:**

- CLI arguments: engine (`gwen` or `omnivoice`), manifest, output directory, report, and warm-trial count. One CLI invocation keeps its chosen engine loaded for one cold pass followed by every warm pass.
- Gwen uses loopback `GET /status`, `POST /warmup`, `POST /tts`, and `POST /release`; it copies the returned WAV into the supplied ignored output directory.
- `/status` returns only PID and model-loaded boolean. It must not load Gwen, expose reference text, or reveal private paths.
- OmniVoice takes fixed reference inputs, model id `k2-fsa/OmniVoice`, model cache, and `num_step=16`; it writes candidate WAVs only to the supplied output directory.
- A sampler polls only the selected PID: Windows private working-set MiB via `GetProcessMemoryInfo` and GPU MiB via `nvidia-smi`. Probe failure is null, never fabricated zero or unrelated memory.

- [ ] Write failing injected-fake tests: Gwen status/warm profile `ru`/synthesize-copy/release order; no OmniVoice, torch, or soundfile import before `load`; failed records for missing reference, missing HTTP client, non-200 service, missing returned WAV, missing Omni import, CUDA OOM, and unavailable nvidia-smi. Test `/status` via Flask client and prove `get_model` is not called.
- [ ] Run `& .\.venv\Scripts\python.exe -m pytest tests\test_benchmark_tts_cli.py tests\test_gwen_tts_server.py -q`; expect failure because adapters/status do not exist.
- [ ] Implement model-safe imports. Gwen warms once per cold pass with profile `ru`, verifies/copies returned WAV, and releases in `finally`; do not send ignored `fast_mode`. Set `HF_HOME` and `HF_HUB_CACHE` under `models\omnivoice` before Omni imports. Load OmniVoice in float16 on cuda:0, create exactly one clone prompt from the fixed reference pair in `load`, synthesize every prompt with it at `num_step=16`, and write finite audio at the returned sample rate. Download time is excluded; load/prompt time is separately recorded.
- [ ] Poll while blocking load/synthesis is active; no aggregation across PIDs. Rerun the three benchmark/service test files and expect PASS without GPU, service, model, or network.
- [ ] Commit exact paths with message `feat: add Gwen and OmniVoice benchmark adapters`.

## Task 3: Isolated environment and sequential coordinator

**Files:** `scripts/setup_omnivoice_env.ps1`, `scripts/run_omnivoice_benchmark.ps1`, `tests/test_benchmark_tts_cli.py`.

**Interfaces:**

- Setup takes `-ModelCache D:\LLM\models\omnivoice -ConfirmModelDownload`, creates only `D:\LLM\omnivoice_env`, installs a compatibility-probed CUDA Torch/OmniVoice pair, then installs the project editable with `--no-deps` for shared benchmark code.
- Coordinator takes timestamped `-OutputRoot`, `-ConfirmModelDownload`, optional `-OverwriteOutput`, and `-WhatIf`. It performs Gwen cold plus five warm passes, releases Gwen, then matching OmniVoice passes.
- WhatIf resolves input paths and prints commands only: no download, CUDA model, service start, directory output, or state change.

- [ ] Write failing script-contract tests asserting explicit confirmation, both engine names, no `gwen_env` installation target, WhatIf, and overwrite rejection without explicit approval.
- [ ] Run the CLI test file; expect failure because scripts do not exist.
- [ ] Implement setup using `D:\LLM\gwen_env\Scripts\python.exe` only as the known Python 3.11 executable to create a new environment, never as an install target. Constrain caches with per-process variables and write resolved package versions to final metadata. The actual first model fetch must wait for the confirmed candidate run.
- [ ] Implement coordinator to resolve the fixed pair/manifest; detect only named Ru Llama processes (`llama-server`, `llama-cli`, local Transformers runner) and abort if one holds GPU compute; establish the owned Gwen baseline then release it in finally; invoke the Omni CLI from `omnivoice_env`; write summary JSON with metadata, report paths, medians, failed records, cache state, and cleanup command; and leave GUI/default voice unchanged.
- [ ] Rerun CLI tests and run `powershell -ExecutionPolicy Bypass -File .\scripts\run_omnivoice_benchmark.ps1 -WhatIf`; expect PASS and printed commands only.
- [ ] Commit exact paths with message `feat: add isolated OmniVoice benchmark runner`.

## Task 4: Documentation, regression checks, and approved hardware gate

**Files:** `README.md`, `docs/ANTIGRAVITY_HANDOFF.md`, benchmark test files.

- [ ] Verify the pre-edit state with fixed-string searches for `run_omnivoice_benchmark.ps1`, `OmniVoice`, and `Gwen remains the default`; confirm the handoff still calls Irodori-Sen-TTS the preferred next candidate.
- [ ] Document setup/run commands with explicit download confirmation, local-only ignored output, cache location, cold load versus first playable WAV versus RTF, and the reject gate: no failed run, no competing Ru model, at least 25% better median warm first-WAV latency or median warm RTF, plus manual approval of clone identity, Vietnamese, English, and mixed quality. State that Gwen remains default until every gate passes.
- [ ] Replace only the obsolete preferred-next-candidate paragraph in `ANTIGRAVITY_HANDOFF.md` with the current isolated OmniVoice status. Preserve historical measurements.
- [ ] Run `& .\.venv\Scripts\python.exe -m pytest -q`, then `& .\.venv\Scripts\python.exe -m compileall -q src scripts gwen_tts_server.py`, then the WhatIf preflight. All must pass before a real run.
- [ ] After explicit first-download approval, run the real benchmark and manually listen to Vietnamese, English, and mixed output from both engines. Record only `accepted`, `rejected`, or `blocked` in summary JSON; numeric measurements alone cannot produce accepted.
- [ ] Commit source/docs only with message `docs: document OmniVoice benchmark gate`. Do not commit models, environments, WAVs, or ignored live reports. A passing candidate still requires a separate design before GUI/runtime adoption.

## Plan self-review

- Tasks 1-2 cover deterministic bilingual inputs, failure-safe reports, reference integrity, separate cold-load and synthesis metrics, and process-scoped resource evidence.
- Task 3 isolates dependencies/cache, prevents arbitrary process control, applies equal cold/five-warm trials, and makes downloads explicit.
- Task 4 updates the stale candidate documentation, requires complete regression/hardware evidence and manual bilingual listening, and keeps Gwen unchanged on every non-pass outcome.
- The plan neither restores old tests nor claims a returned WAV is streaming, and it leaves GUI/VTube/memory paths untouched.
