# Ru V5 Local 3B Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Evaluate a completely local Qwen2.5-3B-Instruct baseline for Ru and only permit later fine-tuning if its untrained conversational quality passes the approved gate.

**Architecture:** Register the 3B model as a local Qwen runtime profile and give it a reproducible downloader. Reuse the fixed 32-case evaluator for an adapter-free baseline, add a durable human-review decision record, and stop before dataset creation or training unless the base passes both automatic and manual gates.

**Tech Stack:** Python 3.11, PyTorch, Transformers, BitsAndBytes 4-bit NF4, pytest, Hugging Face Hub, Windows PowerShell.

---

## Scope guardrails

- [ ] V5 is fully local: models live under the project directory and inference/training never call hosted model APIs.
- [ ] Phase 1 is baseline only. Do not create a V5 corpus, adapter, training launcher, or GUI wiring while the base decision is unresolved.
- [ ] Stop on CUDA OOM or peak allocated VRAM above 5,500 MiB.
- [ ] Preserve V2/V3/V4 artifacts and leave the GUI unchanged.
- [ ] Pass requires no automatic disqualifier and at least 24/32 manually natural, relevant, language-appropriate, anh/em-consistent responses.
- [ ] Keep at least 5 GB free on D: after downloading the model.

## Task 1: Register the V5 3B local base and downloader

**Files:**

- Modify: src/mini_gpt/qwen_runtime.py
- Create: scripts/download_ru_qwen_v5_base.py
- Modify: tests/test_qwen_runtime.py

- [x] **Step 1: Write failing profile tests.**

Add coverage beside the V3/V4 profile tests. It must resolve V5 by name and assert these immutable values:

~~~python
assert profile.model_id == "Qwen/Qwen2.5-3B-Instruct"
assert profile.local_path == Path("models/qwen2.5-3b-instruct")
assert profile.label == "Ru V5 Qwen 3B"
~~~

Add a downloader-module test which imports the script without downloading and confirms it targets the V5 profile, not a copied old model id.

- [x] **Step 2: Run focused tests and confirm failure.**

~~~powershell
python -m pytest tests/test_qwen_runtime.py -q
~~~

Expected: the new profile test fails because the V5 profile is absent.

- [x] **Step 3: Register the immutable profile.**

Define the V5 profile next to V3/V4:

~~~python
QWEN_V5_BASE = QwenBaseProfile(
    model_id="Qwen/Qwen2.5-3B-Instruct",
    local_path=Path("models/qwen2.5-3b-instruct"),
    label="Ru V5 Qwen 3B",
)
~~~

Register it in the profile mapping. Do not alter the current GUI or V4 defaults.

- [x] **Step 4: Add the explicit downloader.**

Copy the V4 downloader structure, but import and use the V5 profile. It must resolve the repository root, download to models/qwen2.5-3b-instruct, print the absolute target, model id, and total safetensors bytes, and exit non-zero if config/tokenizer/weight files are missing. No CLI override may substitute another repository or destination.

- [x] **Step 5: Verify and commit.**

~~~powershell
python -m pytest tests/test_qwen_runtime.py -q
git add src/mini_gpt/qwen_runtime.py scripts/download_ru_qwen_v5_base.py tests/test_qwen_runtime.py
git diff --cached --check
git commit -m "feat: register Ru Qwen V5 base"
~~~

Expected: focused tests pass.

## Task 2: Make the base-only evaluation and human decision reproducible

**Files:**

- Create: configs/ru_qwen_v5_3b.json
- Modify: scripts/evaluate_ru_qwen_v4.py
- Modify: src/mini_gpt/ru_qwen_evaluation.py
- Modify: tests/test_ru_qwen_evaluation.py
- Create: tests/test_evaluate_ru_qwen_script.py

- [x] **Step 1: Write failing tests for a baseline decision record.**

Add a pure decision-record function test. Given an evaluated result and all human reviews, it must emit JSON-safe output including this shape:

~~~python
{
  "profile_name": "ru_qwen_v5_3b",
  "phase": "base_only",
  "automatic_disqualifier_count": 0,
  "manual_natural_count": 24,
  "manual_required_count": 24,
  "verdict": "passed",
  "reviewer_notes": "..."
}
~~~

Add negative tests: any automatic disqualifier or fewer than 24 manual-natural cases produces not_adopted; a review list that does not cover all 32 cases raises a clear validation error. Add a script-level test that the V5 config runs an adapter-free evaluation.

- [x] **Step 2: Run focused tests and confirm failure.**

~~~powershell
python -m pytest tests/test_ru_qwen_evaluation.py tests/test_evaluate_ru_qwen_script.py -q
~~~

- [x] **Step 3: Add the baseline-safe configuration.**

Create configs/ru_qwen_v5_3b.json:

~~~json
{
  "profile_name": "ru_qwen_v5_3b",
  "base_model_id": "Qwen/Qwen2.5-3B-Instruct",
  "base_model_path": "models/qwen2.5-3b-instruct",
  "phase": "base_only"
}
~~~

Do not add dataset, LoRA, optimizer, epochs, or adapter fields. Their absence prevents accidental training during the gate.

- [x] **Step 4: Implement the decision record.**

Keep the fixed 32 prompts and existing automatic guards. Add a pure function that requires exactly one review per evaluated case, aggregates automatic flags, requires at least 24 manual-natural cases, and emits passed only when both conditions hold. Otherwise it must emit not_adopted plus machine-readable failure reasons.

Each review item must contain:

~~~json
{
  "id": "vi_greeting_01",
  "natural": true,
  "relevant": true,
  "expected_language": true,
  "voice": true,
  "notes": "..."
}
~~~

Count a response as manual-natural only when all four booleans are true. Do not add a brittle language detector; explicit human review decides expected language. Update the evaluator CLI to consume profile_name and phase from config and write the full baseline report to the requested output while retaining V4 compatibility.

- [x] **Step 5: Verify and commit.**

~~~powershell
python -m pytest tests/test_ru_qwen_evaluation.py tests/test_evaluate_ru_qwen_script.py -q
git add configs/ru_qwen_v5_3b.json scripts/evaluate_ru_qwen_v4.py src/mini_gpt/ru_qwen_evaluation.py tests/test_ru_qwen_evaluation.py tests/test_evaluate_ru_qwen_script.py
git diff --cached --check
git commit -m "feat: add Ru V5 baseline decision gate"
~~~

Expected: focused tests pass without V4 regressions.

## Task 3: Download, preflight, and measure the untrained V5 base

**Runtime artifacts:**

- Create: models/qwen2.5-3b-instruct (ignored)
- Create: checkpoints/ru-qwen-v5/base_evaluation.json (ignored)
- Create: checkpoints/ru-qwen-v5/base_review.json (ignored)

- [x] **Step 1: Preflight environment.**

~~~powershell
Get-PSDrive -Name D
python -c "import torch; print(torch.cuda.is_available()); print(torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'no CUDA')"
git status --short
~~~

Record free D: space and GPU identity. Stop if CUDA is unavailable, unexpected overlapping edits exist, or the 5 GB post-download requirement cannot be met.

- [x] **Step 2: Download and verify model.**

~~~powershell
python scripts/download_ru_qwen_v5_base.py
Get-PSDrive -Name D
Get-ChildItem models\qwen2.5-3b-instruct -File | Select-Object Name,Length
python -c "from pathlib import Path; p=Path('models/qwen2.5-3b-instruct'); print(sum(x.stat().st_size for x in p.glob('*.safetensors')))"
~~~

Expected: config, tokenizer, and weights exist with non-zero total safetensors bytes and at least 5 GB free remains. If not, do not run inference.

- [x] **Step 3: Run exactly one adapter-free baseline evaluation.**

~~~powershell
python scripts/evaluate_ru_qwen_v4.py --config configs\ru_qwen_v5_3b.json --output checkpoints\ru-qwen-v5\base_evaluation.json
~~~

Do not pass an adapter. Capture peak allocated GPU memory. Stop on CUDA OOM or peak allocated VRAM above 5,500 MiB and record not_adopted rather than retrying with changed prompts or hidden quality settings.

- [x] **Step 4: Review every response (skipped because the automatic gate failed).**

Read all 32 prompt/response pairs in the evaluation artifact. Mark natural, relevant, expected_language, and voice for every case. Voice means Ru calls itself em and user anh whenever Vietnamese address is needed. Write 32 review items with short notes to checkpoints/ru-qwen-v5/base_review.json.

- [x] **Step 5: Record the phase decision.**

Use the decision-record API to add manual result, failure reasons, and verdict to base_evaluation.json.

| Condition | Verdict | Next action |
| --- | --- | --- |
| Any automatic disqualifier | not_adopted | Stop V5: no data or adapter. |
| Manual score below 24/32 | not_adopted | Stop V5: no data or adapter. |
| Zero automatic flags plus 24+/32 | passed | Draft a separate Phase 2 plan. |
| OOM or VRAM above 5,500 MiB | not_adopted | Stop V5 and report hardware evidence. |

- [x] **Step 6: Run the full non-training regression suite.**

~~~powershell
python -m pytest -q
~~~

Keep downloaded weights, checkpoints, and review artifacts out of Git.

## Task 4: Document the measured result and keep a clean decision trail

**Files:**

- Modify: README.md
- Modify: TODO.md
- Modify: docs/superpowers/specs/2026-07-21-ru-qwen-v5-local-3b-design.md
- Review: all V5 documents for stale planned-tense statements

- [x] **Step 1: Update docs only from measured evidence.**

After Task 3, add actual download state, peak VRAM, automatic result, manual score, and final verdict. Do not claim V5 is better unless the saved evaluation supports it.

On failure, state it was not adopted and no V5 corpus, adapter, or GUI change was created. On success, state only that Phase 1 passed and Phase 2 requires a separate approved design and implementation plan.

- [x] **Step 2: Check for contradictory documentation.**

~~~powershell
rg -n "V5|Qwen2.5-3B|Phase 1|Phase 2|pending|planned" README.md TODO.md docs
git diff --check
git status --short
~~~

Keep the approved design and this implementation plan as history; delete a document only if it is wholly superseded.

- [x] **Step 3: Commit the observed documentation.**

~~~powershell
git add README.md TODO.md docs/superpowers/specs/2026-07-21-ru-qwen-v5-local-3b-design.md
git diff --cached --check
git commit -m "docs: record Ru V5 baseline result"
~~~

Do not stage models, checkpoints, or evaluation JSON.

## Task 5: Conditional Phase 2 handoff, not implementation

**Files:**

- Create only after a passed baseline: docs/superpowers/specs/2026-07-21-ru-qwen-v5-phase2-design.md
- Create implementation plan only after the Phase 2 design is approved

- [x] **Step 1: Read the recorded verdict.**

Proceed only when base_evaluation.json says passed and includes all 32 human review items.

- [ ] **Step 2: If passed, design Phase 2 separately.**

The new design must retain the approved constraints: 240 literal individually reviewed project-owned rows (Vietnamese 110, English 100, mixed 30); at least 100 contextual rows; one low-rate QLoRA smoke; no template-generated samples; 5,500 MiB VRAM stop gate; adapter adoption only with automatic checks passing, at least 28/32 manual quality, and no regression against base.

- [x] **Step 3: If not passed, close V5 without training.**

Do not create a Phase 2 corpus, adapter, training command, or GUI option. Summarize failure evidence and propose a distinct hypothesis rather than fine-tuning a failed base by inertia.
