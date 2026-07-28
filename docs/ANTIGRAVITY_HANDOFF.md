# Ru Llama Handoff

## Active scope

Ru is now a Llama-only local project. Keep the Llama 3.1 8B Instruct base,
the v6 QLoRA adapter, its corpus/training path, and the Tkinter GUI. Qwen,
Gemma, MiniGPT, and other non-Llama experiments were removed on 2026-07-26.

## Current runtime

- Base: `models\llama3.1-8b-instruct-hf`
- GUI fallback: `checkpoints\ru-llama3.1-8b-instruct-v6`
- Launch: `& .\.venv\Scripts\python.exe -m mini_gpt.gui`
- Tests: `& .\.venv\Scripts\python.exe -m pytest -q`

Persistent memory is enabled by default and remains local; users can turn it
off in the GUI. Online search is enabled by default and can be turned off in **Settings**;
clearing saved memory affects only local memory artifacts. The GUI bounds
retained chat history before inference.

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
remain required; v6 remains the GUI fallback.

## Current comparison

The deterministic 12-case QLoRA suite was rerun with greedy decoding and 64
new tokens per case. Results: v6 `11/12` (one Vietnamese context-work
out-of-context refusal), v6.1 `12/12`, and v6.2 `12/12`. v6.2 is noticeably
shorter and consistently ends with `Nya~`; its mixed-language turns still need
manual naturalness review. The inference allocator peak was 5,686.78 MiB in
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

Do not promote a new adapter based on loss alone. It must pass the bilingual
conversation gate, preserve Ru's Vietnamese `anh`/`em` voice, and stay within
the physical VRAM cap. Leave v6 as the fallback until that evidence exists.

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
and must not replace the v6 GUI fallback.

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

`g-group-ai-lab/gwen-tts-0.6B` was smoke-tested locally on 2026-07-27 in the
isolated `D:\LLM\gwen_env` environment. It used `tyc_talk.wav`, the matching
Japanese transcript, and Vietnamese target text to produce valid 24 kHz mono
WAV output. The disposable smoke-test files were intentionally cleared after
listening. For cloud replies, the GUI warms the local service on port 5051
while the cloud backend composes the answer, retaining the CUDA model and its
fixed clone prompt. Speech is then queued sentence-by-sentence with up to two
WAV files prefetched; before local inference, the GUI asks Gwen to release
CUDA so the
two models never compete for VRAM. The short greeting passed manual listening,
but Vietnamese pronunciation remains an experimental quality gate. The legacy
voice runtime and all of its cache artifacts were removed on 2026-07-27.

Ru now exposes two local Gwen profiles: `Ru (Tsukuyomi)` and `Yến Nhi (Gwen)`.
The latter needs the one-time `scripts\download_gwen_yen_nhi_voice.py` setup.
Both profiles preserve the same speech queue and VTube lip-sync lifecycle.

The preferred next candidate is `LeNgocTu/Irodori-Sen-TTS`: a MIT-licensed
500M Vietnamese-Japanese model whose card explicitly documents Japanese
reference audio reading Vietnamese text. Its merged model is 1,953.4 MiB and
the required `Aratako/Semantic-DACVAE-Japanese-32dim` codec is 409.7 MiB,
making it viable to evaluate on the 6 GiB GPU. The model is gated: the user
must log into Hugging Face and accept its terms before download, then test it
with `tyc_talk.wav` before wiring it into the GUI.
