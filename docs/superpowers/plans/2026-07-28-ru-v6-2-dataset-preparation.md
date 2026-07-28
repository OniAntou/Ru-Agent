# Ru v6.2 Dataset Preparation Implementation Plan (historical)

> Superseded for the current candidate by
> `2026-07-28-ru-v6-2-fresh-memory-approved.md`. This plan describes the old
> restored `ru_v6_2` corpus and must not be used to overwrite the fresh
> `ru_v6_2_fresh` dataset.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore the previously verified v6.2 authored-corpus pipeline, regenerate its dataset, and verify that the candidate is dataset-ready without starting training or changing the GUI fallback.

**Architecture:** Reuse the exact implementation from commit `1006017`, which authored v6.2 independently from v6 (not from v6.1). The builder writes train/validation/held-out JSONL atomically and enforces language, mode, intensity, long-context, safety, alternating-history, and split-separation gates.

**Tech Stack:** Python 3.11/3.13, standard-library JSON/hashlib/pathlib, pytest, existing `mini_gpt` corpus helpers.

---

### Task 1: Restore the verified v6.2 pipeline

**Files:**
- Restore: `src/mini_gpt/ru_v6_2_dialogues.py`
- Restore: `src/mini_gpt/ru_v6_2_corpus.py`
- Restore: `scripts/build_ru_llama_v6_2_data.py`
- Restore: `tests/test_ru_v6_2_dialogues.py`
- Restore: `tests/test_ru_v6_2_corpus.py`

- [x] **Step 1: Restore only the known v6.2 implementation paths**

Use commit `1006017` as the source for these five paths. Do not restore unrelated files or overwrite the existing dirty worktree.

- [x] **Step 2: Run the focused v6.2 tests**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_ru_v6_2_dialogues.py tests\test_ru_v6_2_corpus.py -q`

Expected: all v6.2 tests pass.

### Task 2: Rebuild and audit the dataset

**Files:**
- Generate: `data/conversations/ru_v6_2/train.jsonl`
- Generate: `data/conversations/ru_v6_2/val.jsonl`
- Generate: `data/conversations/ru_v6_2/held_out.jsonl`
- Generate: `data/conversations/ru_v6_2/manifest.json`

- [x] **Step 1: Run the audit-only command**

Run: `& D:\LLM\.venv\Scripts\python.exe scripts\build_ru_llama_v6_2_data.py --audit`

Expected: 2,400 authored rows, split shape `1680/360/360`, languages `vi=1200`, `mixed=720`, `en=480`, and all safety/context gates pass.

- [x] **Step 2: Write the deterministic dataset**

Run: `& D:\LLM\.venv\Scripts\python.exe scripts\build_ru_llama_v6_2_data.py`

Expected: the four dataset files are written under `data\conversations\ru_v6_2`; no checkpoint or training process is created.

- [x] **Step 3: Verify generated counts and provenance**

Run a read-only JSON check that train/validation/held-out contain `1680/360/360` rows, all rows have the expected system prompt and alternating roles, and the manifest reports `dataset prepared` semantics rather than training or adoption.

### Task 3: Check documentation and leave the adoption boundary explicit

**Files:**
- Review: `README.md`, `TODO.md`, `docs/ANTIGRAVITY_HANDOFF.md`

- [x] **Step 1: Search for stale v6.2 claims**

Run: `rg -n -i "v6\.2|dataset prepared|training not run|GUI remains v6" README.md TODO.md docs`

- [x] **Step 2: Preserve unrelated dirty documentation changes**

If the current documents already contain uncommitted edits, do not overwrite them. Report any stale v6.1/v6.2 status that needs a follow-up documentation patch separately from the dataset result.

- [x] **Step 3: Confirm no training or promotion occurred**

Check that no v6.2 adapter directory or training process was created and that the GUI fallback remains v6.
