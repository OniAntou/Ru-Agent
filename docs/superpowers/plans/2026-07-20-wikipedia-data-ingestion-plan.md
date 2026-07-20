# Wikipedia Data Ingestion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a D:-resident, license-aware streaming downloader for small Vietnamese and English Wikipedia samples and prepare a larger base-model configuration without downloading multi-terabyte corpora.

**Architecture:** A lazy `datasets` import keeps the core training package usable without the downloader dependency. The downloader streams one Wikimedia language subset at a time, writes `VI:`/`EN:` text blocks accepted by the existing corpus cleaner, and records source/configuration/count metadata in a manifest.

**Tech Stack:** Python 3.11, Hugging Face `datasets`, Wikimedia Wikipedia dataset, JSONL/UTF-8 text, existing mini-GPT preparation pipeline.

---

### Task 1: Add the streaming data-source module

**Files:**
- Modify: `D:\LLM\pyproject.toml`
- Create: `D:\LLM\src\mini_gpt\data_sources.py`
- Create: `D:\LLM\tests\test_data_sources.py`

- [x] Add `datasets>=3.0` to the project dependencies while importing it only inside the download function.
- [x] Implement `normalize_wikipedia_record(record, language, min_characters, max_characters)` to return a cleaned `(language, title, text)` tuple or `None`; reject missing text, short text, and oversized records.
- [x] Implement `download_wikipedia_sample(output_dir, languages, snapshot, max_documents_per_language, min_characters, max_characters, cache_dir)` using `load_dataset("wikimedia/wikipedia", f"{snapshot}.{language}", split="train", streaming=True)`.
- [x] Set `HF_HOME`, `HF_DATASETS_CACHE`, and `HF_HUB_CACHE` to the supplied D: cache directory before importing `datasets`.
- [x] Write `wikipedia_<language>.txt` with a language prefix and blank-line document boundaries, and write `wikipedia_manifest.json` containing dataset name, snapshot, requested/accepted counts, character counts, cache path, and license references.
- [x] Test record filtering and output formatting without network access by using a fake iterable and temporary output directory.

### Task 2: Add the downloader CLI

**Files:**
- Create: `D:\LLM\scripts\download_wikipedia.py`
- Modify: `D:\LLM\README.md`
- Test: `D:\LLM\tests\test_cli.py`

- [x] Add arguments for output directory, languages, snapshot, max documents, minimum/maximum characters, and cache directory; default to 10,000 documents per language and all artifacts on `D:\LLM`.
- [x] Make `python scripts\download_wikipedia.py --help` work from any current directory by resolving the repository root from the script location.
- [x] Add README commands that install `datasets` in the existing D: virtual environment and run the downloader with streaming and a D: cache.
- [x] Add a CLI help test and document that the downloader does not grant rights to Common Crawl-derived sources; this command uses Wikimedia's own dataset and license information.

### Task 3: Add a base training configuration

**Files:**
- Create: `D:\LLM\configs\base.json`
- Modify: `D:\LLM\README.md`

- [x] Add the designed 8-layer, width-512, 8-head, feed-forward-2048 model with context length 512 and tokenizer-driven vocabulary size.
- [x] Set a conservative micro-batch of one, gradient accumulation of 32, mixed precision, warmup, cosine decay, periodic validation, and checkpointing; keep the total step count explicit so a long run is never started accidentally.
- [x] Document that the base configuration needs a larger corpus and that the existing tiny configuration remains the short pipeline check.

### Task 4: Download and prepare the first real sample

**Files:** generated under `D:\LLM\data\raw`, `D:\LLM\.hf`, and `D:\LLM\data\processed`

- [x] Install `datasets` with temporary/cache locations on D:.
- [x] Stream 10,000 English and 10,000 Vietnamese Wikipedia records, rejecting documents shorter than 200 characters and longer than 100,000 characters.
- [x] Verify the manifest contains both languages, the raw files are UTF-8, and D: free space remains sufficient for processing/checkpoints.
- [x] Run `prepare`, tokenizer training at vocabulary size 16,384, and train/validation encoding.

### Task 5: Verify and update documents

**Files:** `README.md`, `docs/superpowers/plans/2026-07-20-wikipedia-data-ingestion-plan.md`

- [x] Run the complete test suite and the downloader help command.
- [x] Inspect every project document for stale setup commands or inaccurate paths.
- [x] Run `git diff --check`, commit code/documentation, and report the downloaded sample size and any quality limitation honestly.

## Self-review

- The downloader is streaming and bounded by per-language document counts, so it cannot accidentally request the entire Wikipedia dataset into RAM.
- License and source metadata are recorded with the data; the script does not claim that arbitrary web text is safe to use.
- The existing corpus cleaner accepts the generated `VI:`/`EN:` blocks without a second data format.
- The base model configuration is present but is not launched automatically after download.
