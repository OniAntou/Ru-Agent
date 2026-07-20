# Ru — Bilingual Mini-GPT

An educational Vietnamese-English decoder-only Transformer trained from random initialization. The project is intentionally small enough to run experiments on an RTX 4050 Laptop GPU with approximately 6 GB VRAM.

This repository is not a ChatGPT replacement. The included corpus is an authored smoke-test corpus; useful general-purpose behavior requires a much larger, openly licensed dataset.

## Setup

Use Python 3.11 and keep the environment on D::

```powershell
$python311 = 'C:\Users\USER\AppData\Roaming\uv\python\cpython-3.11.15-windows-x86_64-none\python.exe'
& $python311 -m venv D:\LLM\.venv
& D:\LLM\.venv\Scripts\python.exe -m pip install --upgrade pip
& D:\LLM\.venv\Scripts\python.exe -m pip install --no-cache-dir torch==2.9.0 --index-url https://download.pytorch.org/whl/cu130
& D:\LLM\.venv\Scripts\python.exe -m pip install numpy tokenizers datasets pytest
& D:\LLM\.venv\Scripts\python.exe -m pip install -e D:\LLM --no-deps
```

The CUDA command installs the GPU wheel used on the development laptop. If a future PyTorch release changes its official CUDA matrix, use the Windows/Pip/CUDA selector on the official PyTorch installation page and keep the selected wheel inside this D: drive environment.

Check the runtime:

```powershell
& D:\LLM\.venv\Scripts\python.exe -c "import torch; print(torch.__version__); print('CUDA:', torch.cuda.is_available()); print(torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'CPU')"
```

## End-to-end smoke run

Run from `D:\LLM`:

```powershell
& .\.venv\Scripts\python.exe -m mini_gpt.cli prepare --raw-dir data/raw --processed-dir data/processed
& .\.venv\Scripts\python.exe -m mini_gpt.cli tokenizer --text-dir data/processed --output data/tokenizer.json --vocab-size 300
& .\.venv\Scripts\python.exe -m mini_gpt.cli encode --text data/processed/train.txt --tokenizer data/tokenizer.json --output data/processed/train.bin
& .\.venv\Scripts\python.exe -m mini_gpt.cli encode --text data/processed/val.txt --tokenizer data/tokenizer.json --output data/processed/val.bin
& .\.venv\Scripts\python.exe -m mini_gpt.cli train --config configs/tiny.json --processed-dir data/processed --tokenizer data/tokenizer.json --checkpoint-dir checkpoints
& .\.venv\Scripts\python.exe -m mini_gpt.cli evaluate --checkpoint checkpoints/latest.pt --tokenizer data/tokenizer.json --validation data/processed/val.jsonl
& .\.venv\Scripts\python.exe -m mini_gpt.cli generate --checkpoint checkpoints/latest.pt --tokenizer data/tokenizer.json --prompt "Xin chào"
& .\.venv\Scripts\python.exe -m pytest -q
```

The training command uses CUDA automatically when PyTorch detects it. The tiny configuration is only a pipeline check; it is not enough data or compute for strong language quality.

## Download a larger open sample

The downloader streams a bounded sample from the cleaned Wikimedia Wikipedia dataset and keeps the Hugging Face cache on D:. It does not download the complete Wikipedia collection.

```powershell
cd D:\LLM
& .\.venv\Scripts\python.exe scripts\download_wikipedia.py --max-documents 10000 --min-characters 200
```

This creates `data/raw/wikipedia_en.txt`, `data/raw/wikipedia_vi.txt`, and `data/raw/wikipedia_manifest.json`. Review the manifest and the Wikimedia license information before redistributing the data. Then rerun the preparation/tokenizer/encoding commands above. Use `configs\base.json` for the larger model only after confirming that the data volume and available disk space are sufficient; it is configured for 10,000 steps and is intentionally not launched by the downloader.

## Simple GUI

Launch the local Tkinter interface:

```powershell
cd D:\LLM
& .\.venv\Scripts\python.exe -m mini_gpt.gui
```

The local model is named **Ru**. The window supports checkpoint/tokenizer selection, a multi-turn conversation, maximum generated tokens, temperature, and top-k. Generation runs in a background thread so the window remains responsive.

Use `Send` to continue the current in-memory chat. `New chat` clears only that session. After a useful answer, press `Mark last response useful`; only that user/assistant pair is appended to the local UTF-8 JSONL file:

```text
D:\LLM\data\conversations\approved.jsonl
```

The model does not update its weights automatically when an example is saved. Review the approved examples before using them in a future explicit fine-tuning run.

## Open conversation seed data

Ru can start its conversational fine-tuning from a bounded, openly licensed seed set while keeping user-approved chat separate. The downloader streams a deterministic sample from [UltraChat](https://huggingface.co/datasets/HuggingFaceH4/ultrachat_200k) (MIT), [OpenAssistant OASST2](https://huggingface.co/datasets/OpenAssistant/oasst2) (Apache-2.0), and [Vietnamese Dataset](https://huggingface.co/datasets/hoanghai2110/vietnamese-dataset) (Apache-2.0). It keeps the Hugging Face cache, the normalized data, and its manifest on D:.

The generated `data/conversations/open_seed.jsonl` records the prompt, response, language, source, and licence for every example. It is separate from the local, user-owned `approved.jsonl` produced by the GUI.

```powershell
cd D:\LLM
& .\.venv\Scripts\python.exe scripts\download_open_chat_data.py
& .\.venv\Scripts\python.exe -m mini_gpt.cli prepare-chat --input data/conversations/open_seed.jsonl --output-dir data/conversations/processed
& .\.venv\Scripts\python.exe -m mini_gpt.cli encode --text data/conversations/processed/train.txt --tokenizer data/tokenizer.json --output data/conversations/processed/train.bin
& .\.venv\Scripts\python.exe -m mini_gpt.cli encode --text data/conversations/processed/val.txt --tokenizer data/tokenizer.json --output data/conversations/processed/val.bin
```

## Fine-tuning for natural conversation

Complete base training first. Use the open seed-data commands above for the initial conversational checkpoint, then keep collecting and reviewing useful chat turns with the GUI for later fine-tuning. The chat preprocessor accepts either JSONL file; it requires at least two unique examples, but useful conversational improvement needs many more high-quality examples.

To fine-tune from manually approved examples instead, prepare and encode `approved.jsonl` without changing the Wikipedia corpus or tokenizer:

```powershell
cd D:\LLM
& .\.venv\Scripts\python.exe -m mini_gpt.cli prepare-chat --input data/conversations/approved.jsonl --output-dir data/conversations/processed
& .\.venv\Scripts\python.exe -m mini_gpt.cli encode --text data/conversations/processed/train.txt --tokenizer data/tokenizer.json --output data/conversations/processed/train.bin
& .\.venv\Scripts\python.exe -m mini_gpt.cli encode --text data/conversations/processed/val.txt --tokenizer data/tokenizer.json --output data/conversations/processed/val.bin
```

Fine-tune from the completed base model with a fresh optimizer and checkpoint directory:

```powershell
& .\.venv\Scripts\python.exe -m mini_gpt.cli train --config configs/chat_finetune.json --processed-dir data/conversations/processed --tokenizer data/tokenizer.json --checkpoint-dir checkpoints/chat-finetune --init-checkpoint checkpoints/base/latest.pt
```

`--init-checkpoint` copies only compatible model weights; it does not continue the base optimizer, scheduler, or step counter. Use `--resume checkpoints/chat-finetune/latest.pt` only to continue an interrupted chat fine-tuning run. When a validation-selected `checkpoints/chat-finetune/best.pt` exists, the GUI prefers it; otherwise it falls back to `latest.pt`. Use Browse to select the base checkpoint if a rollback is needed.

## Replacing the corpus

Put UTF-8 `.txt` documents under `data/raw`. A document can begin with `VI:` or `EN:` to contribute to the language report. Use only public-domain, openly licensed, or user-provided data with permission. Re-run preparation, tokenizer training, and encoding after changing the corpus.
