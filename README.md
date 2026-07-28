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

The GUI defaults to `checkpoints\ru-llama3.1-8b-instruct-v6`, keeps a bounded
recent conversation context, and validates runtime dependencies before loading
the model. Its default screen is conversation-first; open **Settings** to
change the adapter, sampler, saved-memory option, or online-search permission.
Send with the button or `Ctrl+Enter`; plain Enter adds a line. The status line
shows whether the Llama model was already resident and how long the completed
local reply took. Persistent memory and online search are disabled by default.
The local GUI deliberately uses eager Llama inference: do not re-enable
`torch.compile` on this Python 3.13/CUDA setup, because it caused native
`torch_cpu.dll` process crashes during chat generation.

## OpenCode cloud replies

**OpenCode Zen API** is Ru's only cloud backend. In **Settings**, provide an
OpenCode API key and choose a model; the service URL is fixed by the app. A
previously saved `OpenAI API` selection migrates to OpenCode once and its URL
field is removed from saved settings.

For an OpenCode reply with voice enabled, the status line can show `API`,
`Gwen warm`, and `first voice` durations. `API` measures the existing request
from send until its complete JSON response is read, so it is not first-token
latency. `Gwen warm` measures the local warmup request, while `first voice`
measures the first synthesis request until its WAV is ready; playback itself
is not included. These measurements create no extra API requests. If an
opted-in web-search reply performs two OpenCode completions, the displayed API
duration is their total.

## GGUF runtime

Ru v6 runs by default through local CUDA llama.cpp. It uses a separate Q4_K_M
base GGUF and a separate v6 LoRA GGUF; the adapter is loaded by llama.cpp and
is not merged into the HF base. Select **Transformers (fallback)** in
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

Run the live benchmark after conversion:

```powershell
& D:\LLM\.venv\Scripts\python.exe scripts\benchmark_ru_v6_gguf.py
```

On 2026-07-27, this RTX 4050 Laptop GPU measured 3.36 s cold and 0.75 s warm
for the two bounded 128-token dialogue probes. llama.cpp reported about 35
generated tokens/s; a resident 2,048-context probe used 5,417 MiB of the
6,141 MiB GPU. These are machine-specific measurements, not a quality
approval. Gwen speech stops the owned GGUF server before synthesis so the two
models never compete for the 6 GiB GPU.

GGUF was promoted to the default after manual testing of natural
Vietnamese-English turn-taking, coherent context, and the friendly `anh`/`em`
voice. Quantization and timing alone never replace that quality gate.

- Enable **Remember conversations** only to persist completed pairs locally in
  `data\conversations\memory_*`.
- Enable **Allow online search** only to permit a model-directed DDGS lookup.
- **Clear saved memory** removes only those local memory files after
  confirmation.
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

## Experimental voice output

The GUI automatically sends each completed reply to the local Gwen service on
port 5051. Its **Voice** selector provides two local Gwen clone profiles:
`Ru (Tsukuyomi)`, using `tyc_talk.wav` with the matching transcript in
`reference_text.txt`, and `Yến Nhi (Gwen)`, using the upstream Gwen reference
audio and its exact transcript. Do not change a reference audio without its
matching transcript.

Set up Yến Nhi once before selecting it:

```powershell
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\download_gwen_yen_nhi_voice.py
```

Both choices use local Gwen, so they retain the same VTube Studio lip-sync
behavior and GPU lifecycle.

`g-group-ai-lab/gwen-tts-0.6B` runs in the separate `gwen_env` environment.
For OpenCode cloud replies, Ru warms and retains its Gwen model plus the fixed
voice-clone prompt on CUDA while the cloud model is composing the reply. The
reply is then synthesized sentence-by-sentence through an up-to-two-WAV
prefetch queue, so the first sentence can play before later sentences are ready
without an avoidable gap. The
first cloud reply after opening the GUI can still be slower because it performs
that warmup.

Before local GGUF or Transformers inference, Ru asks Gwen to release its model
and CUDA cache; this keeps the two models from competing for the 6 GB GPU.
Local replies load Gwen only when speech begins. Gwen is experimental: its
voice identity and short Vietnamese phrases passed manual listening, while
pronunciation still needs user evaluation.

The GUI starts and waits for the local service automatically the first time it
needs to speak. The service continues in the background for later replies. Use
the command below only to inspect the voice service manually:

```powershell
& D:\LLM\run_gwen_tts_server.bat
```

To measure a warm service directly, make a short request five times and inspect
the returned `synthesis_ms` value. The relevant interactive target is the time
from completed reply text to the first audible sentence, which also includes
audio playback setup:

```powershell
$body = @{ text = 'Em chao anh, em da san sang roi.'; fast_mode = $true; voice_profile = 'yen_nhi' } | ConvertTo-Json
1..5 | ForEach-Object {
    Invoke-RestMethod http://127.0.0.1:5051/tts -Method Post -ContentType 'application/json' -Body $body
}
```

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

Direct Vietnamese avatar requests such as `giơ tay`, `vẫy tay`, `vui lên`,
`nhắm mắt`, and `dựng đuôi` remain a compatibility fallback even if the
language model answers as a text-only assistant.

VTube Studio `VoiceVolume` remains the only mouth/lip-sync input. Do not map
or recreate `LLM_MouthOpen`.

## Desktop overlay (Spout2)

`RuDesktopOverlay` is a separate transparent desktop window. It receives the
existing `VTubeStudioSpout` output, so it does not change Ru, the active VTube
Studio model, `VoiceVolume`, `LLM_Sway`, or any model hotkeys.

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

All model weights, checkpoints, data, and caches stay on `D:\LLM`. Llama v6
remains the GUI fallback. Training or promoting a later Llama adapter requires
the existing bilingual conversation and physical-VRAM gates; validation loss
alone is not an adoption decision.
