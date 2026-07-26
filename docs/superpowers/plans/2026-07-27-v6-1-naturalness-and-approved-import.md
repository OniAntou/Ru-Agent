# v6.1 Naturalness Audit and Approved Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the authored v6.1 corpus measurably conversational, then add the three distinct user-approved pairs to its training split without contaminating validation or held-out data.

**Architecture:** A deterministic naturalness auditor examines all 2,400 authored rows before splitting and emits a reviewable JSON report. The source dialogue generator is tightened until that audit passes; the base corpus is rebuilt, then a separate idempotent importer appends only the selected reviewed pairs to `train.jsonl` and records provenance in the manifest.

**Tech Stack:** Python 3.13, standard-library JSON/collections/re/pathlib, pytest, existing corpus helpers.

---

## File structure

- `src/mini_gpt/ru_v6_1_naturalness.py` — pure audit functions and refusal rules for authored v6.1 rows.
- `scripts/audit_ru_v6_1_naturalness.py` — CLI that audits all authored rows and writes JSON.
- `src/mini_gpt/ru_v6_1_dialogues.py` — varied conversational response construction.
- `src/mini_gpt/ru_v6_1_corpus.py` — invokes the audit before deterministic splitting.
- `src/mini_gpt/approved_v6_1_import.py` — validates and atomically merges three approved rows.
- `scripts/merge_approved_into_ru_v6_1.py` — explicit train-only import command.
- `tests/test_ru_v6_1_naturalness.py`, `tests/test_approved_v6_1_import.py` — regression coverage.
- `README.md`, `TODO.md`, `docs/ANTIGRAVITY_HANDOFF.md` — current data status.

### Task 1: Define the full-corpus naturalness contract

**Files:**
- Create: `src/mini_gpt/ru_v6_1_naturalness.py`
- Create: `tests/test_ru_v6_1_naturalness.py`

- [ ] **Step 1: Write failing tests for AI-like wording and case-insensitive prompt duplication**

```python
from mini_gpt.ru_v6_1_naturalness import audit_rows, naturalness_rejection_reason

def _row(prompt, response):
    return {"messages": [{"role": "system", "content": "Ru"}, {"role": "user", "content": prompt}], "response": response}

def test_rejects_ai_meta_language():
    assert naturalness_rejection_reason("Anh thấy sao?", "As an AI language model, em không biết.") == "ai_meta_language"

def test_reports_casefold_prompt_collision():
    report = audit_rows([_row("Chào em", "Chào anh!"), _row("chào em", "Anh cần gì không?")])
    assert report["summary"]["casefold_prompt_collisions"] == ["chào em"]

def test_accepts_short_everyday_anh_em_reply():
    assert naturalness_rejection_reason("Anh mới về.", "Về rồi à anh, nghỉ một chút đã nhé.") is None
```

- [ ] **Step 2: Run the test to prove the module is absent**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_ru_v6_1_naturalness.py -q`

Expected: collection fails with `ModuleNotFoundError` for `ru_v6_1_naturalness`.

- [ ] **Step 3: Implement a deterministic, explainable audit**

```python
AI_META_PATTERNS = ("as an ai", "language model", "ai-generated", "prompt", "generated response")

def naturalness_rejection_reason(prompt: str, response: str) -> str | None:
    text = f"{prompt}\n{response}".casefold()
    if any(pattern in text for pattern in AI_META_PATTERNS):
        return "ai_meta_language"
    if not response.strip():
        return "empty_response"
    return None

def normalized_final_prompt(row: dict[str, object]) -> str:
    return normalize_text(str(row["messages"][-1]["content"])).casefold()
```

`audit_rows` must return a `summary` with row count, rejection count, repeated full-response count, casefold prompt collisions, response-opening frequencies, and per-language/mode/turn-band counts. Its `samples` must be deterministic and stratified by language, mode, and turn band; its `rejections` must record row index and reason.

- [ ] **Step 4: Run the unit tests**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_ru_v6_1_naturalness.py -q`

Expected: PASS.

- [ ] **Step 5: Commit the audit contract**

```powershell
git add src/mini_gpt/ru_v6_1_naturalness.py tests/test_ru_v6_1_naturalness.py
git commit -m "test: define v6.1 naturalness audit"
```

### Task 2: Remove formulaic source patterns and gate the builder

**Files:**
- Modify: `src/mini_gpt/ru_v6_1_dialogues.py`
- Modify: `src/mini_gpt/ru_v6_1_corpus.py`
- Modify: `tests/test_ru_v6_1_dialogues.py`
- Modify: `tests/test_ru_v6_1_corpus.py`

- [ ] **Step 1: Write failing source and builder checks**

```python
def test_v6_1_builder_rejects_casefold_prompt_collision(tmp_path, monkeypatch):
    import mini_gpt.ru_v6_1_corpus as corpus
    rows = [dict(row) for row in corpus.V6_1_ROWS]
    rows[1] = dict(rows[1])
    rows[1]["messages"] = [*rows[1]["messages"][:-1], {"role": "user", "content": rows[0]["messages"][-1]["content"].swapcase()}]
    monkeypatch.setattr(corpus, "V6_1_ROWS", rows)
    with pytest.raises(ValueError, match="casefold.*prompt"):
        corpus.build_ru_v6_1_corpus(tmp_path)
```

Add a source test calling `audit_rows(v6_1_dialogue_records())` and asserting 2,400 rows, zero rejections, zero casefold collisions, and zero repeated full replies.

- [ ] **Step 2: Prove the casefold test fails against the current exact-match-only guard**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_ru_v6_1_dialogues.py tests\test_ru_v6_1_corpus.py -q`

Expected: the new collision assertion fails.

- [ ] **Step 3: Make responses sound like ordinary chat**

Replace only the formulaic response pools in `ru_v6_1_dialogues.py`. Each pool must use varied short acknowledgements, concrete follow-ups, occasional no-question endings, and consistent Vietnamese `anh`/`em` voice. Keep all existing quotas, safety boundaries, bilingual distribution, contextual turns, and deterministic seed behavior. Do not emit the terms `AI`, `language model`, `prompt`, `natural`, or `generated` in a conversational row.

- [ ] **Step 4: Enforce audit results in the builder**

```python
from .ru_v6_1_naturalness import assert_authored_rows_are_natural, audit_rows, normalized_final_prompt

normalized_rows = [_normalize_row(row) for row in V6_1_ROWS]
assert_authored_rows_are_natural(normalized_rows)
prompts = [normalized_final_prompt(row) for row in normalized_rows]
if len(prompts) != len(set(prompts)):
    raise ValueError(f"{VERSION} corpus contains casefold duplicate final prompts")
naturalness = audit_rows(normalized_rows)
report["naturalness_audit"] = naturalness["summary"]
```

Use the same normalised prompt comparison when checking split separation.

- [ ] **Step 5: Run the full source suite**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_ru_v6_1_dialogues.py tests\test_ru_v6_1_corpus.py tests\test_ru_v6_1_naturalness.py -q`

Expected: PASS; 2,400 source rows, zero audit rejections/collisions/repeated full replies, and split sizes 1,680/360/360.

- [ ] **Step 6: Commit the authored-corpus change**

```powershell
git add src/mini_gpt/ru_v6_1_dialogues.py src/mini_gpt/ru_v6_1_corpus.py tests/test_ru_v6_1_dialogues.py tests/test_ru_v6_1_corpus.py
git commit -m "feat: harden v6.1 conversational naturalness"
```

### Task 3: Produce and review the complete audit report

**Files:**
- Create: `scripts/audit_ru_v6_1_naturalness.py`
- Create: `data/conversations/ru_v6_1/naturalness_audit.json`
- Modify: `README.md`

- [ ] **Step 1: Write a failing CLI smoke test**

```python
def test_naturalness_cli_writes_report(tmp_path):
    result = subprocess.run([sys.executable, "scripts/audit_ru_v6_1_naturalness.py", "--output", str(tmp_path / "audit.json")], check=True)
    report = json.loads((tmp_path / "audit.json").read_text(encoding="utf-8"))
    assert report["summary"]["row_count"] == 2400
    assert report["summary"]["rejection_count"] == 0
```

- [ ] **Step 2: Implement the explicit audit command**

```python
parser.add_argument("--output", type=Path, required=True)
report = audit_rows(v6_1_dialogue_records())
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
if report["summary"]["rejection_count"] or report["summary"]["casefold_prompt_collisions"]:
    raise SystemExit(1)
```

- [ ] **Step 3: Audit all 2,400 authored rows and inspect every stratified sample**

Run: `& D:\LLM\.venv\Scripts\python.exe scripts\audit_ru_v6_1_naturalness.py --output data\conversations\ru_v6_1\naturalness_audit.json`

Expected: exit code 0; `row_count=2400`, `rejection_count=0`, zero casefold collisions, and the sample includes every language/mode/turn-band combination. Inspect all samples and return to Task 2 if any sentence reads templated or robotic.

- [ ] **Step 4: Document the audit boundary and commit**

Add a README sentence that the authored base is audited before reviewed local pairs are merged into train.

```powershell
git add scripts/audit_ru_v6_1_naturalness.py data/conversations/ru_v6_1/naturalness_audit.json README.md
git commit -m "docs: publish v6.1 naturalness audit"
```

### Task 4: Import the exact three approved pairs into train only

**Files:**
- Create: `src/mini_gpt/approved_v6_1_import.py`
- Create: `scripts/merge_approved_into_ru_v6_1.py`
- Create: `tests/test_approved_v6_1_import.py`

- [ ] **Step 1: Write failing tests for the selected IDs, collisions, and idempotency**

```python
SELECTED_IDS = {
    "5012176f-d8cf-4fd7-bba0-f94bac083b78",
    "0e124abf-c87a-47e6-be7d-4c1b73c02c72",
    "6e7f8d38-3b8d-41e1-b77e-7410c8ab44c4",
}

def test_selected_rows_are_distinct_after_casefolding(approved_rows):
    rows, rejected = select_approved_rows(approved_rows)
    assert len(rows) == 3
    assert len({normalized_prompt(row) for row in rows}) == 3
    assert "f352af5f-e597-4a61-a6ba-00595049f5b2" in {item["id"] for item in rejected}
```

Add tests proving val and held-out remain byte-identical, a collision in any split raises before writing, and rerunning the merge produces byte-identical train and manifest files.

- [ ] **Step 2: Run the tests to confirm the module is missing**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_approved_v6_1_import.py -q`

Expected: collection fails with `ModuleNotFoundError` for `approved_v6_1_import`.

- [ ] **Step 3: Implement explicit atomic merge**

```python
def approved_row(prompt: str, response: str) -> dict[str, object]:
    return {
        "messages": [{"role": "system", "content": RU_SYSTEM_PROMPT}, {"role": "user", "content": prompt}],
        "response": response,
        "language": "vi", "mode": "user_approved_short_reply", "intensity": "light",
        "turn_band": "single_turn", "source": "user-approved-local", "license": "project-owned",
    }
```

Read `data/conversations/approved.jsonl` without editing it. Accept exactly the three IDs above, remove any old `user-approved-local` rows before appending, reject a normalised prompt collision against train/val/held-out, then write only train and manifest using sibling partial files plus `os.replace`. Manifest key `approved_import` must record selected/rejected IDs, source SHA-256, final train SHA-256, accepted count, and final split counts.

- [ ] **Step 4: Run the importer suite and commit**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_approved_v6_1_import.py tests\test_ru_v6_1_corpus.py -q`

Expected: PASS; test output has train=1,683, validation=360, held_out=360.

```powershell
git add src/mini_gpt/approved_v6_1_import.py scripts/merge_approved_into_ru_v6_1.py tests/test_approved_v6_1_import.py
git commit -m "feat: import reviewed v6.1 conversations"
```

### Task 5: Rebuild, merge, verify, and update current documents

**Files:**
- Modify: `data/conversations/ru_v6_1/train.jsonl`
- Modify: `data/conversations/ru_v6_1/val.jsonl`
- Modify: `data/conversations/ru_v6_1/held_out.jsonl`
- Modify: `data/conversations/ru_v6_1/manifest.json`
- Modify: `README.md`, `TODO.md`, `docs/ANTIGRAVITY_HANDOFF.md`

- [ ] **Step 1: Rebuild only after Task 3 passes**

Run: `& D:\LLM\.venv\Scripts\python.exe scripts\build_ru_llama_v6_1_data.py --output-dir data\conversations\ru_v6_1 --seed 42`

Expected: base manifest has 2,400 examples and 1,680/360/360 splits.

- [ ] **Step 2: Run the approved import**

Run: `& D:\LLM\.venv\Scripts\python.exe scripts\merge_approved_into_ru_v6_1.py --dataset-dir data\conversations\ru_v6_1 --approved-file data\conversations\approved.jsonl`

Expected: manifest has `accepted_count=3`; train=1,683; validation=360; held_out=360.

- [ ] **Step 3: Prove the train rows are compatible without training**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_ru_v6_1_dialogues.py tests\test_ru_v6_1_corpus.py tests\test_ru_v6_1_naturalness.py tests\test_approved_v6_1_import.py -q`

Run: `& D:\LLM\.venv\Scripts\python.exe -c "from pathlib import Path; import json; rows=[json.loads(line) for line in Path('data/conversations/ru_v6_1/train.jsonl').read_text(encoding='utf-8').splitlines()]; chosen=[row for row in rows if row['source']=='user-approved-local']; assert len(chosen)==3 and all(row['messages'][-1]['role']=='user' for row in chosen); print('approved train rows:', len(chosen))"`

Expected: all tests PASS and `approved train rows: 3`. Do not start training.

- [ ] **Step 4: Update docs and remove stale claims**

Document that v6.1 is a 2,400-row audited base plus three train-only reviewed imports; preserve the rule that preparation is not training or adoption. Remove every claim of four approved rows, 1,684 train rows, or an unaudited v6.1 corpus.

- [ ] **Step 5: Verify documentation and commit task-scoped files**

Run: `rg -n "four approved|1684|unaudited v6\.1|F5" README.md TODO.md docs`

Run: `git diff --check -- README.md TODO.md docs data/conversations/ru_v6_1 src/mini_gpt/ru_v6_1_naturalness.py src/mini_gpt/approved_v6_1_import.py scripts/audit_ru_v6_1_naturalness.py scripts/merge_approved_into_ru_v6_1.py tests/test_ru_v6_1_naturalness.py tests/test_approved_v6_1_import.py`

Expected: no stale dataset claims and no whitespace errors.

```powershell
git add README.md TODO.md docs data/conversations/ru_v6_1 src/mini_gpt/ru_v6_1_naturalness.py src/mini_gpt/approved_v6_1_import.py scripts/audit_ru_v6_1_naturalness.py scripts/merge_approved_into_ru_v6_1.py tests/test_ru_v6_1_naturalness.py tests/test_approved_v6_1_import.py
git commit -m "data: audit and extend v6.1 conversations"
```
