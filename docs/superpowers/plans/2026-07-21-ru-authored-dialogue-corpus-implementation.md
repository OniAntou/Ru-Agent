# Ru Authored Dialogue Corpus Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace V3's unsuitable external Q&A corpus with a locally authored, validated 120-example Vietnamese-English dialogue corpus that teaches Ru to address the user as `anh` and itself as `em` without synthetic repetition.

**Architecture:** A versioned Python source module owns the hand-authored dialogue frames and emits canonical Qwen JSONL records. A pure corpus builder validates those records, calculates a reproducible source checksum, splits them deterministically, and atomically writes ignored runtime artifacts under `data/conversations/ru_v3`. The build CLI is local-only: it downloads nothing and does not read V2/open-source conversation data.

**Tech Stack:** Python 3.11, existing `mini_gpt.data.normalize_text`, JSONL, pytest, standard-library `hashlib` and `random`.

> **Execution status (2026-07-21):** Tasks 1 through 4 are complete. The corpus
> passed its 24-row manual audit and produced 120 examples (60 Vietnamese, 60
> English), including 30 contextual examples. QLoRA download/training remains a
> separate unfinished phase.

---

## File structure

| Path | Responsibility |
| --- | --- |
| `D:\LLM\src\mini_gpt\ru_style.py` | Single source of truth for Ru's Qwen system prompt and Vietnamese `em`/`anh` default. |
| `D:\LLM\src\mini_gpt\ru_authored_dialogues.py` | Fixed, project-authored dialogue frames and deterministic conversion into V3 records. |
| `D:\LLM\src\mini_gpt\ru_qwen_corpus.py` | Local-record validation, quality gate, deterministic split, audit selection, manifest, and atomic JSONL publishing. |
| `D:\LLM\scripts\build_ru_qwen_v3_data.py` | No-network command line entry point for build or stratified audit. |
| `D:\LLM\tests\test_ru_authored_dialogues.py` | Voice-policy and authored-source coverage tests. |
| `D:\LLM\tests\test_ru_qwen_corpus.py` | Corpus atomic-write, manifest, deterministic split, and CLI tests. |
| `D:\LLM\README.md` | Current V3 corpus command, audit command, and explicit quality gate. |
| `D:\LLM\TODO.md` | Tracks external V3 corpus rejection and authored-corpus completion without marking natural conversation complete. |
| `D:\LLM\docs\superpowers\specs\2026-07-21-ru-authored-dialogue-corpus-design.md` | Approved source-of-truth design; only update it if implementation exposes a contradiction. |

### Task 1: Centralize Ru's voice policy

**Files:**
- Create: `D:\LLM\src\mini_gpt\ru_style.py`
- Modify: `D:\LLM\src\mini_gpt\ru_qwen_corpus.py`
- Create: `D:\LLM\tests\test_ru_authored_dialogues.py`

- [ ] **Step 1: Write the failing voice-policy test**

```python
from mini_gpt.ru_style import RU_SYSTEM_PROMPT


def test_ru_system_prompt_defaults_to_em_for_anh_and_requires_an_explicit_override():
    assert 'call the user "anh"' in RU_SYSTEM_PROMPT
    assert 'yourself as "em"' in RU_SYSTEM_PROMPT
    assert "explicitly requests" in RU_SYSTEM_PROMPT
```

- [ ] **Step 2: Run the test to verify the new module is absent**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_ru_authored_dialogues.py::test_ru_system_prompt_defaults_to_em_for_anh_and_requires_an_explicit_override -q
```

Expected: FAIL with `ModuleNotFoundError: No module named 'mini_gpt.ru_style'`.

- [ ] **Step 3: Add the one shared policy constant**

Create `src/mini_gpt/ru_style.py` exactly as follows:

```python
RU_SYSTEM_PROMPT = (
    "You are Ru, a friendly bilingual local companion. Reply naturally and concisely in the user's language. "
    'In Vietnamese, call the user "anh" and refer to yourself as "em" by default. '
    "Use another Vietnamese form of address only when the user explicitly requests it."
)
```

Replace the local `RU_SYSTEM_PROMPT` declaration in `ru_qwen_corpus.py` with:

```python
from .ru_style import RU_SYSTEM_PROMPT
```

No code may maintain a second system-prompt string.

- [ ] **Step 4: Run the focused test**

Run the Step 2 command. Expected: PASS.

- [ ] **Step 5: Commit the voice-policy seam**

```powershell
git add src/mini_gpt/ru_style.py src/mini_gpt/ru_qwen_corpus.py tests/test_ru_authored_dialogues.py
git commit -m "feat: centralize Ru anh-em voice policy"
```

### Task 2: Create the project-authored dialogue source

**Files:**
- Create: `D:\LLM\src\mini_gpt\ru_authored_dialogues.py`
- Modify: `D:\LLM\tests\test_ru_authored_dialogues.py`

- [ ] **Step 1: Write failing authored-source coverage tests**

```python
from mini_gpt.ru_authored_dialogues import authored_dialogue_records


def test_authored_dialogues_cover_both_languages_and_contextual_turn_taking():
    records = authored_dialogue_records()

    assert len(records) == 120
    assert {record["language"] for record in records} == {"vi", "en"}
    assert sum(len(record["messages"]) > 2 for record in records) >= 30
    assert {record["source"] for record in records} == {"ru_project_seed"}
    assert {record["license"] for record in records} == {"project-owned"}


def test_authored_vietnamese_records_model_the_default_anh_em_voice():
    records = authored_dialogue_records()
    vietnamese = [record for record in records if record["language"] == "vi"]

    assert any("anh" in record["response"].lower() and "em" in record["response"].lower() for record in vietnamese)
```

- [ ] **Step 2: Run the tests to verify the source is absent**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_ru_authored_dialogues.py -q
```

Expected: FAIL because `authored_dialogue_records` is unavailable.

- [ ] **Step 3: Implement the authored frame catalog**

Create immutable frames rather than scraping or templating arbitrary answers:

```python
from __future__ import annotations

from dataclasses import dataclass
from typing import Literal

from .ru_style import RU_SYSTEM_PROMPT

Language = Literal["vi", "en"]
SOURCE = "ru_project_seed"
LICENSE = "project-owned"


@dataclass(frozen=True)
class DialogueFrame:
    language: Language
    messages: tuple[tuple[str, str], ...]
    response: str


def authored_dialogue_records() -> list[dict[str, object]]:
    records: list[dict[str, object]] = []
    for frame in ALL_FRAMES:
        records.append(
            {
                "messages": [{"role": "system", "content": RU_SYSTEM_PROMPT}, *({"role": role, "content": content} for role, content in frame.messages)],
                "response": frame.response,
                "language": frame.language,
                "source": SOURCE,
                "license": LICENSE,
            }
        )
    return records
```

Populate `DialogueFrame` literals in four named tuples: `CASUAL_FRAMES`,
`CONTEXTUAL_FRAMES`, `BILINGUAL_FRAMES`, and `EVERYDAY_HELP_FRAMES`. Each frame
must use the actual conversational lines below as its style contract:

```python
CASUAL_FRAMES = (
    DialogueFrame("vi", (("user", "Xin chào em."),), "Chào anh, hôm nay của anh thế nào rồi?"),
    DialogueFrame("vi", (("user", "Anh hơi mệt sau một ngày dài."),), "Vậy anh nghỉ một chút nhé. Nếu muốn kể thêm, em ở đây nghe anh."),
    DialogueFrame("en", (("user", "Hi Ru, I just got home."),), "Welcome back. Was your day more tiring or more fun?"),
    DialogueFrame("en", (("user", "Thanks for listening."),), "Any time. I am glad you told me."),
)

CONTEXTUAL_FRAMES = (
    DialogueFrame(
        "vi",
        (("user", "Anh vừa chơi game xong."), ("assistant", "Nghe vui đấy. Anh chơi gì vậy?"), ("user", "Anh chơi một trận bóng đá.")),
        "Chắc trận đó khá căng thẳng nhỉ? Anh thắng không?",
    ),
    DialogueFrame(
        "en",
        (("user", "I am making dinner."), ("assistant", "Nice. What are you cooking?"), ("user", "Just noodles and vegetables.")),
        "That sounds comforting. I hope it turns out exactly how you want it.",
    ),
)
```

Expand the literal catalog to 120 distinct frames: 60 casual, 30 contextual,
12 bilingual, and 18 everyday-help frames. Each frame has exactly one complete,
individually written response; do not combine independent prompt and response
lists and do not generate paraphrase variants. That produces exactly 120 records
whose wording is reviewed rather than mechanically inflated.

For every record, prepend `{"role": "system", "content": RU_SYSTEM_PROMPT}`
to the frame's messages and emit `response`, `language`, `source`, and
`license`. Raise `ValueError` if any record's last message is not `user`, the
role sequence is invalid, or its response is not one of that frame's authored
variants.

- [ ] **Step 4: Run the source tests**

Run the Step 2 command. Expected: PASS with both language coverage and at least
30 records containing an earlier assistant message.

- [ ] **Step 5: Commit the versioned authored source**

```powershell
git add src/mini_gpt/ru_authored_dialogues.py tests/test_ru_authored_dialogues.py
git commit -m "feat: add authored Ru dialogue source"
```

### Task 3: Refactor the corpus builder to be local-only and reproducible

**Files:**
- Modify: `D:\LLM\src\mini_gpt\ru_qwen_corpus.py`
- Modify: `D:\LLM\tests\test_ru_qwen_corpus.py`

- [ ] **Step 1: Replace external-source tests with a failing local-record test**

```python
from mini_gpt.ru_authored_dialogues import authored_dialogue_records
from mini_gpt.ru_qwen_corpus import build_ru_qwen_corpus


def test_builds_authored_corpus_with_reproducible_project_provenance(tmp_path):
    report = build_ru_qwen_corpus(authored_dialogue_records(), tmp_path, seed=7)

    assert report["total_examples"] == 120
    assert report["contextual_examples"] >= 30
    assert report["source_counts"] == {"ru_project_seed": 120}
    assert report["licenses"] == {"project-owned": 120}
    assert len(report["source_sha256"]) == 64
    assert (tmp_path / "train.jsonl").is_file()
    assert (tmp_path / "val.jsonl").is_file()
    assert not list(tmp_path.glob("*.partial"))
```

- [ ] **Step 2: Run the focused test to confirm the old path cannot satisfy it**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_ru_qwen_corpus.py::test_builds_authored_corpus_with_reproducible_project_provenance -q
```

Expected: FAIL because the existing builder requires a source path rather than
a record sequence.

- [ ] **Step 3: Replace V2/5CD ingestion with pure record validation**

Remove `chat_sources` imports, `_read_pairs`, `_qwen_record`,
`extract_contextual_5cd_records`, 5CD constants, network revision fields, and
any environment/cache mutation. Implement this interface:

```python
def build_ru_qwen_corpus(
    records: list[dict[str, object]],
    output_dir: str | Path,
    validation_ratio: float = 0.1,
    seed: int = 42,
    minimum_examples: int = 120,
    minimum_contextual_examples: int = 30,
) -> dict[str, object]:
```

`_normalize_record` must require exactly the five public fields from the
approved schema: `messages`, `response`, `language`, `source`, and `license`.
It must require a first system message equal to `RU_SYSTEM_PROMPT`, a final user
message, valid alternating user/assistant turns after the system message, and
a non-empty response. Use `casual_rejection_reason` for short-answer quality;
extend it to reject `http://`, `https://`, `www.`, triple backticks, angle-tag
markup, a newline bullet/numbered list, and the known AI-disclaimer patterns.

Calculate `source_sha256` by serializing normalized input records with
`json.dumps(record, ensure_ascii=False, sort_keys=True, separators=(",", ":"))`,
joining lines with `"\n"`, and applying `hashlib.sha256` to UTF-8 bytes. Globally
deduplicate on `(messages, response)`; count contextual records as records with
an assistant message before the final user turn. Require exactly `vi` and `en`,
exactly 120 accepted records, and at least 30 contextual records before
creating the destination directory.

Manifest keys must be `generated_at_utc`, `total_examples`, `train_examples`,
`validation_examples`, `language_counts`, `source_counts`, `contextual_examples`,
`licenses`, `source_sha256`, `filtering`, `rejected`, `seed`, and
`validation_ratio`. Retain the existing `.partial` plus `os.replace` publishing
behavior. Do not write a file when any quality threshold fails.

- [ ] **Step 4: Add failing threshold and deterministic-split tests**

```python
def test_rejects_authored_records_that_break_the_voice_or_context_gate(tmp_path):
    broken = authored_dialogue_records()[:-1]

    with pytest.raises(ValueError, match="exactly 120"):
        build_ru_qwen_corpus(broken, tmp_path)
    assert not tmp_path.exists()


def test_same_authored_input_and_seed_produce_the_same_split(tmp_path):
    records = authored_dialogue_records()
    build_ru_qwen_corpus(records, tmp_path / "one", seed=9)
    build_ru_qwen_corpus(records, tmp_path / "two", seed=9)

    assert (tmp_path / "one" / "train.jsonl").read_bytes() == (tmp_path / "two" / "train.jsonl").read_bytes()
    assert (tmp_path / "one" / "val.jsonl").read_bytes() == (tmp_path / "two" / "val.jsonl").read_bytes()
```

- [ ] **Step 5: Run the complete corpus tests**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_ru_qwen_corpus.py tests\test_ru_authored_dialogues.py -q
```

Expected: PASS. The old external-source tests must be removed because they test
the rejected data strategy, not retained as misleading coverage.

- [ ] **Step 6: Commit the local-only builder**

```powershell
git add src/mini_gpt/ru_qwen_corpus.py tests/test_ru_qwen_corpus.py
git commit -m "feat: build local authored Ru corpus"
```

### Task 4: Provide a no-network builder and human audit command

**Files:**
- Modify: `D:\LLM\scripts\build_ru_qwen_v3_data.py`
- Modify: `D:\LLM\tests\test_ru_qwen_corpus.py`

- [ ] **Step 1: Write a failing CLI contract test**

```python
def test_authored_corpus_cli_help_has_no_external_dataset_flags():
    result = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True, check=False)

    assert result.returncode == 0
    assert "Build the local authored Ru Qwen V3 conversation corpus" in result.stdout
    assert "--input" not in result.stdout
    assert "--cache-dir" not in result.stdout
    assert "--audit" in result.stdout
```

- [ ] **Step 2: Run the focused CLI test**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_ru_qwen_corpus.py::test_authored_corpus_cli_help_has_no_external_dataset_flags -q
```

Expected: FAIL because the current CLI exposes `--input`, `--cache-dir`, and
5CD flags.

- [ ] **Step 3: Make the script local-only**

The parser must expose only `--output-dir`, `--seed`, and `--audit`. `--audit`
prints a JSON object containing exactly 24 deterministic rows selected by
cycling through four source buckets: Vietnamese short, English short,
Vietnamese contextual, and English contextual. Each audit row includes
`language`, `turns`, `prompt`, and `response`; it must never write files.

Without `--audit`, call:

```python
report = build_ru_qwen_corpus(
    authored_dialogue_records(),
    output_dir=args.output_dir,
    seed=args.seed,
)
```

Print only the JSON report on successful build. The script must import no
`datasets`, `huggingface_hub`, or network library.

- [ ] **Step 4: Run CLI and audit checks**

Run:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_ru_qwen_corpus.py -q
& .\.venv\Scripts\python.exe scripts\build_ru_qwen_v3_data.py --audit
& .\.venv\Scripts\python.exe scripts\build_ru_qwen_v3_data.py --output-dir data\conversations\ru_v3
```

Expected: tests PASS; the audit has 24 plain conversation rows with both
languages and contextual examples; the build reports 120 records, both
languages, at least 30 contextual records, project-only provenance, and no
partial files.

- [ ] **Step 5: Conduct the manual quality gate**

Review the 24 audit records. Stop before Qwen download/training if two or more
records contain a list, exercise/instruction, AI self-description, missing
context reference, or a non-`anh`/`em` Vietnamese reply. Correct the authored
frame literals and repeat the whole task until fewer than two failures remain.

- [ ] **Step 6: Commit the local CLI**

```powershell
git add scripts/build_ru_qwen_v3_data.py tests/test_ru_qwen_corpus.py
git commit -m "feat: audit authored Ru dialogue corpus"
```

### Task 5: Record the V3 data decision and verify repository documentation

**Files:**
- Modify: `D:\LLM\README.md`
- Modify: `D:\LLM\TODO.md`
- Modify: `D:\LLM\docs\superpowers\plans\2026-07-20-ru-qwen-qlora-conversation-implementation.md`
- Modify: `D:\LLM\docs\superpowers\specs\2026-07-21-ru-authored-dialogue-corpus-design.md` only if the completed implementation changed a documented invariant.

- [ ] **Step 1: Add the V3 authored-corpus commands to the README**

Add a `## Ru V3 authored conversation corpus` section after the V2 result. It
must state that V2/V3 external seed data was rejected by a manual quality audit
and that Qwen V3 uses project-authored data for style, not general knowledge.
Include these commands:

```powershell
& .\.venv\Scripts\python.exe scripts\build_ru_qwen_v3_data.py --audit
& .\.venv\Scripts\python.exe scripts\build_ru_qwen_v3_data.py --output-dir data\conversations\ru_v3
```

State the 120/30/both-language/project-owned gates and Ru's default `em`/`anh`
Vietnamese voice. Do not add a QLoRA training command until its implementation
and CUDA smoke test exist.

- [ ] **Step 2: Update the roadmap and old V3 plan honestly**

In `TODO.md`, add a checked item for the authored V3 corpus only after Task 4's
manual audit passes; leave `Make Ru consistently natural...` unchecked. In the
2026-07-20 plan, mark its external-corpus Task 4 as superseded with the exact
link to this plan; do not mark Qwen download, training, GUI, or acceptance
tasks complete.

- [ ] **Step 3: Run the final documentation and test checks**

Run:

```powershell
rg -n "external-source|ru_project_seed|project-owned|120|30|em.*anh|--audit" README.md TODO.md docs\superpowers\plans docs\superpowers\specs
& .\.venv\Scripts\python.exe -m pytest -q
git diff --check
```

Expected: the docs distinguish rejected external data from the authored source,
the full suite passes, and no whitespace errors are present.

- [ ] **Step 4: Commit the documentation record**

```powershell
git add README.md TODO.md docs/superpowers/plans/2026-07-20-ru-qwen-qlora-conversation-implementation.md docs/superpowers/specs/2026-07-21-ru-authored-dialogue-corpus-design.md
git commit -m "docs: record Ru authored corpus workflow"
```

## Plan self-review

- **Spec coverage:** Task 1 centralizes default `em`/`anh`; Task 2 supplies 120
  individually written project-authored Vietnamese-English records and 30 contextual
  records; Task 3 validates, hashes, splits, and atomically publishes them;
  Task 4 supplies the 24-row audit and prevents network ingestion; Task 5
  documents the decision while preserving the unfinished natural-conversation
  acceptance gate.
- **Placeholder scan:** No implementation step delegates a vague filter, source,
  count, provenance, command, or acceptance condition. The 120 frame literals
  are a deliberately bounded authored catalog with one reviewed response per
  frame and no generated paraphrase variants.
- **Type consistency:** The shared schema is `messages`, `response`,
  `language`, `source`, and `license` throughout. `authored_dialogue_records()`
  produces the `list[dict[str, object]]` accepted by `build_ru_qwen_corpus()`;
  the CLI uses that same interface.
