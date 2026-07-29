# v6.2 GGUF Default and v6.3 Dataset Implementation Plan

> **Historical plan:** the v6.3 suffix requirement below was superseded by the
> 2026-07-29 Ru personality/cloud consistency update. The active contract uses
> bounded contextual profanity metadata and does not force `Nya~`.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make the verified v6.2 LoRA GGUF the GUI's preferred local adapter and prepare a new bounded v6.3 targeted-improvement dataset without starting training.

**Architecture:** The GGUF runtime will select v6.2 first and fall back to v6.1/v6 only when the preferred file is missing. The v6.3 corpus will be a project-owned authored set with explicit repair, mixed-language, and context strata, deterministic splits, safety gates, and a training config capped at the existing 9,600-second wall-clock limit.

**Tech Stack:** Python 3.13, llama.cpp GGUF, JSONL, pytest, existing corpus helpers.

---

### Task 1: Prefer the v6.2 GGUF in the local runtime

**Files:**
- Modify: `src/mini_gpt/llama_cpp_runtime.py`
- Modify: `src/mini_gpt/gui.py`
- Test: `tests/test_llama_cpp_runtime.py`
- Test: `tests/test_gui.py`

- [x] Add v6.2-first adapter path selection and fallback labels.
- [x] Update GUI default checkpoint candidates to v6.2, v6.1, v6.
- [x] Update focused tests for the new ordering and command path.
- [x] Run the focused runtime/GUI tests.

### Task 2: Author and validate the v6.3 targeted dataset

**Files:**
- Create: `src/mini_gpt/ru_v6_3_dialogues.py`
- Create: `src/mini_gpt/ru_v6_3_corpus.py`
- Create: `scripts/build_ru_llama_v6_3_data.py`
- Create: `configs/ru_llama3_1_v6_3.json`
- Test: `tests/test_ru_v6_3_dialogues.py`
- Test: `tests/test_ru_v6_3_corpus.py`

- [x] Create 180 project-owned rows across repair, mixed bilingual, and context strata.
- [x] Enforce role alternation, `anh`/`em` Vietnamese voice, no VTS markers, no explicit/unsafe/meta content, unique final prompts, and the historical `Nya~` suffix requirement (superseded for the active v6.3 rebuild).
- [x] Split deterministically into train/validation/held-out and write a manifest with hashes and counts.
- [x] Bound training to 360 steps and 9,600 seconds using the existing v6.2 QLoRA profile.
- [x] Build the dataset and run focused/full tests.

### Task 3: Update active documentation and verify scope

**Files:**
- Modify: `README.md`
- Modify: `TODO.md`
- Modify: `docs/ANTIGRAVITY_HANDOFF.md`

- [x] Record v6.2 GGUF path/hash and v6.2-first fallback behavior.
- [x] Record v6.3 dataset manifest/hash and explicitly state that training has not run.
- [x] Run `git diff --check`, focused tests, and full pytest; preserve unrelated dirty files.
