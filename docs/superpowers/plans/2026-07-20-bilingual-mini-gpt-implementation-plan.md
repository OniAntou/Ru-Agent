# Bilingual Mini-GPT Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and verify a small Vietnamese-English decoder-only Transformer trained from random initialization on Windows at `D:\LLM`.

**Architecture:** Use a `src`-layout Python package with separate configuration, tokenizer, data, model, checkpoint, training, and evaluation modules. The first runnable path uses a tiny authored bilingual corpus for smoke testing; users can replace it with openly licensed general text without changing the training code.

**Tech Stack:** Python 3.11, PyTorch, Hugging Face Tokenizers, NumPy memmaps, pytest, and a local virtual environment at `D:\LLM\.venv`.

---

## File map

- Create `D:\LLM\pyproject.toml` for package metadata, dependencies, and pytest configuration.
- Create `D:\LLM\.gitignore` for virtual environments and generated training artifacts.
- Create `D:\LLM\README.md` with setup, data, training, generation, and verification commands.
- Create `D:\LLM\src\mini_gpt\__init__.py` as the package boundary.
- Create `D:\LLM\src\mini_gpt\config.py` for validated model and training dataclasses.
- Create `D:\LLM\src\mini_gpt\tokenizer.py` for byte-level BPE training and loading.
- Create `D:\LLM\src\mini_gpt\data.py` for UTF-8 normalization, document splitting, token encoding, and memmap windows.
- Create `D:\LLM\src\mini_gpt\model.py` for LayerNorm Transformer blocks, causal attention, GELU MLPs, and the language-model head.
- Create `D:\LLM\src\mini_gpt\checkpointing.py` for atomic save/load of complete training state.
- Create `D:\LLM\src\mini_gpt\train.py` for deterministic training, validation, mixed precision, and checkpoint resume.
- Create `D:\LLM\src\mini_gpt\evaluate.py` for validation and autoregressive generation.
- Create `D:\LLM\src\mini_gpt\cli.py` for stable `prepare`, `tokenizer`, `train`, and `generate` commands.
- Create `D:\LLM\tests\` covering configuration, tokenizer, data, model, checkpoint, and training smoke behavior.
- Create `D:\LLM\data\raw\demo_bilingual.txt` as a small authored corpus proving the path without importing copyrighted data.

### Task 1: Bootstrap the Python project

**Files:** `pyproject.toml`, `.gitignore`, `README.md`, `src/mini_gpt/__init__.py`, `tests/conftest.py`

- [x] Create a Python 3.11 virtual environment on D: and install the declared dependencies:

```powershell
$python311 = 'C:\Users\USER\AppData\Roaming\uv\python\cpython-3.11.15-windows-x86_64-none\python.exe'
& $python311 -m venv D:\LLM\.venv
& D:\LLM\.venv\Scripts\python.exe -m pip install --upgrade pip
& D:\LLM\.venv\Scripts\python.exe -m pip install --no-cache-dir torch==2.9.0 --index-url https://download.pytorch.org/whl/cu130
& D:\LLM\.venv\Scripts\python.exe -m pip install numpy tokenizers pytest
& D:\LLM\.venv\Scripts\python.exe -m pip install -e D:\LLM --no-deps
```

- [x] Add `pyproject.toml` with a `mini-gpt` package, `src` layout, dependencies `torch`, `tokenizers`, and `numpy`, and pytest configured to discover `tests`.
- [x] Add `.gitignore` entries for `.venv`, `__pycache__`, `.pytest_cache`, generated `data/processed`, generated `checkpoints`, and tokenizer build outputs.
- [x] Add a README that states the project is a from-scratch educational bilingual model and shows the virtual-environment interpreter path for every command.
- [x] Run the import smoke command and the empty test suite:

```powershell
& D:\LLM\.venv\Scripts\python.exe -c "import torch, tokenizers, numpy; print(torch.__version__); print(torch.cuda.is_available())"
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
```

Expected result: dependencies import successfully and pytest reports no collection errors.

### Task 2: Add configuration and tokenizer behavior

**Files:** `src/mini_gpt/config.py`, `src/mini_gpt/tokenizer.py`, `tests/test_config.py`, `tests/test_tokenizer.py`

- [x] Write failing tests asserting that invalid head divisibility, non-positive context length, and invalid vocabulary sizes raise `ValueError`, while the tiny test configuration is valid.
- [x] Implement `ModelConfig` with fields `vocab_size`, `context_length`, `n_layers`, `d_model`, `n_heads`, `d_ff`, `dropout`, and `tie_embeddings`; validate `d_model % n_heads == 0` and positive dimensions.
- [x] Implement `TrainingConfig` with fields for seed, batch size, gradient accumulation, learning rate, weight decay, warmup steps, total steps, validation interval, checkpoint interval, and device selection.
- [x] Write a tokenizer test using the text `Xin chào Việt Nam. Hello English.` and assert that `decode(encode(text))` preserves both languages and Vietnamese diacritics.
- [x] Implement `train_tokenizer(text_files, output_path, vocab_size)` using byte-level BPE with explicit `<pad>`, `<bos>`, `<eos>`, and `<unk>` tokens; implement `load_tokenizer(path)` with a clear missing-file error.
- [x] Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_config.py tests/test_tokenizer.py -q
```

Expected result: all configuration and Unicode tokenizer tests pass.

### Task 3: Implement corpus preparation and token windows

**Files:** `src/mini_gpt/data.py`, `tests/test_data.py`, `data/raw/demo_bilingual.txt`

- [x] Write tests for UTF-8 normalization, exact duplicate removal, deterministic document-level train/validation splitting, causal `x`/`y` shifting, and a clear error for an empty corpus.
- [x] Add the authored demo corpus with separate Vietnamese and English paragraphs, enough repetition to exercise both languages but no external copyrighted text.
- [x] Implement `prepare_corpus(raw_dir, processed_dir, validation_ratio, seed)` to read `.txt` files, normalize Unicode NFC and line endings, drop empty/duplicate documents, split deterministically with language-stratified validation when possible, and write text, JSONL, and report artifacts.
- [x] Implement `encode_split(text_path, tokenizer_path, output_path)` using `np.int32` token IDs and add BOS/EOS at document boundaries.
- [x] Implement `TokenWindowDataset` over `np.memmap` with `__getitem__` returning `input_ids[:-1]` and `input_ids[1:]` for a configured context length.
- [x] Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_data.py -q
```

Expected result: data tests pass without loading the complete corpus into RAM.

### Task 4: Implement the decoder-only Transformer

**Files:** `src/mini_gpt/model.py`, `tests/test_model.py`

- [x] Write tests asserting logits shape `[batch, sequence, vocab]`, scalar cross-entropy loss, causal masking, parameter count within the configured range for the default model, and a tiny model that can run on CPU.
- [x] Implement `CausalSelfAttention` with QKV projection, PyTorch scaled-dot-product attention with a causal mask, output projection, and dropout.
- [x] Implement `MLP` with two linear layers and GELU, `TransformerBlock` with pre-LayerNorm residual connections, and `MiniGPT` with token/position embeddings, blocks, final LayerNorm, tied output projection, and optional targets.
- [x] Initialize linear and embedding weights with a small normal distribution and zero biases; initialize LayerNorm scale to one and bias to zero.
- [x] Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_model.py -q
```

Expected result: shape, masking, loss, and CPU forward tests pass.

### Task 5: Add checkpointing and deterministic training

**Files:** `src/mini_gpt/checkpointing.py`, `src/mini_gpt\train.py`, `tests/test_checkpointing.py`, `tests/test_training_smoke.py`

- [x] Write a checkpoint round-trip test that saves model, optimizer, scheduler, step, validation loss, and RNG states, changes the model, loads the checkpoint, and verifies the original step and parameters return.
- [x] Implement `save_checkpoint` by writing to a sibling `.tmp` file and replacing the target only after serialization succeeds; implement `load_checkpoint` with configuration compatibility checks.
- [x] Write a CPU training smoke test using a tiny model and a few token windows; assert loss is finite and the checkpoint is created.
- [x] Implement deterministic seeding for Python, NumPy, and PyTorch, a DataLoader with a seeded generator, AdamW, warmup-plus-cosine scheduling, gradient accumulation, gradient clipping, and validation loss.
- [x] Enable `torch.amp.autocast("cuda", dtype=torch.float16)` and a CUDA GradScaler only when the selected device is CUDA; never silently switch a requested CUDA run to CPU.
- [x] Print device, parameter count, step, losses, learning rate, token count, and peak CUDA memory at the configured log interval.
- [x] Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_checkpointing.py tests/test_training_smoke.py -q
```

Expected result: checkpoint restore and short CPU training pass.

### Task 6: Add CLI, generation, and documentation

**Files:** `src/mini_gpt/evaluate.py`, `src/mini_gpt/cli.py`, `tests/test_cli.py`, `README.md`

- [x] Write CLI tests asserting that `--help` works and missing required artifacts produce non-zero exit status with an actionable message.
- [x] Implement `evaluate` for separate Vietnamese and English validation loss and implement temperature/top-k autoregressive generation with a maximum output length.
- [x] Implement CLI subcommands:

```text
python -m mini_gpt.cli prepare --raw-dir data/raw --processed-dir data/processed
python -m mini_gpt.cli tokenizer --text-dir data/processed --output data/tokenizer.json
python -m mini_gpt.cli encode --text data/processed/train.txt --tokenizer data/tokenizer.json --output data/processed/train.bin
python -m mini_gpt.cli train --config configs/tiny.json
python -m mini_gpt.cli evaluate --checkpoint checkpoints/latest.pt --validation data/processed/val.jsonl
python -m mini_gpt.cli generate --checkpoint checkpoints/latest.pt --prompt "Xin chào"
```

- [x] Make CLI paths resolve relative to the project root and keep generated artifacts on D:.
- [x] Update README with the actual commands, expected artifact paths, GPU check, and a warning that the demo corpus is only a smoke test.
- [x] Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_cli.py -q
```

Expected result: CLI help, error handling, and generation-path tests pass.

### Task 7: Execute the bilingual end-to-end smoke test

**Files:** `configs/tiny.json`, generated `data/processed/*`, generated `checkpoints/*`

- [x] Add a tiny configuration with 2 layers, width 128, 4 heads, feed-forward width 512, context 32, micro-batch 1, gradient accumulation 1, and 20 training steps so it fits the laptop for verification.
- [x] Run corpus preparation, tokenizer training, token encoding, and the tiny training run using `D:\LLM\.venv\Scripts\python.exe`.
- [x] Run bilingual validation and generation prompts in Vietnamese and English; record device, CUDA availability, parameter count, final loss, and generated samples in the terminal output.
- [x] Run the complete test suite and `git diff --check`.
- [x] Inspect project documents and remove or update stale setup instructions introduced by the implementation, then commit the verified code and documentation.

## Self-review checklist

- The design requirements map to Tasks 2–7: bilingual tokenizer and corpus reporting, model architecture, memory-aware training, atomic checkpoints, evaluation, CLI, and documentation.
- The plan contains no unresolved architecture alternatives or placeholder steps.
- The public interfaces use consistent names: `ModelConfig`, `TrainingConfig`, `train_tokenizer`, `load_tokenizer`, `prepare_corpus`, `TokenWindowDataset`, `MiniGPT`, `save_checkpoint`, and `load_checkpoint`.
- The demo path is intentionally tiny and does not claim production-quality language modeling.
