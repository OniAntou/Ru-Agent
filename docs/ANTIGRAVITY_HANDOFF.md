# Ru Llama Handoff

> Status: historical model-pipeline handoff. Runtime launch and GUI details
> below are superseded by `README.md` and the 2026-08-01 hardening plan.

## Active scope

Ru is now a Llama-only local project. Keep the Llama 3.1 8B Instruct base,
the v6 QLoRA adapter, its corpus/training path, and the Tauri GUI. Qwen,
Gemma, MiniGPT, and other non-Llama experiments were removed on 2026-07-26.

## Current runtime

- Base: `models\llama3.1-8b-instruct-hf`
- GUI default: `models\gguf\ru-llama3.1-8b-instruct-v6.2-lora.gguf`
- GUI GGUF fallback order: v6.1 LoRA, then v6 LoRA
- Transformers fallback checkpoint order: v6.2 fresh, v6.1, then v6
- Launch: `& .\start_dev.bat` (Tauri owns the authenticated backend child)
- Tests: `& .\.venv\Scripts\python.exe -m pytest -q`

Persistent memory is enabled by default and remains local in
`data\conversations\memory.db`; users can turn it off in the GUI. It extracts
only explicit preferences and scoped project decisions, rejects credentials,
URLs, sensitive data, and VTube control metadata, then provides bounded relevant
facts to inference. Clearing saved memory affects only local memory artifacts
and never trains the adapter. Verify this behavior with
`& .\.venv\Scripts\python.exe scripts\evaluate_ru_memory.py`.

The GUI worker and Tk thread access structured memory through thread-local
SQLite connections guarded by the store lock. Do not pass a raw SQLite
connection between those threads.

## v6.2 fresh dataset status

The fresh candidate is at `data\conversations\ru_v6_2_fresh`. It has 1,200
authored rows and 39 accepted train-only calibration imports from
`approved.jsonl` plus `memory_texts.json`; validation and held-out remain
authored-only (759/240/240 final split). Forty-four source rows were rejected
with recorded reasons, including duplicates, malformed/noisy text, AI-meta,
VTS control markers, links/lists, and unsafe explicit content. The historical
`ru_v6_2` directory was not modified.

The bounded v6.2 run completed 480/480 steps on 2026-07-28 in about 2,630
seconds, with final validation loss 0.2060. Its adapter and manifest are at
checkpoints\\ru-llama3.1-8b-instruct-v6.2-fresh. The manifest records the
driver-side physical-VRAM sample as unavailable and the CUDA allocator peak as
8,445 MiB, so the physical-VRAM gate is unverified, not passed. No adapter
promotion has happened.

The original launch command was:

```powershell
& .\.venv\Scripts\python.exe scripts\train_ru_llama3_1.py --config configs\ru_llama3_1_v6_2_fresh.json
```

The 12-case bilingual runtime/held-out gate and a verified hardware measurement
remain required before calling v6.2 quality-gate promoted. The v6.2 GGUF is the
operational GUI default by explicit user choice; v6 remains the HF fallback.

## v6.3 targeted dataset status

The new project-owned dataset is at `data\conversations\ru_v6_3`. It contains
180 authored rows split 108/36/36 into train/validation/held-out, with repair,
bilingual-bridge, and context-recall strata. Its authored SHA-256 is
`d757882ea70ad5b1951a0e94a0012ecfae5c95b5fbbc677f20061bc40f9b7d8b`.
The corpus is new and does not copy v6.2 rows. Training has not started; use
`configs\ru_llama3_1_v6_3.json`, which caps the run at 360 steps and 9,600
seconds. The rows carry bounded profanity metadata (`none=155`, `mild=16`,
`medium=9`) and the active Ru contract does not force a `Nya~` suffix.

## Current comparison

The deterministic 12-case QLoRA suite was rerun with greedy decoding and 64
new tokens per case. Results: v6 `11/12` (one Vietnamese context-work
out-of-context refusal), v6.1 `12/12`, and v6.2 `12/12`. v6.2 was noticeably
shorter and consistently ended with `Nya~` under the historical v6.2 prompt
contract; the active contract no longer forces that suffix. Its mixed-language
turns still need manual naturalness review. The inference allocator peak was
5,686.78 MiB in
each run. These automatic results do not promote v6.2 because its training
physical-VRAM measurement remains unverified.

The harder `ru_hard_v1` suite contains 36 authored cases across context,
bilingual switching, and personality repair (SHA-256
`648d5522a4d8096e33a4f06a7346e96693e7241633cc6ac628ea1bd421c0e84c`). It was
run with greedy decoding and 128 new tokens, one adapter process at a time.
Scores: v6 `158/360` with 13 hard fails, v6.1 `180/360` with 11 hard fails,
and v6.2 `205/360` with 7 hard fails. Automatic ranking is v6.1, v6.2, v6,
but all three fail the strict zero-hard-fail/80%-per-stratum gate. v6.2 has the
strongest repair-stratum score and no explicit-content hard fail, while v6
shows explicit-content and context failures. Keep v6 as fallback; the reports
are in `checkpoints\\ru-hard-evaluation-v1` and manual review is still required.

## Promotion rule

Do not call a new adapter quality-gate promoted based on loss alone. It must
pass the bilingual conversation gate, preserve Ru's Vietnamese `anh`/`em`
voice, and stay within the physical VRAM cap. The v6.2 GGUF is nevertheless
the current operational GUI default by explicit user choice, while v6 remains
the HF fallback until that evidence exists.

## v6.1 training status

Current status (2026-07-28): the bounded continuation completed 640 steps (40
optimizer updates) in 8,329.9 seconds. Its manifest reports validation loss
0.8862 and a 5,900 MiB physical peak under the 6,144 MiB cap; the comparison
suite passed all 12 cases. The older interruption notes below are historical
and must not be used as the current v6.1 status.

The v6.1 continuation run was deliberately stopped on 2026-07-26 after about
three hours because the current loop would take roughly two days: it performs
5,040 single-example forward/backward passes and validates all 360 validation
rows every 10 optimizer updates. The process was PID 3000 and the GPU was
confirmed released after it stopped.

`checkpoints\ru-llama3.1-8b-instruct-v6.1` contains an interrupted,
best-validation adapter snapshot from 18:17. It is not a completed candidate
and must not replace the current v6.2 GGUF GUI default.

The next v6.1 candidate is bounded to 640 single-example steps (40 optimizer
updates). During training it evaluates a deterministic 32-row validation
sample every 20 updates, instead of all 360 rows every 10 updates. This is a
progress signal only: the full held-out bilingual conversation and physical
VRAM gates still decide promotion. The 32-step smoke run completed on
2026-07-26 in 543 seconds (two updates and one 32-row validation), with a
physical peak of 5,844 MiB under the 6,144 MiB cap. It establishes a
conservative roughly 3–3.5 hour window for the bounded 640-step candidate;
the smoke adapter remains only a runtime measurement artifact.

The 640-step launch was stopped at the user's request on 2026-07-26 shortly
after model loading. It must not restart automatically. No v6.1 adapter file
was written by that launch; the interrupted snapshot remains the 18:17
artifact described above. When the user explicitly asks to start it again,
run:

```powershell
& .\.venv311\Scripts\python.exe scripts\train_ru_llama3_1.py --config configs\ru_llama3_1_8b_v6_1.json
```

## Voice status

Ru exposes two persisted local voice choices: `Ru (OmniVoice)` and
`Ái Vy (OmniVoice)`. Every retired Gwen or OmniVoice trial label migrates to
the equivalent OmniVoice choice. Ái Vy uses the independently retained
`data\voices\omnivoice\ai_vy.wav` reference. Both choices preserve the sentence
queue and VTube `VoiceVolume` lip-sync lifecycle; private VTS metadata is
removed before synthesis.

The active Ru identity contract describes Ru as the user's AI assistant embodied
by the connected Live2D avatar. Ru's mission is natural conversation and
in-session assistance; it can use only currently connected services and the
current model catalog, and must not claim unavailable actions. The personality
is friendly, witty, lightly grumpy, and affectionate while preserving the
Vietnamese `anh`/`em` voice and direct handling of serious requests. VTS action
metadata remains private and is removed from display, TTS, history, and memory.

The active VTube motion policy keeps speaking sway layered and eased within an
amplitude bound of `14.0` with base speed `1.35`; bounded direction drift and
short holds break up repetition, while idle sway remains at amplitude `5.0`.
During speech, the
manager checks the current model catalog only every 12--25 seconds and may
trigger one safe ambient blink, eye, head, ear, or tail action. Pout, blush,
clothing/body-reveal, and unknown entries are excluded; Ru's explicit
catalog-selected actions remain available and share a dispatch guard so they
do not race an ambient action.

The GUI now exposes an **Open overlay** / **Close overlay** toggle for the
`RuDesktopOverlay` receiver. Toggling it targets only that receiver process;
VTube Studio and Ru remain running.

The OmniVoice choices are the active GUI runtime. The Ru and Ái Vy profiles use
only `tyc_talk.wav`/`reference_text.txt` or the independently retained Ái Vy
reference pair. All use the separate
`D:\LLM\omnivoice_env`, cache `models\omnivoice`, and loopback
port 5052. OmniVoice is the GUI default. The lifecycle releases local Llama
before warming the service and releases the service before local generation.
Manual verification must cover both profiles, speed persistence, local Llama
release, and no spoken VTS metadata.

Voice pace is persisted as a 0.75x--1.20x manual baseline (default 0.90x).
When automatic pacing is enabled, Ru's private slow/normal/quick marker adjusts
only ±0.08x around that baseline; the marker is excluded from display, TTS,
conversation history, and memory.

The active runtime sends both OmniVoice profiles with
`vi-normalized-16`: explicit Vietnamese language, normalized text, and 16
steps. Gwen's 2.52 GB local model was removed after Ái Vy's reference was moved
to the independent OmniVoice voice-assets directory.

On 2026-07-29, the isolated OmniVoice matrix completed with no benchmark errors.
Manual listening remains the quality gate for Ru and Ái Vy across Vietnamese,
English, and code-switch output.

## Implemented feature awaiting live acceptance: local screen awareness

DeepSeek continuation instructions, exact runtime values, failed-path evidence,
and next commands are maintained in
`docs/DEEPSEEK_SCREEN_AWARENESS_HANDOFF.md`.

The approved design is implemented on `main`. The manual, off-by-default
sidebar control observes only the Windows primary monitor. Rust owns DXGI
capture, a bounded RAM-only frame ring, adaptive event gating, the pinned local
Qwen3-VL server, authenticated FastAPI reaction calls, and process cleanup.
React plays every accepted line through the current Ru OmniVoice and privately
triggers valid VTube Studio catalog actions. Raw pixels never leave loopback;
only Qwen's unfiltered structured text may reach OpenCode/Kilo. Screen content
is not written to disk, chat history, memory, learning data, or content-bearing
logs. System audio remains out of scope.

The pinned assets pass size/SHA-256 validation. The original llama.cpp b10142
grammar failure was resolved with a compatible wire schema while the strict
local parser still rejects non-finite/out-of-range scores, oversized events,
and oversized visible-text arrays. A fresh five-run local benchmark measured
load 2,947 ms, inference p50 784 ms, and p95/worst 1,062 ms. The WDDM
per-process VRAM reading remains unusable, so live co-residency inspection is
still required.

Live diagnosis also fixed the cold OmniVoice preflight timeout, a FastAPI
`Request` import collision, insufficient Qwen completion budget, subjective
novelty/worthiness vetoes, swallowed UI errors, and missing RAM-only pipeline
counters. Kilo `kilo-auto/free` then produced only hidden reasoning and no
spoken content at both 64 and 256 output tokens. A direct Poolside route passed
initial probes but later stalled until the ten-second bridge timeout under the
live observer. The screen-only Kilo Auto Free path therefore selects
`inclusionai/ling-3.0-flash:free` with 256 output tokens,
`reasoning.effort=none`, and a short standalone Ru prompt. A live Ling probe
produced first spoken content at 1.621 seconds and completed at 1.674 seconds.
The hosted stream reader also enforces a total deadline even when reasoning
chunks continue arriving. Chat routing remains unchanged.

With OmniVoice resident, the original 4,096-context/512-image-token vision
configuration spilled memory and took 4.61–5.20 seconds per synthetic request.
The production and benchmark commands now use a 2,048 context and 256 image
tokens with a 512-token JSON output budget. A controlled co-resident
three-frame smoke parsed successfully in 917 ms, but real 960×540 frames still
reached cloud at age 3,309 ms. Production therefore now sends only the newest
640×360 frame per analysis; the 500 ms fingerprint gate still observes the
continuous frame sequence.

At pause, no live screen reaction had reached audio yet. The last completed
trial before the 640×360 change measured pre-cloud/cloud/post-cloud ages of
3,391/2,289/5,680 ms and correctly dropped the reply as stale. Ling also
returned intermittent immediate provider errors. The implemented route keeps
Ling primary and retries `openrouter/free` only for HTTP 429/502/503/504; a
direct fallback probe completed in 1.066 seconds with correct anh/em address.
The 640×360 plus provider-fallback combination has not been live-tested because
the user paused work to conserve quota.

Current focused verification is 99 Python tests, 11 frontend tests, and 30
Rust tests passing; one Rust DXGI test is intentionally ignored outside an
interactive desktop. Job Object cleanup has repeatedly left no owned listeners
on ports 8000, 5052, or 18111 after app termination. Re-run the local gate with:

```powershell
& D:\LLM\.venv\Scripts\python.exe scripts\benchmark_local_vision.py --runs 5
```

Do not mark the feature accepted until a representative real game/YouTube trial
proves useful observations, actual Ru OmniVoice playback, p50/p95/worst
event-to-first-audio latency, the approved cadence, VTS behavior, GPU/resource
stability, 20 observation cycles, and 10 complete app exits. Keep `TODO.md`
open for these gates.
