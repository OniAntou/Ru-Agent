# Chat Fine-tuning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert approved bilingual chat turns into a separate training corpus and fine-tune a base checkpoint with fresh optimizer state, without modifying the base model artifacts.

**Architecture:** `chat_finetune_data.py` owns validation, deduplication, deterministic splitting, and `User`/`Assistant` document formatting. `run_training` receives a distinct weight-only initialization path, while `--resume` remains responsible only for continuing the same optimizer/scheduler state. The CLI composes those pieces and writes all derived data/checkpoints to chat-specific directories.

**Tech Stack:** Python 3.11, existing PyTorch/NumPy/tokenizer training stack, JSONL, pytest, and the local Tkinter-approved conversation store.

---

### Task 1: Prepare approved chat data deterministically

**Files:**
- Create: `D:\LLM\src\mini_gpt\chat_finetune_data.py`
- Create: `D:\LLM\tests\test_chat_finetune_data.py`

- [ ] **Step 1: Write failing tests for Unicode formatting, deduplication, and deterministic split**

Create `tests/test_chat_finetune_data.py` with a helper that writes JSONL records. Include this test:

```python
from mini_gpt.chat_finetune_data import prepare_chat_corpus


def test_prepare_chat_corpus_deduplicates_and_preserves_vietnamese(tmp_path):
    source = tmp_path / "approved.jsonl"
    source.write_text(
        "\n".join(
            [
                '{"prompt":"Xin chào","response":"Chào anh"}',
                '{"prompt":"Xin chào","response":"Chào anh"}',
                '{"prompt":"How are you?","response":"I am well."}',
                '{"prompt":"Cảm ơn","response":"Rất vui được giúp anh."}',
            ]
        )
        + "\n",
        encoding="utf-8",
    )

    report = prepare_chat_corpus(source, tmp_path / "processed", validation_ratio=0.5)

    assert report.examples_before_deduplication == 4
    assert report.examples_after_deduplication == 3
    assert "User: Xin chào\nAssistant: Chào anh" in (tmp_path / "processed" / "train.txt").read_text(encoding="utf-8") or "User: Xin chào\nAssistant: Chào anh" in (tmp_path / "processed" / "val.txt").read_text(encoding="utf-8")
```

Also test that blank prompt/response, malformed JSON, and fewer than two unique pairs raise clear `ValueError`s; run preparation twice with the same seed and assert both split files are byte-identical.

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_chat_finetune_data.py -q`

Expected: FAIL because `mini_gpt.chat_finetune_data` does not exist.

- [ ] **Step 2: Implement the chat corpus module**

Add:

```python
@dataclass(frozen=True)
class ChatCorpusReport:
    examples_before_deduplication: int
    examples_after_deduplication: int
    train_examples: int
    validation_examples: int


def format_chat_example(prompt: str, response: str) -> str:
    return f"User: {prompt}\nAssistant: {response}"
```

Read UTF-8 JSONL one line at a time; require object records with string `prompt` and `response`, normalize text with the existing `normalize_text`, reject blank values with their source line number, deduplicate exact normalized pairs, require two or more pairs, shuffle with `Random(seed)`, and write `train.txt`, `val.txt`, and `report.json`. Use the same validation-count formula as `prepare_corpus`.

- [ ] **Step 3: Run focused chat-data tests**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_chat_finetune_data.py -q`

Expected: PASS.

### Task 2: Add fresh weight-only model initialization

**Files:**
- Modify: `D:\LLM\src\mini_gpt\train.py`
- Modify: `D:\LLM\tests\test_training_smoke.py`

- [ ] **Step 1: Write a failing init-checkpoint smoke test**

Create a tiny source checkpoint using `run_training` for two CPU steps. Then call a second run with `initial_checkpoint_path=source_checkpoint`, `total_steps=1`, and a separate output directory. Assert the second result has `step == 1`, its `latest.pt` exists, and its payload has `step == 1` rather than inheriting the source checkpoint's step.

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_training_smoke.py -q`

Expected: FAIL because `run_training` has no `initial_checkpoint_path` argument.

- [ ] **Step 2: Implement weight-only initialization**

Extend `run_training` with `initial_checkpoint_path: str | Path | None = None`. Raise `ValueError("resume_path and initial_checkpoint_path are mutually exclusive")` when both are supplied. Preserve the current resume branch exactly. In the new initialization branch call:

```python
load_checkpoint(initial_checkpoint_path, model, device=device, restore_rng=False)
```

Do not pass optimizer or scheduler. Leave `start_step = 0`, `best_validation_loss = inf`, and the fresh seed/scheduler intact.

- [ ] **Step 3: Run training smoke tests**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_training_smoke.py -q`

Expected: PASS.

### Task 3: Expose the fine-tuning commands and configuration

**Files:**
- Modify: `D:\LLM\src\mini_gpt\cli.py`
- Create: `D:\LLM\configs\chat_finetune.json`
- Modify: `D:\LLM\tests\test_cli.py`

- [ ] **Step 1: Write failing CLI tests**

Add a `prepare-chat` invocation that writes a two-record source JSONL, assert `main(["prepare-chat", "--input", str(source), "--output-dir", str(processed)]) == 0`, and check `processed/train.txt` and `processed/val.txt` exist. Add a parser test asserting `train --resume state.pt --init-checkpoint base.pt` exits with parser error.

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_cli.py -q`

Expected: FAIL because `prepare-chat` and `--init-checkpoint` do not exist.

- [ ] **Step 2: Implement the CLI composition**

Import `prepare_chat_corpus`. Add `prepare-chat` with `--input` defaulting to `data/conversations/approved.jsonl`, `--output-dir` defaulting to `data/conversations/processed`, and `--validation-ratio` defaulting to `0.1`. Replace the train parser's standalone resume argument with a mutually exclusive group containing `--resume` and `--init-checkpoint`. Pass the resolved initialization path to `run_training`.

- [ ] **Step 3: Add the fine-tuning config**

Create `configs/chat_finetune.json` with the 8-layer/512-dimension model shape from `configs/base.json` and this training payload:

```json
{
  "seed": 42,
  "batch_size": 1,
  "gradient_accumulation_steps": 16,
  "learning_rate": 0.00005,
  "weight_decay": 0.01,
  "warmup_steps": 100,
  "total_steps": 1000,
  "validation_interval": 100,
  "checkpoint_interval": 100,
  "log_interval": 10,
  "max_validation_batches": 20,
  "device": "auto",
  "amp": true
}
```

- [ ] **Step 4: Run focused CLI tests**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_cli.py tests/test_training_smoke.py -q`

Expected: PASS.

### Task 4: Document, verify, and preserve the base run

**Files:**
- Modify: `D:\LLM\README.md`
- Modify: `D:\LLM\docs\superpowers\plans\2026-07-20-chat-finetuning-plan.md`

- [ ] **Step 1: Document the post-base workflow**

Add exact commands for `prepare-chat`, two existing `encode` commands targeting `data/conversations/processed`, and `train --init-checkpoint checkpoints/base/latest.pt --checkpoint-dir checkpoints/chat-finetune`. State that the base job must finish first, at least two examples are required technically but a useful conversational fine-tune needs a substantially reviewed dialogue set, and the user must select the chat-finetune checkpoint in the GUI.

- [ ] **Step 2: Run full verification without launching fine-tuning**

Run:

```powershell
cd D:\LLM
& .\.venv\Scripts\python.exe -m compileall -q src tests scripts
& .\.venv\Scripts\python.exe -m pytest -q
git diff --check
```

Expected: compile succeeds, all tests pass, and no whitespace errors appear. Do not run the production chat fine-tune because the approved data file may be empty and the base run may still be active.

- [ ] **Step 3: Inspect documents, stage, and commit**

Review all files under `D:\LLM\docs` and `README.md` for stale training paths. Mark this plan complete, stage only the feature files, run `git diff --cached --check`, and commit:

```powershell
git add README.md configs/chat_finetune.json docs/superpowers/plans/2026-07-20-chat-finetuning-plan.md src/mini_gpt/chat_finetune_data.py src/mini_gpt/cli.py src/mini_gpt/train.py tests/test_chat_finetune_data.py tests/test_cli.py tests/test_training_smoke.py
git diff --cached --check
git commit -m "feat: add chat fine-tuning pipeline"
```

## Self-review

- Fine-tune data and checkpoints use paths separate from the active base run.
- Weight-only initialization starts a fresh local step counter and never restores the base optimizer, scheduler, or RNG state.
- The base model architecture and tokenizer are reused exactly.
- The implementation does not auto-launch a fine-tuning job or train on unreviewed chat data.
