# Open Chat Seed Data Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a reproducible, provenance-preserving Vietnamese-English conversation seed corpus for Ru, fine-tune it from the completed base checkpoint, and make the GUI select the resulting chat checkpoint by default.

**Architecture:** A new `chat_sources.py` module owns source-specific record extraction, quality filtering, exact deduplication, quota enforcement, UTF-8 JSONL output, and manifest creation. A small script composes its CLI. The existing chat preparation and training pipeline consumes the produced `prompt`/`response` fields unchanged; the GUI adds the completed chat-fine-tuning checkpoint as its highest-priority default.

**Tech Stack:** Python 3.11, Hugging Face `datasets` streaming, JSONL, existing byte-level BPE tokenizer, PyTorch CUDA training, Tkinter, pytest.

---

## File structure

- Create `D:\LLM\src\mini_gpt\chat_sources.py`: source constants, normalized seed record, extraction functions, quality filter, deterministic importer, and manifest writer.
- Create `D:\LLM\scripts\download_open_chat_data.py`: explicit D: drive CLI entry point for the importer.
- Create `D:\LLM\tests\test_chat_sources.py`: network-free source/extraction/filter/importer tests using fake dataset streams.
- Modify `D:\LLM\src\mini_gpt\gui.py`: select `checkpoints/chat-finetune/latest.pt` before the base checkpoint when it exists.
- Modify `D:\LLM\tests\test_gui.py`: cover priority selection without loading a model.
- Modify `D:\LLM\README.md`: document sources, licences, exact ingestion/fine-tuning commands, and automatic checkpoint preference.
- Modify `D:\LLM\TODO.md`: mark the open seed-data and initial fine-tune milestones only after their runtime commands succeed.

### Task 1: Test source extraction and conversational-quality policy

**Files:**
- Create: `D:\LLM\tests\test_chat_sources.py`
- Create: `D:\LLM\src\mini_gpt\chat_sources.py`

- [ ] **Step 1: Write failing record-extraction and filter tests**

Create `tests/test_chat_sources.py` with minimal source records and these assertions:

```python
from mini_gpt.chat_sources import (
    OASST2_LICENSE,
    OASST2_SOURCE,
    extract_oasst2_pairs,
    extract_ultrachat_pairs,
    extract_vietnamese_pairs,
    quality_rejection_reason,
)


def test_extracts_normalized_pairs_from_each_source():
    ultrachat = {
        "messages": [
            {"role": "user", "content": " Hello\r\nthere "},
            {"role": "assistant", "content": "Hi! How can I help?"},
        ]
    }
    assert list(extract_ultrachat_pairs(ultrachat))[0].prompt == "Hello\nthere"

    vietnamese = {
        "messages": [
            {"role": "user", "content": "Xin chào"},
            {"role": "assistant", "content": "Chào anh, hôm nay anh thế nào?"},
        ]
    }
    assert list(extract_vietnamese_pairs(vietnamese))[0].language == "vi"

    records = [
        {"message_id": "p", "role": "prompter", "text": "How are you?", "lang": "en"},
        {
            "message_id": "a", "parent_id": "p", "role": "assistant",
            "text": "I am doing well, thank you!", "lang": "en",
            "deleted": False, "review_result": True, "synthetic": False,
        },
    ]
    pair = list(extract_oasst2_pairs(records))[0]
    assert (pair.source, pair.license) == (OASST2_SOURCE, OASST2_LICENSE)


def test_filter_rejects_code_roleplay_links_and_oversized_pairs():
    assert quality_rejection_reason("hi", "```python\nprint('x')\n```") == "code_or_markup"
    assert quality_rejection_reason("nhập vai phù thủy", "Được rồi, ta bắt đầu nhé.") == "roleplay"
    assert quality_rejection_reason("hello", "See https://example.com now") == "link"
    assert quality_rejection_reason("x" * 401, "y" * 401) == "too_long"
    assert quality_rejection_reason("Xin chào", "Chào anh!") is None
```

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_chat_sources.py -q`

Expected: FAIL because `mini_gpt.chat_sources` does not exist.

- [ ] **Step 2: Implement focused source-pair and quality functions**

Create `src/mini_gpt/chat_sources.py` with an immutable record and explicit metadata:

```python
@dataclass(frozen=True)
class ChatSeedRecord:
    prompt: str
    response: str
    language: str
    source: str
    license: str


ULTRACHAT_SOURCE = "HuggingFaceH4/ultrachat_200k"
ULTRACHAT_LICENSE = "MIT"
OASST2_SOURCE = "OpenAssistant/oasst2"
OASST2_LICENSE = "Apache-2.0"
VIETNAMESE_SOURCE = "hoanghai2110/vietnamese-dataset"
VIETNAMESE_LICENSE = "Apache-2.0"
```

Implement `_require_message_text(record, field)` to raise `ValueError` with the source field name for missing/non-string values. Implement `extract_ultrachat_pairs(record)` and `extract_vietnamese_pairs(record)` to scan `messages` in order and yield only consecutive `user` then `assistant` messages. Normalize both fields with `mini_gpt.data.normalize_text`; assign `en` to UltraChat and `vi` to the Vietnamese dataset.

Implement `extract_oasst2_pairs(records)` using a `message_id -> prompter record` map and a pending-child map so an assistant message works whether it appears before or after its parent. Yield only an `assistant` whose parent is a `prompter`, both texts are strings, `deleted is False`, `review_result is True`, and `synthetic is False`. Require `lang` to be `en` or `vi` and equal for both messages.

Implement `quality_rejection_reason(prompt, response, max_characters=800)` with exactly these ordered rules:

```python
if len(prompt) < 1 or len(response) < 2:
    return "too_short"
if len(prompt) + len(response) > max_characters:
    return "too_long"
if "```" in prompt or "```" in response or re.search(r"<[^>]{1,80}>", prompt + response):
    return "code_or_markup"
if re.search(r"\b(roleplay|as an ai language model)\b|nhập vai|đóng vai", prompt + "\n" + response, re.IGNORECASE):
    return "roleplay"
if re.search(r"https?://|www\.", prompt + "\n" + response, re.IGNORECASE):
    return "link"
return None
```

Do not strip friendly short greetings beyond the two-character response guard. Do not generate or translate examples.

- [ ] **Step 3: Run focused extraction tests**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_chat_sources.py -q`

Expected: PASS.

- [ ] **Step 4: Commit the extraction layer**

Run:

```powershell
cd D:\LLM
git add src/mini_gpt/chat_sources.py tests/test_chat_sources.py
git diff --cached --check
git commit -m "feat: add open chat source filters"
```

Expected: a commit containing only the new extractor module and its tests.

### Task 2: Stream bounded sources into provenance-preserving JSONL

**Files:**
- Modify: `D:\LLM\src\mini_gpt\chat_sources.py`
- Modify: `D:\LLM\tests\test_chat_sources.py`
- Create: `D:\LLM\scripts\download_open_chat_data.py`

- [ ] **Step 1: Write failing importer and script-help tests**

Add this importer test using a fake `datasets.load_dataset` module through `monkeypatch.setitem(sys.modules, "datasets", ...)`:

```python
import json
import sys
from types import SimpleNamespace

from mini_gpt.chat_sources import download_open_chat_seed


def test_importer_writes_deduplicated_jsonl_and_manifest(tmp_path, monkeypatch):
    streams = {
        ("HuggingFaceH4/ultrachat_200k", "train_sft"): [{"messages": [
            {"role": "user", "content": "Hello"},
            {"role": "assistant", "content": "Hi there!"},
            {"role": "user", "content": "Hello"},
            {"role": "assistant", "content": "Hi there!"},
            {"role": "user", "content": "How are you?"},
            {"role": "assistant", "content": "I am well, thank you!"},
        ]}],
        ("OpenAssistant/oasst2", "train"): [
            {"message_id": "p", "role": "prompter", "text": "Xin chào", "lang": "vi"},
            {"message_id": "a", "parent_id": "p", "role": "assistant", "text": "Chào anh!", "lang": "vi", "deleted": False, "review_result": True, "synthetic": False},
        ],
        ("hoanghai2110/vietnamese-dataset", "train"): [{"messages": [
            {"role": "user", "content": "Bạn khỏe không?"},
            {"role": "assistant", "content": "Mình khỏe, cảm ơn anh!"},
        ]}],
    }
    monkeypatch.setitem(sys.modules, "datasets", SimpleNamespace(
        load_dataset=lambda name, split, streaming: streams[(name, split)]
    ))

    manifest = download_open_chat_seed(
        tmp_path / "open_seed.jsonl", tmp_path / "manifest.json", tmp_path / "cache",
        max_ultrachat_examples=2, max_oasst_examples=1, max_vietnamese_examples=1,
    )

    rows = [json.loads(line) for line in (tmp_path / "open_seed.jsonl").read_text(encoding="utf-8").splitlines()]
    assert len(rows) == 4
    assert set(rows[0]) == {"prompt", "response", "language", "source", "license"}
    assert manifest["sources"]["HuggingFaceH4/ultrachat_200k"]["accepted"] == 2
    assert manifest["sources"]["HuggingFaceH4/ultrachat_200k"]["rejected"]["duplicate"] == 1
```

Also test that a stream ending before its requested quota raises `ValueError` with the source name, leaves no final JSONL/manifest, and that the repository-root script prints help successfully.

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_chat_sources.py -q`

Expected: FAIL because `download_open_chat_seed` and the script do not exist.

- [ ] **Step 2: Implement deterministic streaming, quota, and atomic-output behavior**

Add this public function to `chat_sources.py`:

```python
def download_open_chat_seed(
    output_path: str | Path,
    manifest_path: str | Path,
    cache_dir: str | Path,
    max_ultrachat_examples: int = 2_000,
    max_oasst_examples: int = 400,
    max_vietnamese_examples: int = 1_500,
) -> dict[str, Any]:
```

Validate every quota is positive. Set `HF_HOME`, `HF_DATASETS_CACHE`, and `HF_HUB_CACHE` under the supplied cache directory before importing `datasets`. Process sources in this fixed order:

```python
(
    (ULTRACHAT_SOURCE, "train_sft", max_ultrachat_examples, extract_ultrachat_pairs),
    (OASST2_SOURCE, "train", max_oasst_examples, extract_oasst2_pairs),
    (VIETNAMESE_SOURCE, "train", max_vietnamese_examples, extract_vietnamese_pairs),
)
```

For each source, increment `scanned` per input record, run its extractor, then apply `quality_rejection_reason`. Use a process-wide `set[(prompt, response)]` for exact deduplication; count duplicate pairs as `duplicate`. Stop the source only after exactly its quota has been accepted. If the stream ends first, raise `ValueError(f"{source} yielded {accepted} acceptable examples; expected {quota}")`.

Write each accepted record with `json.dumps(asdict(record), ensure_ascii=False)` to `<output>.partial`. Build a manifest containing `generated_at_utc`, `cache_dir`, all requested limits, `total_examples`, and for each source its split, licence, scanned/accepted counts, and rejection counts. Write it to `<manifest>.partial`; use `os.replace` for both final paths only after all three source quotas are met. On an exception, remove only the two known temporary paths and re-raise.

Create `scripts/download_open_chat_data.py` following the existing Wikipedia-script pattern. Resolve `PROJECT_ROOT` from the script path and expose these arguments:

```text
--output             default D:\LLM\data\conversations\open_seed.jsonl
--manifest           default D:\LLM\data\conversations\open_seed_manifest.json
--cache-dir          default D:\LLM\.hf
--max-ultrachat      default 2000
--max-oasst          default 400
--max-vietnamese     default 1500
```

Call `download_open_chat_seed(...)`, print its manifest with `ensure_ascii=False`, and call `sys.stdout.reconfigure(encoding="utf-8", errors="replace")` when available.

- [ ] **Step 3: Run importer tests and script help**

Run:

```powershell
cd D:\LLM
& .\.venv\Scripts\python.exe -m pytest tests/test_chat_sources.py -q
& .\.venv\Scripts\python.exe scripts\download_open_chat_data.py --help
```

Expected: tests pass and help lists all six arguments.

- [ ] **Step 4: Commit the downloader**

Run:

```powershell
git add src/mini_gpt/chat_sources.py scripts/download_open_chat_data.py tests/test_chat_sources.py
git diff --cached --check
git commit -m "feat: add open chat seed downloader"
```

Expected: a commit containing the bounded importer, script, and expanded tests.

### Task 3: Prefer the fine-tuned checkpoint and document the supported flow

**Files:**
- Modify: `D:\LLM\src\mini_gpt\gui.py`
- Modify: `D:\LLM\tests\test_gui.py`
- Modify: `D:\LLM\README.md`

- [ ] **Step 1: Write a failing checkpoint-priority test**

Add to `tests/test_gui.py`:

```python
from mini_gpt import gui


def test_default_paths_prefer_completed_chat_finetune(tmp_path, monkeypatch):
    monkeypatch.setattr(gui, "PROJECT_ROOT", tmp_path)
    chat_checkpoint = tmp_path / "checkpoints" / "chat-finetune" / "latest.pt"
    chat_checkpoint.parent.mkdir(parents=True)
    chat_checkpoint.touch()
    (tmp_path / "checkpoints" / "base").mkdir(parents=True)
    (tmp_path / "checkpoints" / "base" / "latest.pt").touch()

    checkpoint, _ = gui.default_paths()

    assert checkpoint == chat_checkpoint
```

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_gui.py::test_default_paths_prefer_completed_chat_finetune -q`

Expected: FAIL because the GUI currently checks base first.

- [ ] **Step 2: Make the priority change and update README commands**

Put this first in `default_paths()`:

```python
PROJECT_ROOT / "checkpoints" / "chat-finetune" / "latest.pt",
```

In `README.md`, add an **Open conversation seed data** subsection before the existing fine-tuning instructions. State the three exact dataset identifiers and their licences, that all cache/output files remain on D:, and that `open_seed.jsonl` carries provenance while `approved.jsonl` remains user-owned. Add these commands exactly:

```powershell
cd D:\LLM
& .\.venv\Scripts\python.exe scripts\download_open_chat_data.py
& .\.venv\Scripts\python.exe -m mini_gpt.cli prepare-chat --input data/conversations/open_seed.jsonl --output-dir data/conversations/processed
& .\.venv\Scripts\python.exe -m mini_gpt.cli encode --text data/conversations/processed/train.txt --tokenizer data/tokenizer.json --output data/conversations/processed/train.bin
& .\.venv\Scripts\python.exe -m mini_gpt.cli encode --text data/conversations/processed/val.txt --tokenizer data/tokenizer.json --output data/conversations/processed/val.bin
& .\.venv\Scripts\python.exe -m mini_gpt.cli train --config configs/chat_finetune.json --processed-dir data/conversations/processed --tokenizer data/tokenizer.json --checkpoint-dir checkpoints/chat-finetune --init-checkpoint checkpoints/base/latest.pt
```

Replace the statement that the user must manually select the chat checkpoint with the fact that the GUI uses it automatically when it exists and still allows manual selection for rollback.

- [ ] **Step 3: Run GUI and documentation verification**

Run:

```powershell
cd D:\LLM
& .\.venv\Scripts\python.exe -m pytest tests/test_gui.py tests/test_chat_sources.py -q
& .\.venv\Scripts\python.exe -m compileall -q src scripts tests
git diff --check
```

Expected: all focused tests pass, compilation has no output, and `git diff --check` has no output.

- [ ] **Step 4: Commit integration and documentation**

Run:

```powershell
git add README.md src/mini_gpt/gui.py tests/test_gui.py
git diff --cached --check
git commit -m "feat: prefer Ru chat checkpoint"
```

Expected: a commit containing only GUI checkpoint preference, its test, and user documentation.

### Task 4: Generate, prepare, and fine-tune the initial conversation checkpoint

**Files:**
- Modify: `D:\LLM\TODO.md`
- Generated, ignored: `D:\LLM\data\conversations\open_seed.jsonl`
- Generated, ignored: `D:\LLM\data\conversations\open_seed_manifest.json`
- Generated, ignored: `D:\LLM\data\conversations\processed\*`
- Generated, ignored: `D:\LLM\checkpoints\chat-finetune\*`

- [ ] **Step 1: Verify the base artifacts and free disk before creating data**

Run:

```powershell
cd D:\LLM
Get-Item checkpoints\base\latest.pt, data\tokenizer.json | Select-Object FullName,Length,LastWriteTime
Get-PSDrive D | Select-Object Used,Free
```

Expected: the base checkpoint and tokenizer exist, and D: has enough headroom for a second approximately 407 MB checkpoint plus generated data.

- [ ] **Step 2: Download the bounded source corpus and inspect its manifest**

Run:

```powershell
& .\.venv\Scripts\python.exe scripts\download_open_chat_data.py
Get-Content -Raw data\conversations\open_seed_manifest.json
& .\.venv\Scripts\python.exe -c "import json; from pathlib import Path; rows=[json.loads(x) for x in Path('data/conversations/open_seed.jsonl').read_text(encoding='utf-8').splitlines()]; print(len(rows)); print(rows[0]); print(set(row['language'] for row in rows))"
```

Expected: 3,900 rows, each with prompt/response/language/source/licence; the manifest reports 2,000 UltraChat, 400 OASST2, and 1,500 Vietnamese-dataset records. Stop and diagnose instead of training if any quota, source, licence, or language is missing.

- [ ] **Step 3: Build the fine-tuning token files**

Run:

```powershell
& .\.venv\Scripts\python.exe -m mini_gpt.cli prepare-chat --input data/conversations/open_seed.jsonl --output-dir data/conversations/processed
& .\.venv\Scripts\python.exe -m mini_gpt.cli encode --text data/conversations/processed/train.txt --tokenizer data/tokenizer.json --output data/conversations/processed/train.bin
& .\.venv\Scripts\python.exe -m mini_gpt.cli encode --text data/conversations/processed/val.txt --tokenizer data/tokenizer.json --output data/conversations/processed/val.bin
Get-Item data\conversations\processed\train.bin, data\conversations\processed\val.bin | Select-Object Name,Length
```

Expected: a non-empty train and validation split and both `.bin` files larger than 512 tokens.

- [ ] **Step 4: Capture a reproducible base-chat baseline**

Run each command with CUDA and store the exact completions in the task record:

```powershell
& .\.venv\Scripts\python.exe -m mini_gpt.cli generate --checkpoint checkpoints/base/latest.pt --tokenizer data/tokenizer.json --prompt "User: Xin chào\nAssistant:" --max-new-tokens 60 --temperature 0.7 --top-k 20 --device cuda
& .\.venv\Scripts\python.exe -m mini_gpt.cli generate --checkpoint checkpoints/base/latest.pt --tokenizer data/tokenizer.json --prompt "User: Hello, how are you today?\nAssistant:" --max-new-tokens 60 --temperature 0.7 --top-k 20 --device cuda
```

Expected: baseline outputs may remain non-conversational; this is the comparison point rather than a quality gate.

- [ ] **Step 5: Run the chat fine-tuning phase from the immutable base checkpoint**

Run:

```powershell
& .\.venv\Scripts\python.exe -m mini_gpt.cli train --config configs/chat_finetune.json --processed-dir data/conversations/processed --tokenizer data/tokenizer.json --checkpoint-dir checkpoints/chat-finetune --init-checkpoint checkpoints/base/latest.pt
```

Expected: CUDA is selected, the local step starts at 1, checkpoints are written only under `checkpoints/chat-finetune`, and the run ends at local step 1,000. Do not use `--resume` on this first run. If interrupted, continue only with `--resume checkpoints/chat-finetune/latest.pt` and the same dataset/configuration.

- [ ] **Step 6: Compare completed chat checkpoint, run regression tests, and update roadmap**

Run:

```powershell
& .\.venv\Scripts\python.exe -m mini_gpt.cli generate --checkpoint checkpoints/chat-finetune/latest.pt --tokenizer data/tokenizer.json --prompt "User: Xin chào\nAssistant:" --max-new-tokens 60 --temperature 0.7 --top-k 20 --device cuda
& .\.venv\Scripts\python.exe -m mini_gpt.cli generate --checkpoint checkpoints/chat-finetune/latest.pt --tokenizer data/tokenizer.json --prompt "User: Hello, how are you today?\nAssistant:" --max-new-tokens 60 --temperature 0.7 --top-k 20 --device cuda
& .\.venv\Scripts\python.exe -m pytest -q
git diff --check
```

Expected: both post-fine-tune completions begin as assistant turns without immediately continuing `User:` or producing empty text; all tests pass. Inspect `README.md`, `TODO.md`, and `docs/` for stale checkpoint-selection or command text. Only then mark the open seed-data and initial chat-fine-tune items complete in `TODO.md`; retain the separate user-approved-data fine-tune item as pending.

- [ ] **Step 7: Commit only the final roadmap status**

Run:

```powershell
git add TODO.md
git diff --cached --check
git commit -m "docs: record Ru initial chat fine-tune"
git status --short
```

Expected: generated source data, processed bins, Hugging Face cache, and model checkpoints remain ignored; the working tree is clean after the roadmap-status commit.

## Self-review

- **Spec coverage:** Task 1 provides source-specific extraction, natural-conversation filters, and exact deduplication. Task 2 provides bounded streaming, source/licence provenance, quota failures, manifest data, and atomic outputs. Task 3 changes GUI priority and user documentation. Task 4 runs the documented preprocessing/fine-tune flow, compares Vietnamese-English baselines, verifies tests, and updates the roadmap only after real success.
- **Placeholder scan:** no `TBD`, `TODO`, deferred implementation text, or undefined error-handling steps remain in this plan.
- **Type consistency:** `ChatSeedRecord`, `quality_rejection_reason`, and `download_open_chat_seed` have one spelling and signature throughout. All generated records retain the `prompt`, `response`, `language`, `source`, and `license` fields accepted by the existing `prepare-chat` reader.
