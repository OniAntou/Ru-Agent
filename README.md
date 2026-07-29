# Ru — Local Llama Conversational Runtime

Ru is a local Vietnamese-English conversational assistant built only on
Llama 3.1 8B Instruct with the retained v6 QLoRA adapter. The project keeps
the active Llama runtime, its training/corpus path, and local GUI; Qwen, Gemma,
MiniGPT, and other base-model experiments have been removed.

## Setup

Use Python 3.13 and install the CUDA build of PyTorch first:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pip install --no-cache-dir torch==2.13.0 --index-url https://download.pytorch.org/whl/cu130
& D:\LLM\.venv\Scripts\python.exe -m pip install -e "D:\LLM[runtime,dev]"
```

Verify the environment:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
& D:\LLM\.venv\Scripts\python.exe -c "import torch; print(torch.__version__); print(torch.cuda.is_available())"
```

## Active GUI

```powershell
& D:\LLM\.venv\Scripts\python.exe -m mini_gpt.gui
```

The GUI defaults to the v6.2 LoRA GGUF at
`models\gguf\ru-llama3.1-8b-instruct-v6.2-lora.gguf`, with v6.1 and v6 GGUF
fallbacks if the preferred file is unavailable. The Transformers fallback
selector prefers `checkpoints\ru-llama3.1-8b-instruct-v6.2-fresh`, then v6.1
and v6. The app keeps a bounded recent conversation context and validates
runtime dependencies before loading the model. Its default screen is
conversation-first; open **Settings** to change the adapter, sampler,
saved-memory option, or online-search permission.
Send with the button or `Ctrl+Enter`; plain Enter adds a line. The status line
shows whether the Llama model was already resident and how long the completed
local reply took. Persistent memory is enabled by default and can be turned off
in **Settings**; online search is enabled by default and can also be turned off.
The default reply
limit is 512 tokens.
The local GUI deliberately uses eager Llama inference: do not re-enable
`torch.compile` on this Python 3.13/CUDA setup, because it caused native
`torch_cpu.dll` process crashes during chat generation.

## OpenCode cloud replies

**OpenCode Zen API** is Ru's only cloud backend. In **Settings**, provide an
OpenCode API key and choose a model; the service URL is fixed by the app. A
previously saved `OpenAI API` selection migrates to OpenCode once and its URL
field is removed from saved settings.

OpenCode responses stream into the in-progress Ru bubble. Private VTube Studio
metadata and model-directed `[SEARCH: ...]` commands stay hidden during the
stream; a search command clears that provisional bubble before the grounded
follow-up streams. The GUI receives only newly emitted text from the streaming
runtime, so cumulative provider snapshots do not duplicate text in the bubble.
For an OpenCode reply with voice enabled, the status line
can show `API`, `Voice warm`, and `first voice` durations. `API` measures the
existing request from send until its streamed completion is received, so it is
not first-token latency. `Voice warm` measures the selected local warmup request, while `first voice`
measures the first synthesis request until its WAV is ready; playback itself
is not included. These measurements create no extra API requests. If an
opted-in web-search reply performs two OpenCode completions, the displayed API
duration is their total. A protocol-valid but empty stream is retried once
before the GUI reports a failure.

## GGUF runtime

Ru v6.2 runs by default through local CUDA llama.cpp. It uses a separate
Q4_K_M base GGUF and a separate v6.2 LoRA GGUF; the adapter is loaded by
llama.cpp and is not merged into the HF base. Select **Transformers (fallback)** in
**Settings** only when diagnosing or evaluating the HF runtime. Keep the HF
base on disk: it is required for v6.1 training, evaluation, and future GGUF
conversions.

The one-time conversion needs at least 24 GiB free on D:, a local llama.cpp
source checkout at `tools\llama.cpp`, and the matching CUDA 12.4 Windows
release files at `tools\llama.cpp-bin`. The directory must contain
`llama-server.exe`, `llama-quantize.exe`, and the CUDA DLLs from the official
release. Install the converter extra, then validate before starting:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pip install -e "D:\LLM[runtime,gguf,dev]"
& D:\LLM\.venv\Scripts\python.exe scripts\build_ru_v6_gguf.py --dry-run
& D:\LLM\.venv\Scripts\python.exe scripts\build_ru_v6_gguf.py
```

The guarded script refuses to run below 24 GiB free, never overwrites an
existing candidate, prevents concurrent conversion, preserves the HF base and
adapter, and removes only its verified temporary F16 GGUF after success. It
creates:

- `models\gguf\llama3.1-8b-instruct-q4_k_m.gguf`
- `models\gguf\ru-llama3.1-8b-instruct-v6-lora.gguf`
- `models\gguf\ru-llama3.1-8b-instruct-v6.2-lora.gguf` (preferred)

The v6.2 LoRA GGUF was converted from
`checkpoints\ru-llama3.1-8b-instruct-v6.2-fresh` on 2026-07-29. Its SHA-256 is
`F991F730EF5D89A5C0C187FD36A6FEC5CA958B99710244F48AADC36572F80DB6`.

Run the live benchmark after conversion:

```powershell
& D:\LLM\.venv\Scripts\python.exe scripts\benchmark_ru_v6_gguf.py
```

On 2026-07-27, this RTX 4050 Laptop GPU measured 3.36 s cold and 0.75 s warm
for the two bounded 128-token dialogue probes. llama.cpp reported about 35
generated tokens/s; a resident 2,048-context probe used 5,417 MiB of the
6,141 MiB GPU. These are machine-specific measurements, not a quality
approval. OmniVoice speech stops the owned GGUF server before synthesis so the two
models never compete for the 6 GiB GPU.

The original v6 GGUF path was promoted to the default after manual testing of natural
Vietnamese-English turn-taking, coherent context, and the friendly `anh`/`em`
voice. Quantization and timing alone never replace that quality gate.

The v6.2 GGUF is the current operational GUI default by explicit user choice.
The hard quality gate is still recorded as failed, so this default selection
does not mean v6.2 has passed promotion review.

## v6.3 targeted dataset

The new dataset is `data\conversations\ru_v6_3`, with 180 authored rows split
108/36/36 into train/validation/held-out. It contains repair, bilingual bridge,
and context-recall strata, with 90 Vietnamese, 60 mixed, and 30 English rows.
Manifest authored SHA-256: `d757882ea70ad5b1951a0e94a0012ecfae5c95b5fbbc677f20061bc40f9b7d8b`.
It is new project-owned data targeted from the hard-suite failures; no v6.2
rows were copied. It includes bounded contextual profanity metadata: 155
`none`, 16 `mild`, and 9 `medium` rows. Training has not started. The bounded config is
`configs\ru_llama3_1_v6_3.json` with 360 steps and a 9,600-second wall-clock
cap. Chat output must not contain Unicode emoji or ASCII emoticons; visible
emotion is delegated to VTS metadata when available, with natural prose as the
fallback.

- Enable **Remember conversations** to let Ru automatically save safe, structured
  local preferences and project decisions in `data\conversations\memory.db`.
  Credentials, URLs, sensitive data, and VTube Studio metadata are rejected.
- **Allow online search** is enabled by default to permit a model-directed
  DDGS lookup; disable it to keep replies fully offline.
- **Clear saved memory** removes only the local SQLite/vector memory artifacts
  after confirmation; it never changes approved examples or model weights.
- Evaluate the deterministic memory gate with
  `& D:\LLM\.venv\Scripts\python.exe scripts\evaluate_ru_memory.py`.
- **Mark last response useful** writes a reviewed example to
  `data\conversations\approved.jsonl`; it never changes model weights.

## v6.1 dataset

`data\conversations\ru_v6_1` contains an audited authored base of 2,400
rows plus three train-only reviewed local examples, for 1,683 train rows and
360 rows each in validation and held-out. The audit report is
`naturalness_audit.json`; it rejects AI-meta wording, case-insensitive prompt
collisions, repeated full replies, and an overused response opening.

Recreate the authored base and then explicitly reapply the reviewed rows:

```powershell
& D:\LLM\.venv\Scripts\python.exe scripts\build_ru_llama_v6_1_data.py --output-dir data\conversations\ru_v6_1 --seed 42
& D:\LLM\.venv\Scripts\python.exe scripts\merge_approved_into_ru_v6_1.py --dataset-dir data\conversations\ru_v6_1 --approved-file data\conversations\approved.jsonl
```

Dataset preparation does not start training or promote a v6.1 adapter.

## v6.2 fresh roleplay dataset

`data\conversations\ru_v6_2_fresh` is a new authored candidate, separate from
the historical `ru_v6_2` data. It contains 1,200 authored rows plus 39 safe
train-only calibration rows imported from `approved.jsonl` and
`memory_texts.json`: 759 train, 240 validation, and 240 held-out. The import
audit rejected 44 rows for duplicates, malformed/noisy content, AI-meta text,
control markers, links/lists, or unsafe explicit material. Rejected source text
is not written to the candidate. Authored and imported rows now use the
language-specific `ru_style.py` personality prompts, preserve the `anh`/`em`
or English-only voice, and keep VTS control markers out of the training text.
The active contract no longer forces a `Nya~` suffix; it allows only contextual
mild/medium profanity under the documented safety boundary.

Rebuild it deterministically with:

```powershell
& D:\LLM\.venv\Scripts\python.exe scripts\build_ru_llama_v6_2_fresh_data.py
```

The candidate was trained from the v6 adapter with the bounded config. It ran
480 steps in 2,630 seconds and reached final validation loss 0.2060. The
adapter manifest is `checkpoints\ru-llama3.1-8b-instruct-v6.2-fresh\ru_manifest.json`.
The bounded config was `configs\ru_llama3_1_v6_2_fresh.json` with a 9,600-second
wall-clock guard and 6,144 MiB physical cap. The CUDA allocator reported 8,445 MiB
while the driver-side physical sample
was unavailable, so the physical-VRAM gate is recorded as unverified; the v6.2
GGUF remains an operational default only until that hardware check and the
bilingual runtime gate pass.

The same deterministic 12-case QLoRA evaluation was rerun for all three
adapters (`comparison_v6.json`, `comparison_v6_1.json`, and
`comparison_v6_2.json`). Automatic results were v6 `11/12`, v6.1 `12/12`, and
v6.2 `12/12`; v6 alone failed the Vietnamese context-work case with an
out-of-context refusal. v6.2 responses were shorter and consistently ended with
`Nya~` under the historical v6.2 prompt contract; that suffix is no longer
active, and mixed-language grammar still needs manual review. The evaluation
allocator peak was 5,686.78 MiB for each run. This does not override v6.2's
unverified physical-training VRAM gate or the required manual bilingual/held-out
review.

### v6 hard evaluation

The harder suite is `data\\evaluations\\ru_hard_v1.json` (36 authored cases,
SHA-256 `648d5522a4d8096e33a4f06a7346e96693e7241633cc6ac628ea1bd421c0e84c`).
It was run with greedy decoding and 128 new tokens in isolated adapter
processes. Results were v6 `158/360` with 13 hard fails, v6.1 `180/360` with
11 hard fails, and v6.2 `205/360` with 7 hard fails. Automatic ranking by
strict/pass-rate is v6.1, v6.2, v6; none passed the strict gate. v6.2 had the
best repair-stratum score and no explicit-content hard fail, but still failed
several repair cases and needs manual review. Reports are under
`checkpoints\\ru-hard-evaluation-v1`.

## Local voice output

The GUI automatically sends each completed reply to the selected local voice
service. Its **Voice** selector persists either `Ru (OmniVoice)` or
`Ái Vy (OmniVoice)`. Every historical Gwen or OmniVoice trial label migrates
to the equivalent OmniVoice choice. OmniVoice is the default. Ru uses
`tyc_talk.wav` and its matching transcript; Ái Vy uses the independently kept
`data\voices\omnivoice\ai_vy.wav` and its matching transcript. Do not change a
reference audio without its matching transcript.

If the Ái Vy asset is absent on a fresh checkout, fetch only that reference
file (not the removed Gwen model):

```powershell
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\download_omnivoice_ai_vy_voice.py
```

All choices retain the same VTube Studio `VoiceVolume` lip-sync behavior and
sentence queue. VTube metadata is removed before synthesis.

### OmniVoice runtime

Both OmniVoice labels require the already-installed
`D:\LLM\omnivoice_env` runtime and `models\omnivoice` cache on `D:`. The GUI
does not install packages or download a model. It starts
`omnivoice_tts_server.py` only when that selected engine is needed, on loopback
port 5052. The D-only setup script installs OmniVoice and its Flask loopback
service dependency in `omnivoice_env`.

For the pending pronunciation listening trial, both OmniVoice labels use the
measured `vi-normalized-16` settings (`language="Vietnamese"`, text
normalization, 16 steps). This affects only explicitly selected OmniVoice test
voices; **Ru (OmniVoice)** remains the GUI default.

### Voice pace

Settings exposes a persisted **Base speed** control from 0.75x to 1.20x,
defaulting to 0.90x. When **Let Ru adjust pace around this speed** is enabled,
Ru can select only slow, normal, or quick pacing within ±0.08x of that manual
baseline and still within the global safety bounds. Pacing metadata is private:
it never appears in the chat, TTS text, conversation history, or memory.

Before warming OmniVoice, Ru releases the local Llama runtime. Before local
generation, it releases OmniVoice. A voice failure is reported as unavailable
and never silently changes the saved selection.

For manual verification, select Ru and Ái Vy, test a manual speed plus each
automatic pace, restart the GUI to confirm settings persist, and verify that
normal local Llama generation and VTube lip-sync still work.

## Experimental voice input

Voice input defaults to the existing Google recognizer (`vi-VN`). The
**Speech input** settings section can instead select **Sherpa-ONNX (offline)**.
That choice never falls back to Google: microphone audio stays local for the
recognition request, and no model is downloaded automatically.

Install the optional runtime only after supplying a compatible local model:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pip install -e '.[speech-offline]'
```

Select a folder containing `tokens.txt` plus one matching
`encoder*.onnx`, `decoder*.onnx`, and `joiner*.onnx`. The recommended initial
model is the official `sherpa-onnx-zipformer-vi-30M-int8-2026-02-09` package,
whose files are `encoder.int8.onnx`, `decoder.onnx`, and `joiner.int8.onnx`.
The GUI stores only the selected recognizer and this model folder; it does not
save microphone recordings or transcripts.

Benchmark supplied samples before considering Sherpa-ONNX as a default. Each
JSONL manifest row contains an `audio_path` and `expected_text`; paths may be
relative to the manifest. Samples must be uncompressed, mono, 16-bit, 16 kHz
WAV files.

```powershell
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\benchmark_stt.py --engine sherpa-onnx --model-dir D:\LLM\models\sherpa-onnx-zipformer-vi-30M-int8-2026-02-09 --manifest D:\LLM\data\stt_samples.jsonl --output D:\LLM\data\stt_benchmark.jsonl
```

Keep Google as the default unless an explicit 20-sample manual gate covers
Vietnamese, English, and Vietnamese-English turns; record recognition success,
final-transcript latency, and whether local LLM replies plus OmniVoice playback
remain stable. A failed gate is a non-adoption result, not a reason to weaken
the fallback or privacy behavior.

## VTube Studio actions

For every reply, the GUI reads the current VTube Studio model, its hotkeys,
and its `.exp3.json` expressions. It supplies that model-scoped catalog to Ru
as private `vts_XX` and `expr_XX` codes, so switching models changes the
available actions without editing source code. Ru may use any listed hotkey,
including appearance, hair, and clothing changes that belong to the current
model.

Ru emits chosen actions only as a terminal `[[VTS:...]]` suffix. The GUI
removes that metadata before showing chat text, generating TTS, saving memory,
or saving a reviewed example. It rechecks the current model before dispatch;
codes from a previous model are discarded safely. An expression activated by
Ru is deactivated after a short interval, while expressions that were already
active from the user are never touched.

While Ru is speaking, `LLM_Sway` keeps a reduced but visible motion (amplitude `14.0`,
speed `1.35`) instead of the former large sway. At most one ambient
micro-action is considered every 12--25 seconds. The candidate pool is read
from the current model catalog each time and is limited to reversible blink,
eye, head, ear, or tail motions; pout, blush, clothing/body-reveal, and
unknown entries are skipped. Ru's explicit catalog-selected actions remain
independent, so a model can still expose additional hotkeys without changing
this scheduler.

Direct Vietnamese avatar requests such as `giơ tay`, `vẫy tay`, `vui lên`,
`nhắm mắt`, and `dựng đuôi` remain a compatibility fallback even if the
language model answers as a text-only assistant.

VTube Studio `VoiceVolume` remains the only mouth/lip-sync input. Do not map
or recreate `LLM_MouthOpen`.

## Desktop overlay (Spout2)

`RuDesktopOverlay` is a separate transparent desktop window. It receives the
existing `VTubeStudioSpout` output, so it does not change Ru, the active VTube
Studio model, `VoiceVolume`, `LLM_Sway`, or any model hotkeys.

The GUI's **Open overlay** / **Close overlay** button starts or stops only the
`RuDesktopOverlay` receiver. It does not close Ru or VTube Studio, and it uses
the current `VTubeStudioSpout` sender.

1. In VTube Studio, enable its Windows **Spout2** sender. Use **Color Picker
   Background** and enable transparency in capture. Do not use the earlier
   green desktop-window workaround.
2. Build the receiver once:

   ```powershell
   .\scripts\build_ru_desktop_overlay.ps1
   ```

3. Start it without click-through first, then drag it into place:

   ```powershell
   .\scripts\start_ru_desktop_overlay.ps1
   ```

4. Only after confirming that the avatar has no green background, close it and
   restart with mouse input passing to the desktop beneath the model:

   ```powershell
   .\scripts\start_ru_desktop_overlay.ps1 -ClickThrough
   ```

While the non-click-through overlay has focus, `C` toggles click-through, `T`
keeps it topmost, and `Esc` closes it. Closing this overlay does not close Ru
or VTube Studio. If it says it is waiting for `VTubeStudioSpout`, enable the
Spout2 sender in VTube Studio and leave that app running.

The voice sample is used under the Tsukuyomi-chan sample-voice terms. Any
distributed synthesis using only these samples must include the required
Tsukuyomi-chan attribution from the official source:
https://tyc.rei-yumesaki.net/material/voice/sample-voice/

The local stack has no API fee, but its model and sample-voice licenses still
apply. Do not redistribute the Tsukuyomi-chan source WAV files; follow its
character and sample-voice terms, including attribution when required for
published output.

## Project boundaries

All model weights, checkpoints, data, and caches stay on `D:\LLM`. The v6.2
GGUF is the operational GUI default and v6 remains the HF fallback. Training or
calling a later Llama adapter quality-gate promoted requires the existing
bilingual conversation and physical-VRAM gates; validation loss alone is not an
adoption decision.
