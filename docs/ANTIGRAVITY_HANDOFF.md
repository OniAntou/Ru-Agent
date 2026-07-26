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

Persistent memory and online search are disabled by default. They require a
visible opt-in in the GUI; clearing saved memory affects only local memory
artifacts. The GUI bounds retained chat history before inference.

## Promotion rule

Do not promote a new adapter based on loss alone. It must pass the bilingual
conversation gate, preserve Ru's Vietnamese `anh`/`em` voice, and stay within
the physical VRAM cap. Leave v6 as the fallback until that evidence exists.
