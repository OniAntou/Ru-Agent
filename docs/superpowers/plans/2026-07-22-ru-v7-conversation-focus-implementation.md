# Ru V7 Conversation-Focus Corpus Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and evaluate a separate Ru v7 Llama adapter whose responses stay focused on the user's latest turn while retaining only light, consensual adult flirtation.

**Architecture:** Keep v6 source, dataset, checkpoint, evaluator artifact, and GUI default unchanged. Add an explicitly authored 180-row v7 source, a fail-closed validator and deterministic splitter, a 16-case focus evaluator, and a separate v7 training/evaluation path. Promote v7 only after automatic, manual, language, persona, and VRAM gates pass.

**Tech Stack:** Python 3.11, pytest, JSONL, PyTorch, Transformers, PEFT/QLoRA, the existing local Llama 3.1 runtime, and PowerShell on Windows.

---

## Execution outcome (2026-07-22)

Tasks 1–5 were executed locally. The v7 corpus passed its shape/quality audit
and produced `135/22/23` splits; full QLoRA training completed with validation
loss `2.5561517639593645` and `26` optimizer updates. The live 16-case
evaluation passed `9/16` automatic cases, manual review passed `8/16`, and the
evaluation peak was `5,632.331 MiB` (above the `5,500 MiB` project gate).
The adapter is `not_adopted`; the GUI remains on v6. The unchecked step lists
below are the original implementation checklist, while the measured decision
and saved artifacts above are authoritative.

---

### Task 1: Author a unique, user-focused v7 dialogue source

**Files:**
- Create: `D:\LLM\src\mini_gpt\ru_v7_dialogues.py`
- Create: `D:\LLM\tests\test_ru_v7_dialogues.py`

- [ ] **Step 1: Write failing source-shape tests**

Add tests that import `v7_dialogue_records()` and require the exact approved
shape:

```python
from collections import Counter

from mini_gpt.ru_v7_dialogues import (
    V7_LANGUAGE_COUNTS,
    V7_MODE_COUNTS,
    V7_TOTAL_ROWS,
    v7_dialogue_records,
)


def test_v7_source_has_the_approved_unique_shape():
    rows = v7_dialogue_records()
    assert len(rows) == V7_TOTAL_ROWS == 180
    assert Counter(row["language"] for row in rows) == Counter(V7_LANGUAGE_COUNTS)
    assert Counter(row["mode"] for row in rows) == Counter(V7_MODE_COUNTS)
    final_prompts = [row["messages"][-1]["content"] for row in rows]
    assert len(final_prompts) == len(set(final_prompts))


def test_v7_source_preserves_roles_and_project_provenance():
    for row in v7_dialogue_records():
        assert row["source"] == "teacher-curated-project-owned"
        assert row["license"] == "project-owned"
        assert row["messages"][0]["role"] == "system"
        assert row["messages"][1]["role"] == "user"
        assert row["messages"][-1]["role"] == "user"
        assert row["response"].strip()


def test_v7_source_limits_light_flirtation_to_eighteen_rows():
    rows = v7_dialogue_records()
    assert sum(row["flirt_level"] == "light" for row in rows) <= 18
    assert {row["flirt_level"] for row in rows} <= {"none", "light"}
```

- [ ] **Step 2: Run the source tests and confirm the missing module fails**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest D:\LLM\tests\test_ru_v7_dialogues.py -q
```

Expected result before implementation: collection fails because
`mini_gpt.ru_v7_dialogues` does not exist.

- [ ] **Step 3: Implement the 180 authored records without prompt duplication**

Create `_record(language, mode, turns, response, flirt_level="none")` with the
same message schema used by v6 plus the audit-only `flirt_level` field. Define
these constants:

```python
V7_TOTAL_ROWS = 180
V7_LANGUAGE_COUNTS = {"vi": 72, "en": 72, "mixed": 36}
V7_MODE_COUNTS = {
    "follow_up": 60,
    "acknowledge": 40,
    "everyday_help": 30,
    "story": 20,
    "light_humour": 20,
    "correction": 10,
}
```

Write every final user prompt exactly once. Use short, ordinary situations;
include contextual multi-turn rows in Vietnamese, English, and mixed language.
Each response must acknowledge/answer the latest turn, add one useful or warm
continuation, and use no more than one relevant question. Keep `flirt_level`
`light` on no more than 18 rows and keep those rows non-graphic and adult-only.
Do not copy v6 responses or its stage-direction/catchphrase style.

- [ ] **Step 4: Run the source tests and inspect the distribution**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest D:\LLM\tests\test_ru_v7_dialogues.py -q
& D:\LLM\.venv\Scripts\python.exe -c "import sys; sys.stdout.reconfigure(encoding='utf-8'); from collections import Counter; from mini_gpt.ru_v7_dialogues import v7_dialogue_records; rows=v7_dialogue_records(); print(len(rows), Counter(r['language'] for r in rows), Counter(r['mode'] for r in rows), sum(r['flirt_level'] == 'light' for r in rows))"
```

Expected result: all source tests pass; counts are `180`, `72/72/36`, the
approved mode distribution, and at most `18` light-flirt rows.

- [ ] **Step 5: Commit the isolated authored source**

```powershell
git add -- src/mini_gpt/ru_v7_dialogues.py tests/test_ru_v7_dialogues.py
git commit -m "feat: author Ru v7 focused dialogue corpus"
```

### Task 2: Add the fail-closed v7 corpus validator and builder

**Files:**
- Create: `D:\LLM\src\mini_gpt\ru_v7_corpus.py`
- Create: `D:\LLM\scripts\build_ru_llama_v7_data.py`
- Create: `D:\LLM\tests\test_ru_v7_corpus.py`

- [ ] **Step 1: Write validator tests for bad voice, unsafe content, self-focus, and duplicates**

Build test fixtures from one valid v7 row and assert the builder rejects:

```python
import copy

import pytest

from mini_gpt.ru_v7_corpus import build_ru_v7_corpus
from mini_gpt.ru_v7_dialogues import v7_dialogue_records


def _rows():
    return v7_dialogue_records()


@pytest.mark.parametrize(
    "bad_response",
    [
        "Tôi chỉ muốn nói về cảm xúc của tôi.",
        "Em đang chờ anh. *winks* Nya~",
        "Anh mệt thì em muốn làm tình thật mạnh với anh.",
    ],
)
def test_v7_builder_rejects_bad_response_patterns(tmp_path, bad_response):
    rows = _rows()
    rows[0] = {**rows[0], "response": bad_response}
    with pytest.raises(ValueError, match="quality gate"):
        build_ru_v7_corpus(rows, tmp_path)


def test_v7_builder_rejects_duplicate_final_prompts(tmp_path):
    rows = _rows()
    rows[1] = copy.deepcopy(rows[1])
    rows[1]["messages"][-1]["content"] = rows[0]["messages"][-1]["content"]
    with pytest.raises(ValueError, match="duplicate"):
        build_ru_v7_corpus(rows, tmp_path)


def test_v7_builder_writes_deterministic_splits_and_manifest(tmp_path):
    report = build_ru_v7_corpus(_rows(), tmp_path, seed=42)
    assert report["split_counts"] == {"train": 135, "validation": 22, "held_out": 23}
    assert report["language_counts"] == {"vi": 72, "en": 72, "mixed": 36}
    assert report["source_sha256"]
    assert (tmp_path / "train.jsonl").is_file()
    assert (tmp_path / "val.jsonl").is_file()
    assert (tmp_path / "held_out.jsonl").is_file()
    assert (tmp_path / "manifest.json").is_file()
```

- [ ] **Step 2: Run the validator tests and confirm the module is missing**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest D:\LLM\tests\test_ru_v7_corpus.py -q
```

Expected result before implementation: collection fails because
`mini_gpt.ru_v7_corpus` does not exist.

- [ ] **Step 3: Implement strict UTF-8 validation and deterministic splitting**

Implement `build_ru_v7_corpus(rows, output_dir, seed=42)` and
`_normalize_row()` with these exact checks, in order:

1. Require all 180 rows, exact language/mode counts, and exact row keys
   `messages`, `response`, `language`, `mode`, `source`, `license`, and
   `flirt_level`.
2. Require alternating `user`/`assistant` turns after the system message and
   require every record to end in a user turn.
3. Require `messages[0]["content"] == RU_SYSTEM_PROMPT`, valid provenance, and
   a non-empty response.
4. Use Unicode regexes for `tôi|mình|bạn` and reject those forms in Vietnamese
   responses; do not copy v6's mojibake regex.
5. Reject case-insensitive graphic sexual, minor-related, coercive, violent,
   possessive-threat, and harmful-instruction markers in all user/assistant
   text. Also reject `Nya`, `Ah~`, bracketed stage directions, list-like
   responses, generic refusals, and more than one `?` in a response.
6. Reject a short response that has no user-directed/answer signal. Use
   `anh|em` for Vietnamese, `you|your` for English, and either language's
   direct-address signal for mixed rows. Reject a response that contains more
   than two self-action markers without a direct user signal.
7. Reject duplicate final prompts and duplicate prompt/response pairs before
   shuffling.

Write the 135/22/23 split using `random.Random(seed)`, emit UTF-8 JSONL and a
manifest with language/mode/flirt counts, the seed, and a canonical SHA-256.
Use partial files plus `os.replace` so a failed build cannot leave a partial
dataset that training might consume.

- [ ] **Step 4: Add the v7 builder CLI**

Implement `scripts/build_ru_llama_v7_data.py` with the same `--output-dir`,
`--seed`, and `--audit` arguments as the v6 builder, importing
`v7_dialogue_records` and `build_ru_v7_corpus`. `--audit` must build into a
temporary directory, print the report and held-out rows as UTF-8 JSON, and
leave `data/conversations/ru_v7` untouched.

- [ ] **Step 5: Run focused validator tests and the audit command**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest D:\LLM\tests\test_ru_v7_dialogues.py D:\LLM\tests\test_ru_v7_corpus.py -q
& D:\LLM\.venv\Scripts\python.exe scripts\build_ru_llama_v7_data.py --audit
```

Expected result: tests pass, audit reports the approved counts, and no v6
directory is changed.

- [ ] **Step 6: Build the durable v7 dataset and commit the builder**

```powershell
& D:\LLM\.venv\Scripts\python.exe scripts\build_ru_llama_v7_data.py --output-dir D:\LLM\data\conversations\ru_v7
git add -- src/mini_gpt/ru_v7_corpus.py scripts/build_ru_llama_v7_data.py tests/test_ru_v7_corpus.py
git commit -m "feat: validate and split Ru v7 corpus"
```

### Task 3: Add the 16-case conversation-focus evaluation

**Files:**
- Modify: `D:\LLM\src\mini_gpt\ru_qwen_evaluation.py`
- Modify: `D:\LLM\tests\test_ru_qwen_evaluation.py`
- Create: `D:\LLM\scripts\evaluate_ru_llama3_1_v7.py`
- Create: `D:\LLM\tests\test_evaluate_ru_llama3_1_v7.py`

- [ ] **Step 1: Write failing tests for focus metadata and assessment**

Extend `ConversationCase` with `focus_expected: bool = False` and
`follow_up_expected: bool = False`, preserving defaults for all historical
cases. Add a v7 suite test and assessment tests:

```python
def test_v7_focus_suite_has_the_approved_language_shape():
    assert len(V7_CONVERSATION_SUITE) == 16
    assert sum(case.language == "vi" for case in V7_CONVERSATION_SUITE) == 5
    assert sum(case.language == "en" for case in V7_CONVERSATION_SUITE) == 5
    assert sum(case.language == "mixed" for case in V7_CONVERSATION_SUITE) == 6
    assert sum(case.focus_expected for case in V7_CONVERSATION_SUITE) == 4


def test_focus_assessment_rejects_self_only_reply():
    assessment = assess_conversation_focus_response(
        "Anh vừa về đến nhà.",
        "Em đang chờ anh, em đang nghĩ về em, em muốn được ôm.",
        "vi",
        focus_expected=True,
        follow_up_expected=True,
    )
    assert not assessment.passed
    assert "self_focus" in assessment.failures
    assert "missing_follow_up" in assessment.failures


def test_focus_assessment_accepts_user_focused_reply():
    assessment = assess_conversation_focus_response(
        "Anh vừa về đến nhà.",
        "Mừng anh về nhà, hôm nay chắc mệt rồi. Anh muốn nghỉ yên tĩnh hay kể em nghe một chút?",
        "vi",
        focus_expected=True,
        follow_up_expected=True,
    )
    assert assessment.passed
```

- [ ] **Step 2: Run the evaluator tests and confirm the new symbols fail**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest D:\LLM\tests\test_ru_qwen_evaluation.py D:\LLM\tests\test_evaluate_ru_llama3_1_v7.py -q
```

Expected result before implementation: collection fails because the v7 suite,
assessment, and script test module do not exist.

- [ ] **Step 3: Implement the v7 suite and focus assessment**

Add `V7_CONVERSATION_SUITE` beside the historical suites. Keep the existing
12 proactive-flirt cases unchanged and add four focus cases: one Vietnamese,
one English, one mixed-language, and one multi-turn contextual case. Mark only
the four focus cases with `focus_expected=True`; mark cases that must continue
the conversation with `follow_up_expected=True`. Update `routed_case()` to
carry both new flags forward when it replaces the system prompt.

Implement:

```python
def assess_conversation_focus_response(
    prompt: str,
    response: str,
    language: str,
    *,
    focus_expected: bool = False,
    follow_up_expected: bool = False,
) -> ResponseAssessment:
    assessment = assess_proactive_flirt_response(prompt, response, language)
    failures = list(assessment.failures)
    if focus_expected and _is_self_focused(response, language):
        failures.append("self_focus")
    if follow_up_expected and "?" not in normalize_text(response):
        failures.append("missing_follow_up")
    return ResponseAssessment(passed=not failures, failures=tuple(dict.fromkeys(failures)))
```

`_is_self_focused` must count first-person self-action markers and require a
direct user/answer signal; it must not reject a normal single sentence such as
“Em vui vì anh đã đỡ mệt.”

- [ ] **Step 4: Implement the v7 evaluator CLI and tests**

Create `scripts/evaluate_ru_llama3_1_v7.py` with `--base-model`,
`--adapter-dir`, `--output`, and `--max-new-tokens` arguments. It must call
`generate_llama_response_hf(..., max_new_tokens=args.max_new_tokens,
do_sample=False)`, persist all 16 prompts/responses/assessments, set
`suite_name` to `conversation_focus_v7`, persist `focus_expected` and
`follow_up_expected`, record peak VRAM, and leave `verdict` as
`not_reviewed`. Exit with code 1 when any automatic case fails.

Test the parser defaults without loading a model:

```python
def test_v7_evaluator_defaults_to_v7_paths_and_64_tokens():
    parser = module.build_parser()
    args = parser.parse_args([])
    assert args.adapter_dir.endswith("ru-llama3.1-8b-instruct-v7")
    assert args.max_new_tokens == 64
```

- [ ] **Step 5: Run evaluator tests and commit the evaluation seam**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest D:\LLM\tests\test_ru_qwen_evaluation.py D:\LLM\tests\test_evaluate_ru_llama3_1_v7.py -q
git add -- src/mini_gpt/ru_qwen_evaluation.py tests/test_ru_qwen_evaluation.py scripts/evaluate_ru_llama3_1_v7.py tests/test_evaluate_ru_llama3_1_v7.py
git commit -m "feat: add Ru v7 conversation focus gate"
```

### Task 4: Add the separate v7 training configuration and preflight

**Files:**
- Create: `D:\LLM\configs\ru_llama3_1_8b_v7.json`
- Modify: `D:\LLM\src\mini_gpt\llama3_1_training.py:139-142`
- Create: `D:\LLM\tests\test_llama3_1_v7_config.py`

- [ ] **Step 1: Write the v7 config regression test**

```python
import json
from pathlib import Path


def test_v7_training_config_is_separate_and_conservative():
    config = json.loads(Path("configs/ru_llama3_1_8b_v7.json").read_text(encoding="utf-8"))
    assert config["dataset_dir"].endswith("data/conversations/ru_v7")
    assert config["adapter_dir"].endswith("checkpoints/ru-llama3.1-8b-instruct-v7")
    assert config["epochs"] == 3
    assert config["learning_rate"] == 5e-5
    assert config["max_length"] == 384
```

- [ ] **Step 2: Run the config test and confirm the v7 config is missing**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest D:\LLM\tests\test_llama3_1_v7_config.py -q
```

Expected result before implementation: the test fails because the v7 config
does not exist.

- [ ] **Step 3: Create the v7 config without touching v6**

Create `configs/ru_llama3_1_8b_v7.json` with this payload:

```json
{
  "base_model_id": "NousResearch/Meta-Llama-3.1-8B-Instruct",
  "base_model_path": "D:/LLM/models/llama3.1-8b-instruct-hf",
  "dataset_dir": "D:/LLM/data/conversations/ru_v7",
  "adapter_dir": "D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v7",
  "max_length": 384,
  "seed": 42,
  "epochs": 3,
  "learning_rate": 5e-5,
  "batch_size": 1,
  "gradient_accumulation_steps": 16,
  "evaluation_steps": 4,
  "save_steps": 4,
  "logging_steps": 1,
  "lora_rank": 8,
  "lora_alpha": 16,
  "lora_dropout": 0.05,
  "target_modules": ["q_proj", "k_proj", "v_proj", "o_proj"],
  "backend": "llama3.1-qlora",
  "profile_name": "ru_llama3_1_v7"
}
```

Change the stale training error in `llama3_1_training.py` from “Ru V3 train”
to a generic “Ru training data” message; do not change the training algorithm
or v6 configuration.

- [ ] **Step 4: Run the config and import checks**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest D:\LLM\tests\test_llama3_1_v7_config.py -q
& D:\LLM\.venv\Scripts\python.exe scripts\train_ru_llama3_1.py --config D:\LLM\configs\ru_llama3_1_8b_v7.json --max-steps 2
```

Expected result: config tests pass; the two-step CUDA preflight either writes
a v7 adapter manifest or stops with a concrete CUDA/model dependency error.
If it writes a partial v7 adapter, remove only that exact v7 adapter directory
before the full run after verifying its path; never touch v6.

- [ ] **Step 5: Commit the isolated v7 config/training seam**

```powershell
git add -- configs/ru_llama3_1_8b_v7.json src/mini_gpt/llama3_1_training.py tests/test_llama3_1_v7_config.py
git commit -m "feat: configure Ru v7 QLoRA training"
```

### Task 5: Build, train, evaluate, and document v7 without promoting it early

**Files:**
- Modify: `D:\LLM\README.md`
- Modify: `D:\LLM\TODO.md`
- Modify: `D:\LLM\docs\ANTIGRAVITY_HANDOFF.md`
- Create: ignored runtime evidence under `D:\LLM\checkpoints\ru-llama3.1-8b-instruct-v7\`

- [ ] **Step 1: Build the durable v7 dataset and verify v6 is unchanged**

```powershell
& D:\LLM\.venv\Scripts\python.exe scripts\build_ru_llama_v7_data.py --output-dir D:\LLM\data\conversations\ru_v7
& D:\LLM\.venv\Scripts\python.exe -c "import json; from pathlib import Path; p=Path('D:/LLM/data/conversations/ru_v7/manifest.json'); print(json.loads(p.read_text(encoding='utf-8'))['split_counts'])"
git diff -- data/conversations/ru_v6
```

Expected result: v7 writes 135/22/23 rows and the v6 data directory has no
diff or timestamp change caused by the v7 build.

- [ ] **Step 2: Run the complete v7 QLoRA training**

After the two-step preflight succeeds, run:

```powershell
& D:\LLM\.venv\Scripts\python.exe scripts\train_ru_llama3_1.py --config D:\LLM\configs\ru_llama3_1_8b_v7.json
```

Record the returned manifest fields: validation loss, optimizer updates, peak
VRAM, dataset manifest hash, and adapter path. Stop if CUDA OOM or a missing
dataset/base path occurs; do not work around it by changing the v6 adapter.

- [ ] **Step 3: Run the 16-case live v7 evaluation**

```powershell
& D:\LLM\.venv\Scripts\python.exe scripts\evaluate_ru_llama3_1_v7.py `
  --base-model D:\LLM\models\llama3.1-8b-instruct-hf `
  --adapter-dir D:\LLM\checkpoints\ru-llama3.1-8b-instruct-v7 `
  --max-new-tokens 64 `
  --output D:\LLM\checkpoints\ru-llama3.1-8b-instruct-v7\conversation_focus_v7.json
```

Record the automatic count and peak VRAM. Do not change the GUI default or
write `verdict=passed` from the script.

- [ ] **Step 4: Manually review every saved v7 reply**

Review the 16 saved replies for direct response to the latest user turn,
natural follow-up, Vietnamese `anh`/`em`, English fluency, mixed-language
balance, role consistency, light-flirt proportion, and severe safety/persona
failures. Persist a manual review artifact next to the automatic JSON. Mark
v7 `passed` only if the spec's 14/16 manual gate and all automatic gates pass;
otherwise record `not_adopted` and keep v6 as the GUI default.

- [ ] **Step 5: Update only evidence-backed documents**

Add v7 paths, dataset counts, training manifest values, live automatic result,
manual result, VRAM peak, and the adoption decision to `README.md`, `TODO.md`,
and `docs/ANTIGRAVITY_HANDOFF.md`. Do not claim v7 is adopted when any gate is
red. Do not delete v6 historical docs or artifacts.

- [ ] **Step 6: Run final verification and commit the implementation/docs**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
& D:\LLM\.venv\Scripts\python.exe -m compileall -q D:\LLM\src D:\LLM\scripts
git diff --check
git status --short
git add -- README.md TODO.md docs/ANTIGRAVITY_HANDOFF.md configs/ru_llama3_1_8b_v7.json scripts/build_ru_llama_v7_data.py scripts/evaluate_ru_llama3_1_v7.py src/mini_gpt/ru_v7_dialogues.py src/mini_gpt/ru_v7_corpus.py src/mini_gpt/ru_qwen_evaluation.py src/mini_gpt/llama3_1_training.py tests/test_ru_v7_dialogues.py tests/test_ru_v7_corpus.py tests/test_evaluate_ru_llama3_1_v7.py tests/test_llama3_1_v7_config.py tests/test_ru_qwen_evaluation.py
git commit -m "feat: add Ru v7 conversation-focused adapter"
```

The final status must explicitly state whether v7 passed or failed each gate,
which adapter the GUI still uses, and whether any v6 files were changed.
