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
port 5051. Its reference is `tyc_talk.wav`, the official Tsukuyomi-chan sample,
with the matching Japanese transcript in `reference_text.txt`. Do not change
one without changing the other: Gwen voice cloning needs an exact transcript of
the reference audio.

`g-group-ai-lab/gwen-tts-0.6B` runs in the separate `gwen_env` environment.
Before speaking, the GUI releases its cached Llama model; the Gwen service then
starts a short-lived worker for the request. The worker exits before the WAV is
returned, fully releasing its CUDA context. This avoids simultaneous-model VRAM
exhaustion on the 6 GB GPU, at the cost of a short delay before each reply
starts speaking. Gwen is experimental: its voice identity and short Vietnamese
phrases passed manual listening, while pronunciation still needs user evaluation.

The GUI starts and waits for the local service automatically the first time it
needs to speak. The service continues in the background for later replies. Use
the command below only to inspect the voice service manually:

```powershell
& D:\LLM\run_gwen_tts_server.bat
```

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
