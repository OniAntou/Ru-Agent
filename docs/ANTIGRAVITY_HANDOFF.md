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

Persistent memory is disabled by default and requires a visible opt-in in the
GUI. Online search is enabled by default and can be turned off in **Settings**;
clearing saved memory affects only local memory artifacts. The GUI bounds
retained chat history before inference.

## Promotion rule

Do not promote a new adapter based on loss alone. It must pass the bilingual
conversation gate, preserve Ru's Vietnamese `anh`/`em` voice, and stay within
the physical VRAM cap. Leave v6 as the fallback until that evidence exists.

## v6.1 training status

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
