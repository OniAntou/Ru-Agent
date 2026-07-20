# Response-Only Chat Fine-tuning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Train a separate Vietnamese-English conversational Ru checkpoint from the base model using a larger licensed corpus and loss only on assistant replies.

**Architecture:** `chat_sources.py` adds a V2 importer with 5CD multi-turn extraction and provenance. A focused `chat_supervision.py` module creates aligned token/label binaries, and a supervised window dataset excludes prompt-only windows. The CLI selects that dataset explicitly; the trainer automatically persists the best validation checkpoint and the GUI prefers V2 only when it exists.

**Tech Stack:** Python 3.11, Hugging Face `datasets` streaming, NumPy memmaps, PyTorch CUDA, existing byte-level BPE tokenizer, Tkinter, pytest.

---

## File structure

- Modify `D:\LLM\src\mini_gpt\chat_sources.py`: add 5CD source metadata/extraction and V2 bounded importer.
- Create `D:\LLM\scripts\download_response_chat_data.py`: explicit V2 downloader CLI on D:.
- Create `D:\LLM\src\mini_gpt\chat_supervision.py`: split JSONL pairs, encode tokens/response-only labels, and expose `SupervisedTokenWindowDataset`.
- Modify `D:\LLM\src\mini_gpt\cli.py`: add V2 preparation/encoding commands and select supervised datasets for `train --response-only`.
- Modify `D:\LLM\src\mini_gpt\train.py`: atomically write a real `best.pt` only when validation improves.
- Create `D:\LLM\configs\chat_response_v2.json`: lower-learning-rate 1,200-step V2 configuration.
- Modify `D:\LLM\src\mini_gpt\gui.py`: prefer V2 `best.pt`, then V2 `latest.pt`, before older checkpoints.
- Modify `D:\LLM\README.md`, `D:\LLM\TODO.md`, and V2 plan/spec documents: document only commands and results that actually work.
- Create `D:\LLM\tests\test_chat_supervision.py`; modify `tests\test_chat_sources.py`, `tests\test_cli.py`, `tests\test_training_smoke.py`, and `tests\test_gui.py`.

### Task 1: Import a larger licensed multi-turn V2 corpus

**Files:**
- Modify: `D:\LLM\src\mini_gpt\chat_sources.py`
- Create: `D:\LLM\scripts\download_response_chat_data.py`
- Modify: `D:\LLM\tests\test_chat_sources.py`

- [ ] **Step 1: Write the failing 5CD extraction test**

Add this test to `tests/test_chat_sources.py`:

```python
from mini_gpt.chat_sources import extract_5cd_pairs


def test_extracts_adjacent_human_gpt_pairs_from_5cd():
    record = {
        "conversations": [
            {"from": "system", "value": "ignored"},
            {"from": "human", "value": "Xin chào"},
            {"from": "gpt", "value": "Chào anh, hôm nay anh thế nào?"},
            {"from": "human", "value": "I am fine."},
            {"from": "gpt", "value": "Glad to hear that!"},
        ]
    }

    pairs = list(extract_5cd_pairs(record))

    assert [(pair.prompt, pair.response) for pair in pairs] == [
        ("Xin chào", "Chào anh, hôm nay anh thế nào?"),
        ("I am fine.", "Glad to hear that!"),
    ]
    assert all(pair.source == "5CD-AI/Vietnamese-Multi-turn-Chat-Alpaca" for pair in pairs)
    assert all(pair.license == "Apache-2.0" for pair in pairs)
```

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_chat_sources.py::test_extracts_adjacent_human_gpt_pairs_from_5cd -q`

Expected: FAIL because `extract_5cd_pairs` does not exist.

- [ ] **Step 2: Implement the minimal 5CD extractor**

Add these constants and function to `src/mini_gpt/chat_sources.py`:

```python
MULTI_TURN_SOURCE = "5CD-AI/Vietnamese-Multi-turn-Chat-Alpaca"
MULTI_TURN_LICENSE = "Apache-2.0"


def extract_5cd_pairs(record: dict[str, Any]) -> Iterator[ChatSeedRecord]:
    conversations = record.get("conversations")
    if not isinstance(conversations, list) or not all(isinstance(item, dict) for item in conversations):
        raise ValueError("5CD source record field 'conversations' must be a list of objects")
    for user, assistant in zip(conversations, conversations[1:]):
        if user.get("from") != "human" or assistant.get("from") != "gpt":
            continue
        prompt = _message_text(user, "value")
        response = _message_text(assistant, "value")
        yield ChatSeedRecord(
            prompt=prompt,
            response=response,
            language="vi" if any(character in prompt + response for character in "ăâđêôơưĂÂĐÊÔƠƯ") else "en",
            source=MULTI_TURN_SOURCE,
            license=MULTI_TURN_LICENSE,
        )
```

The function ignores system and non-adjacent messages; it never inserts them into the prompt.

- [ ] **Step 3: Run the extraction test**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_chat_sources.py -q`

Expected: PASS.

- [ ] **Step 4: Write the failing V2 importer test**

Add a fake `datasets.load_dataset` test. Supply one accepted pair for each of `5CD-AI/Vietnamese-Multi-turn-Chat-Alpaca`, `hoanghai2110/vietnamese-dataset`, `OpenAssistant/oasst2`, and `HuggingFaceH4/ultrachat_200k`. Call:

```python
manifest = download_response_chat_seed(
    tmp_path / "response_v2.jsonl",
    tmp_path / "response_v2_manifest.json",
    tmp_path / "cache",
    max_multiturn_examples=1,
    max_vietnamese_examples=1,
    max_oasst_examples=1,
    max_ultrachat_examples=1,
)
```

Assert four rows, all five provenance fields, `manifest["total_examples"] == 4`, each source has one accepted record, and no final files exist when any quota cannot be reached.

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_chat_sources.py -q`

Expected: FAIL because `download_response_chat_seed` does not exist.

- [ ] **Step 5: Implement the V2 importer and explicit script**

Add this public interface to `chat_sources.py`:

```python
def download_response_chat_seed(
    output_path: str | Path,
    manifest_path: str | Path,
    cache_dir: str | Path,
    max_multiturn_examples: int = 18_000,
    max_vietnamese_examples: int = 1_800,
    max_oasst_examples: int = 2_000,
    max_ultrachat_examples: int = 6_000,
) -> dict[str, Any]:
```

Validate all four positive quotas. Reuse the existing temporary-output, global exact-deduplication, quality-filter, and cache-environment behavior. Process this exact ordered source tuple:

```python
(
    (MULTI_TURN_SOURCE, "train", max_multiturn_examples, extract_5cd_pairs),
    (VIETNAMESE_SOURCE, "train", max_vietnamese_examples, extract_vietnamese_pairs),
    (OASST2_SOURCE, "train", max_oasst_examples, extract_oasst2_pairs),
    (ULTRACHAT_SOURCE, "train_sft", max_ultrachat_examples, extract_ultrachat_pairs),
)
```

Count input records, accepted records, and rejection reasons per source. On a short source, raise `ValueError(f"{source} yielded {accepted} acceptable examples; expected {quota}")`, delete only V2 partial files, and retain any prior final V2 output. The manifest must contain `generated_at_utc`, cache directory, all four limits, total examples, split, licence, scanned/accepted/rejected statistics.

Create `scripts/download_response_chat_data.py` using the existing downloader script pattern with defaults:

```text
--output D:\LLM\data\conversations\response_v2_seed.jsonl
--manifest D:\LLM\data\conversations\response_v2_manifest.json
--cache-dir D:\LLM\.hf
--max-multiturn 18000
--max-vietnamese 1800
--max-oasst 2000
--max-ultrachat 6000
```

It configures UTF-8 stdout, prints the JSON manifest, and returns zero on success.

- [ ] **Step 6: Verify and commit the V2 importer**

Run:

```powershell
cd D:\LLM
& .\.venv\Scripts\python.exe -m pytest tests\test_chat_sources.py -q
& .\.venv\Scripts\python.exe scripts\download_response_chat_data.py --help
git add src/mini_gpt/chat_sources.py scripts/download_response_chat_data.py tests/test_chat_sources.py
git diff --cached --check
git commit -m "feat: add response chat data importer"
```

Expected: all source tests pass and the help command lists all seven arguments.

### Task 2: Encode aligned assistant-only labels and safe windows

**Files:**
- Create: `D:\LLM\src\mini_gpt\chat_supervision.py`
- Create: `D:\LLM\tests\test_chat_supervision.py`

- [ ] **Step 1: Write the failing response-only label test**

Create `tests/test_chat_supervision.py` with a fake tokenizer whose `encode(text).ids` returns one deterministic integer per character and whose BOS/EOS IDs are 1 and 2. Add:

```python
from mini_gpt.chat_supervision import encode_response_chat_split


def test_encoder_masks_prompt_targets_and_supervises_response(tmp_path, monkeypatch):
    tokenizer = FakeTokenizer()
    monkeypatch.setattr("mini_gpt.chat_supervision.load_tokenizer", lambda _path: tokenizer)
    source = tmp_path / "train.jsonl"
    source.write_text('{"prompt":"Hi","response":"OK"}\n', encoding="utf-8")

    count = encode_response_chat_split(source, "unused.json", tmp_path / "train.tokens.bin", tmp_path / "train.labels.bin")

    labels = np.fromfile(tmp_path / "train.labels.bin", dtype=np.int32)
    assert count == len(labels)
    assert list(labels).count(-100) == len("User: Hi\nAssistant:") + 1
    assert list(labels[-3:]) == [ord("O"), ord("K"), 2]
```

Also add a test writing an `-100`-only label region longer than the context plus a short supervised response. Assert the dataset has no item whose target tensor is all `-100`.

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_chat_supervision.py -q`

Expected: FAIL because `mini_gpt.chat_supervision` does not exist.

- [ ] **Step 2: Implement corpus preparation, aligned encoding, and valid windows**

Create `chat_supervision.py` with these public functions and class:

```python
def prepare_response_chat_corpus(source_path: str | Path, output_dir: str | Path, validation_ratio: float = 0.1, seed: int = 42) -> ChatCorpusReport: ...

def encode_response_chat_split(source_path: str | Path, tokenizer_path: str | Path, token_output: str | Path, label_output: str | Path) -> int: ...

class SupervisedTokenWindowDataset(Dataset[tuple[torch.Tensor, torch.Tensor]]):
    def __init__(self, token_path: str | Path, label_path: str | Path, context_length: int) -> None: ...
```

`prepare_response_chat_corpus` uses the same UTF-8 JSONL validation, NFC normalization, exact-pair deduplication, `Random(seed)` split, and report fields as `prepare_chat_corpus`, but writes `train.jsonl` and `val.jsonl` retaining normalized `prompt` and `response` objects.

For every record, `encode_response_chat_split` must build:

```python
prefix = [bos_id, *tokenizer.encode(f"User: {prompt}\nAssistant:").ids]
completion = [*tokenizer.encode(response).ids, eos_id]
token_ids.extend([*prefix, *completion])
label_ids.extend([-100] * len(prefix) + completion)
```

Write equal-length `np.int32` token/label arrays atomically through sibling `.tmp` paths; require at least one supervised token and delete only temporary outputs on failure.

`SupervisedTokenWindowDataset` memmaps both equal-length binaries. Build allowed start indexes with a cumulative sum over `labels != -100`:

```python
starts = np.arange(len(tokens) - context_length)
counts = cumulative[starts + context_length + 1] - cumulative[starts + 1]
self.starts = starts[counts > 0]
```

For `start = self.starts[index]`, return `tokens[start:start + context_length]` as inputs and `labels[start + 1:start + context_length + 1]` as targets. Raise clear `ValueError`s for mismatched binaries, insufficient token count, or no supervised window.

- [ ] **Step 3: Run response-only data tests and commit**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_chat_supervision.py -q
git add src/mini_gpt/chat_supervision.py tests/test_chat_supervision.py
git diff --cached --check
git commit -m "feat: add response-only chat supervision"
```

Expected: response labels preserve answer tokens/EOS, prompt targets are exactly `-100`, and every training item has a supervised target.

### Task 3: Select supervised training and persist real best checkpoints

**Files:**
- Modify: `D:\LLM\src\mini_gpt\cli.py`
- Modify: `D:\LLM\src\mini_gpt\train.py`
- Create: `D:\LLM\configs\chat_response_v2.json`
- Modify: `D:\LLM\tests\test_cli.py`
- Modify: `D:\LLM\tests\test_training_smoke.py`

- [ ] **Step 1: Write failing CLI and best-checkpoint tests**

Add a CLI test that writes two response JSONL records, runs:

```python
assert main(["prepare-response-chat", "--input", str(source), "--output-dir", str(processed)]) == 0
assert (processed / "train.jsonl").is_file()
```

Then monkeypatch `encode_response_chat_split` and assert `main(["encode-response-chat", "--input", str(processed / "train.jsonl"), "--tokenizer", "data/tokenizer.json", "--tokens-output", str(tokens), "--labels-output", str(labels)]) == 0` calls it with the two outputs. Add a parser test that `train --response-only` is accepted.

In `test_training_smoke.py`, run a two-step CPU training job with validation every step and assert:

```python
assert (tmp_path / "checkpoints" / "best.pt").is_file()
best_payload = torch.load(tmp_path / "checkpoints" / "best.pt", map_location="cpu", weights_only=False)
assert best_payload["step"] in {1, 2}
assert best_payload["best_validation_loss"] <= result["validation_loss"]
```

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_cli.py tests\test_training_smoke.py -q
```

Expected: FAIL because the commands, flag, and automatic `best.pt` do not exist.

- [ ] **Step 2: Add commands, V2 config, and best checkpoint writes**

In `cli.py`, import `SupervisedTokenWindowDataset`, `encode_response_chat_split`, and `prepare_response_chat_corpus`. Add `prepare-response-chat` with `--input`, `--output-dir`, and `--validation-ratio`; add `encode-response-chat` with `--input`, `--tokenizer`, `--tokens-output`, and `--labels-output`. Add `train --response-only` as `store_true`.

When `args.response_only` is true, construct exactly:

```python
train_dataset = SupervisedTokenWindowDataset(processed / "train.tokens.bin", processed / "train.labels.bin", model_config.context_length)
validation_dataset = SupervisedTokenWindowDataset(processed / "val.tokens.bin", processed / "val.labels.bin", model_config.context_length)
```

Otherwise preserve existing `TokenWindowDataset` behavior.

In `train.py`, replace the validation update with:

```python
improved = False
if completed_step % training_config.validation_interval == 0 or completed_step == training_config.total_steps:
    latest_validation_loss = evaluate_loss(...)
    if latest_validation_loss < best_validation_loss:
        best_validation_loss = latest_validation_loss
        improved = True
```

Immediately after the existing interval/latest checkpoint writes, call the same `save_checkpoint` with `Path(checkpoint_dir) / "best.pt"` only when `improved` is true. Pass the same model, optimizer, scheduler, completed step, global best loss, model config, and training config so `best.pt` is a resume-compatible atomic checkpoint.

Create `configs/chat_response_v2.json` with the existing 8-layer/512-dimension model and:

```json
{
  "seed": 42,
  "batch_size": 1,
  "gradient_accumulation_steps": 16,
  "learning_rate": 0.00002,
  "weight_decay": 0.01,
  "warmup_steps": 100,
  "total_steps": 1200,
  "validation_interval": 50,
  "checkpoint_interval": 50,
  "log_interval": 10,
  "max_validation_batches": 20,
  "device": "auto",
  "amp": true
}
```

- [ ] **Step 3: Run focused training/CLI tests and commit**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_chat_supervision.py tests\test_cli.py tests\test_training_smoke.py -q
git add src/mini_gpt/cli.py src/mini_gpt/train.py configs/chat_response_v2.json tests/test_cli.py tests/test_training_smoke.py
git diff --cached --check
git commit -m "feat: train response-only chat checkpoints"
```

Expected: the normal base/chat commands retain existing behavior, V2 commands create aligned artifacts, and each validated run owns a genuine `best.pt`.

### Task 4: Prefer V2 only when it exists and document the new workflow

**Files:**
- Modify: `D:\LLM\src\mini_gpt\gui.py`
- Modify: `D:\LLM\tests\test_gui.py`
- Modify: `D:\LLM\README.md`
- Modify: `D:\LLM\docs\superpowers\plans\2026-07-20-response-only-chat-finetuning-plan.md`

- [ ] **Step 1: Write the failing V2 checkpoint-priority test**

Add to `tests/test_gui.py`:

```python
def test_default_paths_prefer_response_v2_best_checkpoint(tmp_path, monkeypatch):
    monkeypatch.setattr(gui, "PROJECT_ROOT", tmp_path)
    v2 = tmp_path / "checkpoints" / "chat-response-v2" / "best.pt"
    older = tmp_path / "checkpoints" / "chat-finetune" / "best.pt"
    v2.parent.mkdir(parents=True)
    older.parent.mkdir(parents=True)
    v2.touch()
    older.touch()

    checkpoint, _ = gui.default_paths()

    assert checkpoint == v2
```

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_gui.py::test_default_paths_prefer_response_v2_best_checkpoint -q`

Expected: FAIL because V2 is not a candidate.

- [ ] **Step 2: Implement V2 priority and update exact commands**

Place these candidates first in `default_paths()`:

```python
PROJECT_ROOT / "checkpoints" / "chat-response-v2" / "best.pt",
PROJECT_ROOT / "checkpoints" / "chat-response-v2" / "latest.pt",
```

In `README.md`, add a **Response-only V2 fine-tune** subsection with the four source identifiers/licences and these commands:

```powershell
cd D:\LLM
& .\.venv\Scripts\python.exe scripts\download_response_chat_data.py
& .\.venv\Scripts\python.exe -m mini_gpt.cli prepare-response-chat --input data/conversations/response_v2_seed.jsonl --output-dir data/conversations/response-v2
& .\.venv\Scripts\python.exe -m mini_gpt.cli encode-response-chat --input data/conversations/response-v2/train.jsonl --tokenizer data/tokenizer.json --tokens-output data/conversations/response-v2/train.tokens.bin --labels-output data/conversations/response-v2/train.labels.bin
& .\.venv\Scripts\python.exe -m mini_gpt.cli encode-response-chat --input data/conversations/response-v2/val.jsonl --tokenizer data/tokenizer.json --tokens-output data/conversations/response-v2/val.tokens.bin --labels-output data/conversations/response-v2/val.labels.bin
& .\.venv\Scripts\python.exe -m mini_gpt.cli train --response-only --config configs/chat_response_v2.json --processed-dir data/conversations/response-v2 --tokenizer data/tokenizer.json --checkpoint-dir checkpoints/chat-response-v2 --init-checkpoint checkpoints/base/latest.pt
```

State that V2 preserves the base and previous fine-tunes, `best.pt` is selected by validation, and no claim of ChatGPT-level ability is made.

- [ ] **Step 3: Verify docs, GUI priority, and commit**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_gui.py tests\test_chat_sources.py tests\test_chat_supervision.py -q
& .\.venv\Scripts\python.exe -m compileall -q src scripts tests
git diff --check
git add README.md src/mini_gpt/gui.py tests/test_gui.py docs/superpowers/plans/2026-07-20-response-only-chat-finetuning-plan.md
git diff --cached --check
git commit -m "feat: prefer Ru response chat checkpoint"
```

Expected: V2 is only selected after its checkpoint exists, all source/training docs are current, and prior checkpoint candidates remain fallbacks.

### Task 5: Run V2, evaluate honestly, and update the roadmap

**Files:**
- Modify: `D:\LLM\TODO.md`
- Generated, ignored: `D:\LLM\data\conversations\response_v2_seed.jsonl`
- Generated, ignored: `D:\LLM\data\conversations\response_v2_manifest.json`
- Generated, ignored: `D:\LLM\data\conversations\response-v2\*`
- Generated, ignored: `D:\LLM\checkpoints\chat-response-v2\*`

- [ ] **Step 1: Confirm space and create V2 artifacts**

Run:

```powershell
cd D:\LLM
Get-Item checkpoints\base\latest.pt, data\tokenizer.json | Select-Object FullName,Length,LastWriteTime
Get-PSDrive -Name D | Format-List Name,Free
& .\.venv\Scripts\python.exe scripts\download_response_chat_data.py
Get-Content -Raw data\conversations\response_v2_manifest.json
```

Expected: 27,800 accepted pairs, every source quota/licence present, and sufficient D: headroom for V2 checkpoints. Stop before encoding if a quota fails.

- [ ] **Step 2: Prepare, encode, and inspect labels**

Run the four response-only CLI preparation/encoding commands from Task 4. Then run:

```powershell
& .\.venv\Scripts\python.exe -c "import numpy as np; from pathlib import Path; labels=np.memmap(Path('data/conversations/response-v2/train.labels.bin'), dtype=np.int32, mode='r'); print({'total': len(labels), 'supervised': int((labels != -100).sum()), 'masked': int((labels == -100).sum())})"
```

Expected: train/validation splits are non-empty and both masked prompt labels and supervised response labels are present.

- [ ] **Step 3: Capture deterministic baselines and run V2 from base**

Use a small Python verification script that loads each checkpoint on CUDA, calls `torch.manual_seed(42)` before `generate_completion`, and records 60-token completions for:

```text
User: Xin chào
Assistant:

User: Hello, how are you today?
Assistant:
```

First record `checkpoints/base/latest.pt` and `checkpoints/chat-finetune/best.pt`. Then launch:

```powershell
& .\.venv\Scripts\python.exe -m mini_gpt.cli train --response-only --config configs/chat_response_v2.json --processed-dir data/conversations/response-v2 --tokenizer data/tokenizer.json --checkpoint-dir checkpoints/chat-response-v2 --init-checkpoint checkpoints/base/latest.pt
```

Expected: CUDA run starts at local step one, writes only V2 checkpoint files, and creates `best.pt` automatically at the first successful validation.

- [ ] **Step 4: Compare V2 best, verify the application, and update docs only on success**

Run the same deterministic prompt script against `checkpoints/chat-response-v2/best.pt`, then:

```powershell
& .\.venv\Scripts\python.exe -m pytest -q
& .\.venv\Scripts\python.exe -m compileall -q src scripts tests
& .\.venv\Scripts\python.exe -c "from mini_gpt.gui import default_paths; print(default_paths()[0])"
git diff --check
```

Adopt V2 only if both responses are non-empty, do not immediately emit a new `User:` label, and are visibly less repetitive than the old V1 best. If it fails, leave the GUI preference code in place but do not create V2 `best.pt` manually, do not mark the natural-conversation roadmap milestone complete, and report the evidence.

- [ ] **Step 5: Commit only verified roadmap/doc status**

After a passing manual quality gate, mark the V2 data and response-only fine-tune milestones complete in `TODO.md`; retain the broader natural-conversation objective as pending until Ru consistently passes manual chat tests. Then run:

```powershell
git add TODO.md docs/superpowers/plans/2026-07-20-response-only-chat-finetuning-plan.md
git diff --cached --check
git commit -m "docs: record Ru response-only fine-tune"
git status --short
```

## Self-review

- **Spec coverage:** Task 1 imports all four sources with quotas, filtering, provenance, and atomic output. Task 2 supplies assistant-only labels and supervised windows. Task 3 wires CLI/configuration and automatic best saving. Task 4 selects V2 safely and documents the workflow. Task 5 runs, compares, and only adopts a checkpoint after the explicit quality gate.
- **Placeholder scan:** no `TBD`, `TODO`, deferred implementation, or unspecified validation is present.
- **Type consistency:** `ChatSeedRecord`, `download_response_chat_seed`, `prepare_response_chat_corpus`, `encode_response_chat_split`, and `SupervisedTokenWindowDataset` use the same names and artifacts in every task.
