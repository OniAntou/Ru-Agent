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
the model. Persistent memory and online search are disabled by default.

- Enable **Remember conversations** only to persist completed pairs locally in
  `data\conversations\memory_*`.
- Enable **Allow online search** only to permit a model-directed DDGS lookup.
- **Clear saved memory** removes only those local memory files after
  confirmation.
- **Mark last response useful** writes a reviewed example to
  `data\conversations\approved.jsonl`; it never changes model weights.

## Project boundaries

All model weights, checkpoints, data, and caches stay on `D:\LLM`. Llama v6
remains the GUI fallback. Training or promoting a later Llama adapter requires
the existing bilingual conversation and physical-VRAM gates; validation loss
alone is not an adoption decision.
