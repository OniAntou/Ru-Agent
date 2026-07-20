# Bilingual Mini-GPT

An educational Vietnamese-English decoder-only Transformer trained from random initialization. The project is intentionally small enough to run experiments on an RTX 4050 Laptop GPU with approximately 6 GB VRAM.

This repository is not a ChatGPT replacement. The included corpus is an authored smoke-test corpus; useful general-purpose behavior requires a much larger, openly licensed dataset.

## Setup

Use Python 3.11 and keep the environment on D::

```powershell
$python311 = 'C:\Users\USER\AppData\Roaming\uv\python\cpython-3.11.15-windows-x86_64-none\python.exe'
& $python311 -m venv D:\LLM\.venv
& D:\LLM\.venv\Scripts\python.exe -m pip install --upgrade pip
& D:\LLM\.venv\Scripts\python.exe -m pip install --no-cache-dir torch==2.9.0 --index-url https://download.pytorch.org/whl/cu130
& D:\LLM\.venv\Scripts\python.exe -m pip install numpy tokenizers pytest
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

## Replacing the corpus

Put UTF-8 `.txt` documents under `data/raw`. A document can begin with `VI:` or `EN:` to contribute to the language report. Use only public-domain, openly licensed, or user-provided data with permission. Re-run preparation, tokenizer training, and encoding after changing the corpus.
