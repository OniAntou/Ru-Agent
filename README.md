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

The active desktop GUI is the **Tauri** app in `gui-tauri/` (React 19 +
Tauri 2, Vite), backed by the local FastAPI server
`mini_gpt.server` on `127.0.0.1:8000`. The legacy CustomTkinter GUI
(`mini_gpt.gui`), its patch scripts (`edit_gui.py`, `rewrite_ui.py`,
`rewrite_kilo.py`, `fix.py`), the old Flask web GUI
(`scripts/gui_ru_llama3_1.py`), and the `Start_Ru.bat` launcher were
removed.

```powershell
& D:\LLM\start_dev.bat
```

`start_dev.bat` starts the backend **in the background** (no terminal is
blocked) with logs at `logs\server.log` / `logs\server_err.log`, then opens
the Tauri dev window. Stop the server with
`taskkill /F /PID <pid>` (find it via `netstat -ano | findstr :8000`).

or manually:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m mini_gpt.server   # runs in foreground; use a dedicated terminal
npm run tauri dev   # in gui-tauri/
```

The app has four tabs: **Chat** (WebSocket streaming to `/chat` with
auto-reconnect and typing indicator; persistent session history — each chat
session is stored under `~/.minigpt/sessions/` with an auto title from the
first message, a "New Session" button, and a history panel to reopen/delete
old sessions; legacy `history.json` is auto-migrated once), **Memory** (clear
saved memory via `DELETE /memory`), **Settings** (backend, model, API key, max
tokens, temperature, top-k, web search toggle, checkpoint path — loads the
current state from `GET /settings`), and **Voice** (TTS via the OmniVoice
service: voice pick, speed, test, warm up, release GPU; toggle "read replies
aloud" persists in the browser and auto-plays each chat reply; microphone
input → speech recognition fills the chat box; VTube Studio avatar connection
and expression controls).

The backend defaults to the v6.2 LoRA GGUF at
`models\gguf\ru-llama3.1-8b-instruct-v6.2-lora.gguf`, with v6.1 and v6 GGUF
fallbacks if the preferred file is unavailable. The Transformers fallback
selector prefers `checkpoints\ru-llama3.1-8b-instruct-v6.2-fresh`, then v6.1
and v6. The backend keeps a bounded recent conversation context and validates
runtime dependencies before loading the model.

Persistent memory and online search remain runtime capabilities (see
below); the Tauri GUI exposes the memory controls and an "Allow online
search" toggle in Settings.

## OpenCode cloud replies

**OpenCode Zen API** is Ru's only cloud backend. In the **Settings** tab,
provide an OpenCode API key and choose a model; the service URL is fixed by
the backend.

OpenCode responses stream into the in-progress Ru message. Model-directed
`[SEARCH: ...]` commands stay hidden during the stream; a search command
clears that provisional message before the grounded follow-up streams. The
frontend receives only newly emitted text from the streaming runtime, so
cumulative provider snapshots do not duplicate text. If an
opted-in web-search reply performs two OpenCode completions, the recorded
API duration is their total. A protocol-valid but empty stream is retried
once before the backend reports a failure.

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
adapter, and always removes its temporary F16 GGUF (also on failure or
Ctrl+C). It creates:

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
- **Self-learning** (bật mặc định trong Model Settings): sau mỗi lượt chat, khi
  model rảnh, Ru dùng chính LLM để trích các fact lâu dài
  (profile/preference/project/decision) từ lượt vừa rồi, dedup với memory hiện
  có rồi upsert vào `data\conversations\memory.db`; các fact này được tự chèn
  vào context ở các lượt sau. Thông tin nhạy cảm (API key, password, token,
  URL) và VTS metadata bị bỏ qua.
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
& D:\LLM\.venv\Scripts\python.exe scripts\build_corpus.py --version v6_1 --output-dir data\conversations\ru_v6_1 --seed 42
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
& D:\LLM\.venv\Scripts\python.exe scripts\build_corpus.py --version v6_2_fresh
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

The runtime can automatically send each completed reply to the selected local
voice service. Its **Voice** selection persists either `Ru (OmniVoice)` or
`Ái Vy (OmniVoice)`. Every historical Gwen or OmniVoice trial label migrates
to the equivalent OmniVoice choice. OmniVoice is the default. Ru uses
`tyc_talk.wav` and its matching transcript; Ái Vy uses the independently kept
`data\voices\omnivoice\ai_vy.wav` and its matching transcript. Do not change a
reference audio without its matching transcript.

Note: the Tauri GUI does not expose this voice selection yet; the runtime
helpers (`voice_runtime`, `speech_runtime`, `tts_chunks`, `voice_pacing`)
remain for the backend integration.

If the Ái Vy asset is absent on a fresh checkout, fetch only that reference
file (not the removed Gwen model):

```powershell
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\download_omnivoice_ai_vy_voice.py
```

All choices retain the same VTube Studio `VoiceVolume` lip-sync behavior and
sentence queue. VTube metadata is removed before synthesis.

### OmniVoice runtime

Both OmniVoice labels require the already-installed
`D:\LLM\omnivoice_env` runtime and `models\omnivoice` cache on `D:`. The runtime
does not install packages or download a model. It starts
`omnivoice_tts_server.py` only when that selected engine is needed, on loopback
port 5052. The D-only setup script installs OmniVoice and its Flask loopback
service dependency in `omnivoice_env`.

For the pending pronunciation listening trial, both OmniVoice labels use the
measured `vi-normalized-16` settings (`language="Vietnamese"`, text
normalization, 16 steps). This affects only explicitly selected OmniVoice test
voices; **Ru (OmniVoice)** remains the runtime default.

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
automatic pace, restart the app to confirm settings persist, and verify that
normal local Llama generation and VTube lip-sync still work.

## Experimental voice input

Voice input uses the Google recognizer (`vi-VN`) and is an experimental
feature. Microphone audio is sent to Google's recognition endpoint; nothing is
stored locally beyond the resulting transcript. The former Sherpa-ONNX offline
engine was removed from the runtime; saved settings that still reference it
fall back to Google with a clear error.

The Tauri GUI wires it through the backend `POST /voice/transcribe` endpoint:
the mic button in the chat input records via the browser APIs
(`getUserMedia` + `ScriptProcessorNode`), down-samples to 16 kHz PCM WAV, and
fills the chat input with the recognized Vietnamese text.

## Ru identity and capabilities

Ru is the user's AI assistant embodied by the connected Live2D avatar, not a
standalone human or an all-powerful controller. Its mission is natural
conversation, in-session context, explanations, search, planning, and voice
responses when the related services are available. The active personality is
friendly, witty, lightly grumpy, and affectionate; in Vietnamese Ru calls the
user `anh` and refers to itself as `em`, while staying direct in serious or
sensitive situations.

Ru can use the current model's VTube Studio hotkeys and expressions, combine up
to three compatible visible actions, and drive the mapped `LLM_Sway` body signal.
`VoiceVolume` remains the lip-sync source; `LLM_MouthOpen` is not used. VTS
action metadata is private and is removed from chat, TTS, history, and memory.
Ru must report unavailable connections or controls honestly instead of claiming
that an action happened.

## VTube Studio actions

The VTS runtime modules (`vts_manager`, `vts_catalog`, `vts_actions`,
`vts_motion`) are wired through the backend (`/vts/status|connect|action`) and
the Tauri Voice tab (connect, facial joy/surprised/gentle, body
raise_hand/tail_up). For every reply, the runtime reads the current VTube Studio
model, its hotkeys,
and its `.exp3.json` expressions. It supplies that model-scoped catalog to Ru
as private `vts_XX` and `expr_XX` codes, so switching models changes the
available actions without editing source code. Ru may use any listed hotkey,
including appearance, hair, and clothing changes that belong to the current
model.

Ru emits chosen actions only as a terminal `[[VTS:...]]` suffix. The runtime
removes that metadata before showing chat text, generating TTS, saving memory,
or saving a reviewed example. It rechecks the current model before dispatch;
codes from a previous model are discarded safely. An expression activated by
Ru is deactivated after a short interval, while expressions that were already
active from the user are never touched.

While Ru is speaking, `LLM_Sway` keeps a reduced but visible layered motion
(amplitude bound `14.0`, base speed `1.35`) instead of the former large sway.
The layers and easing vary the timing slightly so the movement does not repeat
as one obvious cycle. At most one ambient
micro-action is considered every 12--25 seconds. The candidate pool is read
from the current model catalog each time and is limited to reversible blink,
eye, head, ear, or tail motions; pout, blush, clothing/body-reveal, and
unknown entries are skipped. Ru's explicit catalog-selected actions remain
independent, so a model can still expose additional hotkeys without changing
this scheduler. Ru may combine up to three compatible facial, eye, ear, or
tail catalog codes in one private suffix when they reinforce the same emotion;
the codes are still removed before chat display, TTS, memory, and history.

Direct Vietnamese avatar requests such as `giơ tay`, `vẫy tay`, `vui lên`,
`nhắm mắt`, and `dựng đuôi` remain a compatibility fallback even if the
language model answers as a text-only assistant.

VTube Studio `VoiceVolume` remains the only mouth/lip-sync input. Do not map
or recreate `LLM_MouthOpen`.

## Desktop overlay (Spout2)

`RuDesktopOverlay` is a separate transparent desktop window. It receives the
existing `VTubeStudioSpout` output, so it does not change Ru, the active VTube
Studio model, `VoiceVolume`, `LLM_Sway`, or any model hotkeys.

The overlay can be started or stopped through the `scripts`
(`build_ru_desktop_overlay.ps1` / `start_ru_desktop_overlay.ps1`); the legacy
GUI button was removed with the CustomTkinter GUI. It starts or stops only the
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
