# Ru v6.1 Natural Style Adapter Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` (or execute the same tasks inline when that sub-skill is unavailable) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build, train, evaluate, and hand off a non-default Ru v6.1 adapter that improves natural conversation while preserving v6's `anh`/`em` voice.

**Architecture:** Author a fresh 120-row style corpus, validate it with deterministic split and voice gates, then continue the existing v6 PEFT adapter with a conservative QLoRA profile. Evaluate v6.1 against v6 on the shared conversation suite plus the exact multi-turn identity-repair transcript before any GUI promotion.

**Tech Stack:** Python 3.11, PyTorch/CUDA, Transformers, PEFT, bitsandbytes, JSONL, pytest, existing Tkinter GUI.

---

## File map

| File | Responsibility |
| --- | --- |
| `src/mini_gpt/ru_v6_1_dialogues.py` | Authored source records and category constants for the new style corpus. |
| `src/mini_gpt/ru_v6_1_corpus.py` | Schema validation, quality gates, deterministic split, manifest, and atomic JSONL writes. |
| `scripts/build_ru_llama_v6_1_data.py` | CLI for building or auditing the corpus. |
| `configs/ru_llama3_1_8b_v6_1.json` | Conservative continuation-training profile. |
| `src/mini_gpt/llama3_1_training.py` | v6.1 source/output path guards and existing QLoRA training implementation. |
| `scripts/train_ru_llama3_1_v6_1.py` | Safe CLI wrapper that cannot overwrite v6 or a non-empty v6.1 directory. |
| `src/mini_gpt/ru_v6_1_evaluation.py` | Fixed style and screenshot regression cases plus response assessment. |
| `scripts/evaluate_ru_llama3_1_v6_1.py` | Greedy evaluation, VRAM measurement, and JSON result artifact. |
| `tests/test_ru_v6_1_corpus.py` | Corpus shape, voice, determinism, and write-safety tests. |
| `tests/test_llama3_1_v6_1_config.py` | Config and training-guard tests. |
| `tests/test_ru_v6_1_evaluation.py` | Regression suite shape and identity-assessment tests. |
| `README.md` | Final evidence and adoption status only after evaluation. |
| `docs/ANTIGRAVITY_HANDOFF.md` | Concise handoff of v6.1 results, gates, and rollback status. |

The generated dataset lives under `data/conversations/ru_v6_1`; the trained candidate lives under `checkpoints/ru-llama3.1-8b-instruct-v6.1`. Neither path may overwrite v6.

## Task 1: Lock the corpus contract with failing tests

**Files:**

- Create: `tests/test_ru_v6_1_corpus.py`
- Create later: `src/mini_gpt/ru_v6_1_corpus.py`, `src/mini_gpt/ru_v6_1_dialogues.py`

- [x] **Step 1: Add the corpus contract tests.**

Test these exact invariants:

```python
def test_v6_1_corpus_has_120_rows_and_required_shape(tmp_path):
    report = build_ru_v6_1_corpus(tmp_path)
    assert report["total_examples"] == 120
    assert report["split_counts"] == {"train": 84, "validation": 18, "held_out": 18}
    assert report["language_counts"] == {"vi": 60, "mixed": 36, "en": 24}
    assert report["contextual_examples"] >= 48
    assert report["identity_correction_examples"] == 18
    assert report["light_flirt_examples"] <= 18
```

Add tests that every row starts with `RU_SYSTEM_PROMPT`, has alternating `user`/`assistant` turns ending in `user`, contains the exact project-owned source/license values, and has no Vietnamese assistant self-reference matching `tớ`, `tôi`, or `mình`. Add a determinism test that calls the builder twice with seed 42 and compares reports plus file bytes, and an audit test that calls `build_ru_v6_1_corpus(output_dir, write=False)` and asserts that `output_dir` is not created.

- [x] **Step 2: Run the focused tests and verify the contract is red.**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests/test_ru_v6_1_corpus.py -q
```

Expected result: collection fails because the v6.1 corpus module does not exist yet. Do not weaken the assertions to make the test pass.

## Task 2: Author and validate the fresh style corpus

**Files:**

- Create: `src/mini_gpt/ru_v6_1_dialogues.py`
- Create: `src/mini_gpt/ru_v6_1_corpus.py`
- Create: `scripts/build_ru_llama_v6_1_data.py`
- Modify: `tests/test_ru_v6_1_corpus.py`

- [x] **Step 1: Author 120 records with the approved distribution.**

Use a `_record(language, mode, turns, response, flirt_level="none")` helper that always inserts `RU_SYSTEM_PROMPT` as the first message and returns the fields `messages`, `response`, `language`, `mode`, `source`, `license`, and `flirt_level`.

Author exactly 60 Vietnamese, 36 mixed, and 24 English records. Include 48 or more multi-turn records, exactly 18 identity/correction records, and no more than 18 `flirt_level="light"` records. The identity cases must include the four-turn screenshot path and variants where the user says the wrong self-reference, asks for a correction, or asks what Ru meant. Every target must use `em`/`anh` in Vietnamese and must not use `tớ`, `tôi`, or `mình` as Ru's self-reference.

Use the following modes and counts so the distribution is deterministic: `acknowledge=24`, `follow_up=30`, `everyday_help=20`, `emotion=16`, `light_humour=12`, and `identity_repair=18`. Keep ordinary responses to one or two natural sentences unless the context needs a short third sentence. Put light flirt only where the prompt supports it; do not use graphic, violent, possessive, coercive, or stage-direction targets in this style corpus.

- [x] **Step 2: Implement corpus validation and splitting.**

Implement `build_ru_v6_1_corpus(output_dir, *, seed=42, write=True)` in `src/mini_gpt/ru_v6_1_corpus.py`. It must:

1. Normalize every text field with `normalize_text`.
2. Require the exact approved schema and project-owned provenance.
3. Validate role alternation and the exact system prompt.
4. Reject duplicate prompt/response pairs and duplicate final prompts across train, validation, and held-out splits.
5. Reject forbidden Vietnamese self-reference and hard quality failures using the existing `casual_rejection_reason` helper plus explicit style checks.
6. Shuffle with `Random(42)` and write 84/18/18 rows to `train.jsonl`, `val.jsonl`, and `held_out.jsonl`.
7. Write `manifest.json` with counts, mode/language counts, contextual and correction counts, source hash, selected-row hash, and seed.
8. Write `.partial` files and replace them atomically; remove partial files if a write fails.

Expose constants for `EXPECTED_TOTAL`, `EXPECTED_SPLIT_COUNTS`, `EXPECTED_LANGUAGE_COUNTS`, and `EXPECTED_IDENTITY_CORRECTION_EXAMPLES` so tests and the CLI share the same contract.

- [x] **Step 3: Add the builder CLI.**

Implement `scripts/build_ru_llama_v6_1_data.py` with `--output-dir`, `--seed`, and `--audit` arguments. Its default output is `D:/LLM/data/conversations/ru_v6_1`. The command prints the manifest as UTF-8 JSON and never writes when `--audit` is supplied.

- [x] **Step 4: Run corpus tests and the audit.**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests/test_ru_v6_1_corpus.py -q
& .\.venv\Scripts\python.exe scripts/build_ru_llama_v6_1_data.py --audit
& .\.venv\Scripts\python.exe scripts/build_ru_llama_v6_1_data.py
```

Expected result: all focused tests pass; the audit reports 120 examples without creating output; the build writes the three JSONL splits and a manifest with the approved counts.

## Task 3: Add safe v6.1 training configuration and guards

**Files:**

- Create: `configs/ru_llama3_1_8b_v6_1.json`
- Create: `tests/test_llama3_1_v6_1_config.py`
- Modify: `src/mini_gpt/llama3_1_training.py`
- Create: `scripts/train_ru_llama3_1_v6_1.py`

- [x] **Step 1: Write failing config and guard tests.**

Assert that the config uses:

```json
{
  "source_adapter_dir": "D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v6",
  "dataset_dir": "D:/LLM/data/conversations/ru_v6_1",
  "adapter_dir": "D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v6.1",
  "max_length": 512,
  "epochs": 3,
  "learning_rate": 0.000005,
  "gradient_accumulation_steps": 16,
  "physical_vram_cap_mib": 6144,
  "profile_name": "ru_llama3_1_v6_1_from_v6"
}
```

Test that `validate_v6_1_adapter_dir` accepts only `ru-llama3.1-8b-instruct-v6.1` and `ru-llama3.1-8b-instruct-v6.1-smoke` directly below the checkpoint root, rejects v6/v7/v8 and outside paths, and that `validate_v6_1_source_adapter_dir` accepts only the v6 adapter. Test that an output equal to the source is rejected.

- [x] **Step 2: Run the focused tests and verify they fail.**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests/test_llama3_1_v6_1_config.py -q
```

Expected result: collection or assertions fail because the v6.1 config and guards do not exist yet.

- [x] **Step 3: Implement the guards and config.**

Add v6.1-specific constants and validators next to the existing v7.1/v8 guards without changing their accepted paths. The validators must resolve paths, enforce the checkpoint root, and raise messages containing `v6.1 output` or `v6.1 source` respectively. Add the exact JSON config above plus the existing LoRA target modules and backend `llama3.1-qlora-style-continuation`.

- [x] **Step 4: Implement the safe training wrapper.**

Follow `scripts/train_ru_llama3_1_v8.py` but call the v6.1 validators, default to the v6.1 config, reject a non-empty output directory, reject output equal to the v6 source, replace resolved source/output paths in `LlamaTrainingConfig`, and print the JSON manifest. The wrapper must accept `--output-adapter-dir` and `--max-steps` for smoke runs.

- [x] **Step 5: Run the config and guard tests.**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests/test_llama3_1_v6_1_config.py tests/test_llama3_1_training_guards.py -q
```

Expected result: all config/guard tests pass and the existing v8 guard behavior remains unchanged.

## Task 4: Build the dataset and run a bounded training smoke

**Files:**

- Use: `data/conversations/ru_v6_1/`
- Use: `checkpoints/ru-llama3.1-8b-instruct-v6.1-smoke/`
- Use: `checkpoints/ru-llama3.1-8b-instruct-v6.1/` only for the full run

- [x] **Step 1: Verify the source and output paths before training.**

Run:

```powershell
Test-Path D:\LLM\checkpoints\ru-llama3.1-8b-instruct-v6
Test-Path D:\LLM\models\llama3.1-8b-instruct-hf
Test-Path D:\LLM\data\conversations\ru_v6_1\train.jsonl
```

Expected result: all commands return `True`. If the smoke output directory is non-empty, stop rather than deleting it.

- [x] **Step 2: Run a two-update smoke with the physical cap.**

The retained smoke artifact was created before the final screenshot-row edit;
the full run below used the final dataset hash. The smoke still validated the
QLoRA load/save path and the physical cap.

Run:

```powershell
& .\.venv\Scripts\python.exe scripts/train_ru_llama3_1_v6_1.py `
  --output-adapter-dir checkpoints/ru-llama3.1-8b-instruct-v6.1-smoke `
  --max-steps 2
```

Expected result: the v6 source loads, trainable LoRA parameters are present, a manifest is written, and the physical peak is at or below 6,144 MiB. Keep the smoke artifact for inspection; do not replace v6.

- [x] **Step 3: Run the full three-epoch training job.**

After the smoke passes and the destination is confirmed absent or empty, run:

```powershell
& .\.venv\Scripts\python.exe scripts/train_ru_llama3_1_v6_1.py `
  --config configs/ru_llama3_1_8b_v6_1.json
```

Expected result: `checkpoints/ru-llama3.1-8b-instruct-v6.1/ru_manifest.json` records the v6 source, dataset hash, validation-best loss, optimizer updates, and a physical peak no greater than 6,144 MiB.

## Task 5: Add evaluation and the screenshot regression gate

**Files:**

- Create: `src/mini_gpt/ru_v6_1_evaluation.py`
- Create: `scripts/evaluate_ru_llama3_1_v6_1.py`
- Create: `tests/test_ru_v6_1_evaluation.py`

- [x] **Step 1: Write tests for the fixed evaluation suite.**

Assert that the suite contains the existing 24 common conversation cases plus a four-turn identity case with these user prompts in order: `chào em`, `phải xưng là em chứ`, `sao em vẫn xưng là tôi vậy.`, and `nói cái gì?`. Assert that all cases use `RU_SYSTEM_PROMPT`, that identity cases mark `identity_expected=True`, and that the assessor fails responses containing a Vietnamese self-reference `tớ`, `tôi`, or `mình`.

- [x] **Step 2: Run the evaluation tests and verify they fail.**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests/test_ru_v6_1_evaluation.py -q
```

Expected result: collection fails until the suite module exists.

- [x] **Step 3: Implement deterministic assessments.**

Create immutable `ConversationCase` values and an `assess_v6_1_response` function. Reuse the existing relevance/follow-up assessment where applicable, then add explicit identity checks for Vietnamese output. Keep automatic checks deterministic and leave naturalness/style scoring to the manual review artifact.

- [x] **Step 4: Implement the evaluation CLI.**

Evaluate the v6.1 adapter with `do_sample=False`, `max_new_tokens=64`, and the existing `PhysicalVramMonitor`. Clear the generation cache in `finally`, write an atomic JSON result containing every prompt, response, assessment, peak memory, physical cap status, and `verdict: "not_reviewed"`, then return a non-zero exit code on automatic or VRAM failure.

- [x] **Step 5: Run v6 and v6.1 with identical settings.**

Run the evaluator for v6 first into a comparison file, then v6.1 into its own file:

```powershell
& .\.venv\Scripts\python.exe scripts/evaluate_ru_llama3_1_v6_1.py `
  --adapter-dir checkpoints/ru-llama3.1-8b-instruct-v6 `
  --output eval_results/ru_llama3_1_v6_style.json

& .\.venv\Scripts\python.exe scripts/evaluate_ru_llama3_1_v6_1.py `
  --adapter-dir checkpoints/ru-llama3.1-8b-instruct-v6.1 `
  --output eval_results/ru_llama3_1_v6_1_style.json
```

Actual result: both artifacts contain the same case IDs and generation settings; both physical caps passed, but both failed the automatic conversation gate and the first screenshot replay turn.

## Task 6: Manual review, GUI smoke, and documentation

**Files:**

- Create: `checkpoints/ru-llama3.1-8b-instruct-v6.1/manual_review_v6_1.json`
- Update after evidence: `README.md`, `docs/ANTIGRAVITY_HANDOFF.md`

- [x] **Step 1: Review the fixed cases against v6.**

The automatic gate failed, so manual naturalness scoring was skipped. The gate
decision records the failed case IDs, screenshot turn, and v6 comparison.

For each common-suite and identity case, record naturalness, relevance, language, voice, role consistency, and severe failure. Mark the candidate `not_adopted` if any identity failure remains, if ordinary naturalness is not better than v6, or if a severe output appears.

- [x] **Step 2: Launch the candidate through the existing Tkinter GUI without changing its default.**

Candidate GUI smoke was intentionally skipped after the hard automatic failure;
the GUI default remained v6 and no promotion path was opened.

Use a one-off `pythonw.exe` launch that sets the checkpoint field to `D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v6.1`, or use the GUI Browse button. Verify the status line names `ru-llama3.1-8b-instruct-v6.1`; verify that a fresh normal conversation and the screenshot sequence do not silently use v6.

- [x] **Step 3: Update the project documents with actual results.**

Add only the v6.1 facts that occurred: adapter path, source v6, dataset/manifest hash, training metrics, VRAM peak, automatic result, manual verdict, and whether GUI default remains v6. Do not claim adoption if any gate failed. Remove no historical document solely because it describes an earlier candidate.

## Task 7: Final verification and handoff

- [x] **Step 1: Run the focused regression suite.**

```powershell
& .\.venv\Scripts\python.exe -m pytest `
  tests/test_ru_v6_1_corpus.py `
  tests/test_llama3_1_v6_1_config.py `
  tests/test_ru_v6_1_evaluation.py `
  tests/test_llama3_1_training_guards.py -q
```

Expected result: all focused tests pass.

- [x] **Step 2: Run the full test suite and source checks.**

```powershell
& .\.venv\Scripts\python.exe -m pytest -q
git diff --check
```

Expected result: the full suite passes and `git diff --check` reports no whitespace errors. Existing unrelated dirty files remain untouched.

- [x] **Step 3: Verify adoption state and rollback.**

Confirm that `src/mini_gpt/gui.py` still lists v6 first, that v6 files and adapter hashes are unchanged, and that the final handoff states either `adopted` with explicit evidence or `not_adopted` with v6 retained as the active baseline.

- [x] **Step 4: Commit only the v6.1 implementation/docs surface.**

Stage the new v6.1 modules, tests, config, scripts, result metadata, and intentional README/handoff changes by exact path. Do not stage the pre-existing unrelated modifications shown by `git status`. Commit with:

```powershell
git add -- `
  src/mini_gpt/ru_v6_1_dialogues.py `
  src/mini_gpt/ru_v6_1_corpus.py `
  src/mini_gpt/ru_v6_1_evaluation.py `
  src/mini_gpt/llama3_1_training.py `
  scripts/build_ru_llama_v6_1_data.py `
  scripts/train_ru_llama3_1_v6_1.py `
  scripts/evaluate_ru_llama3_1_v6_1.py `
  configs/ru_llama3_1_8b_v6_1.json `
  tests/test_ru_v6_1_corpus.py `
  tests/test_llama3_1_v6_1_config.py `
  tests/test_ru_v6_1_evaluation.py `
  README.md `
  docs/ANTIGRAVITY_HANDOFF.md
git commit -m "feat: add Ru v6.1 natural style candidate"
```

## Self-review checklist

- The design's 120-row corpus, language split, contextual minimum, identity count, flirt cap, and deterministic output are covered by Tasks 1 and 2.
- v6 immutability, v6-only source validation, v6.1 output isolation, fresh optimizer, and VRAM cap are covered by Tasks 3 and 4.
- The exact four-turn screenshot sequence, greedy evaluation, automatic gate decision, GUI-default preservation, and rollback state are covered by Tasks 5 and 6; candidate GUI sampling was skipped after the hard automatic failure.
- Full tests, document checks, rollback verification, and exact-path staging are covered by Task 7.
- No incomplete markers or unresolved file names are used in the plan.
