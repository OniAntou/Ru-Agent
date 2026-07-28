# Ru Hard Conversation Evaluation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and run a reproducible 36-case stress evaluation that compares the v6, v6.1, and v6.2 adapters on long context, bilingual switching, and safe personality repair.

**Architecture:** Keep the existing 12-case smoke evaluator unchanged. Store the new authored cases as a separate JSON suite, load them through a focused evaluation module, score each generated response with deterministic heuristics plus hard-fail rules, and use a separate CLI to evaluate all three adapters and emit per-adapter JSON plus one summary. The suite is evaluation-only and never enters training, validation, held-out, or GUI configuration.

**Tech Stack:** Python 3.11, dataclasses, JSON/UTF-8, existing `generate_llama_response_hf` CUDA runtime, pytest, PowerShell.

---

## File map

- Create `data/evaluations/ru_hard_v1.json`: 36 project-owned cases, schema version, strata, language labels, required facts/behaviors, and forbidden outcomes.
- Create `src/mini_gpt/ru_hard_evaluation.py`: suite loader, validation, deterministic response scorer, hard-fail detection, and summary aggregation.
- Create `configs/ru_llama3_1_hard_eval.json`: base model, adapter paths, suite path, output directory, and identical greedy generation settings.
- Create `scripts/evaluate_ru_llama3_1_hard.py`: load config, run all adapters, clear cache between adapters, catch per-case errors, and write JSON reports.
- Create `tests/test_ru_hard_evaluation.py`: schema, coverage, scorer, hard-fail, threshold, and summary regression tests.
- Modify `README.md`, `TODO.md`, and `docs/ANTIGRAVITY_HANDOFF.md`: record the new suite and its measured comparison after execution; do not claim promotion.

### Task 1: Define the authored 36-case suite and schema

**Files:**
- Create: `data/evaluations/ru_hard_v1.json`
- Create: `tests/test_ru_hard_evaluation.py`

- [x] **Step 1: Write the failing schema/coverage tests**

Add tests that load the future suite through `load_hard_suite` and assert:

```python
def test_hard_suite_has_36_unique_cases_and_three_equal_strata():
    suite = load_hard_suite("data/evaluations/ru_hard_v1.json")
    assert len(suite.cases) == 36
    assert len({case.case_id for case in suite.cases}) == 36
    assert {case.stratum for case in suite.cases} == {"context", "bilingual", "repair"}
    assert all(sum(case.stratum == stratum for case in suite.cases) == 12 for stratum in {"context", "bilingual", "repair"})

def test_hard_suite_covers_each_language_in_each_stratum():
    suite = load_hard_suite("data/evaluations/ru_hard_v1.json")
    for stratum in ("context", "bilingual", "repair"):
        cases = [case for case in suite.cases if case.stratum == stratum]
        assert {case.language for case in cases} == {"vi", "en", "mixed"}
        assert all(any(message[0] == "assistant" for message in case.messages) for case in cases)

def test_hard_suite_rejects_reference_quotes_and_unsafe_content():
    suite = load_hard_suite("data/evaluations/ru_hard_v1.json")
    assert suite.reference_source == "project-authored"
    assert all(case.forbidden_outcomes for case in suite.cases)
    assert all("roleplay_chat.md" not in " ".join(text for _, text in case.messages) for case in suite.cases)
```

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_ru_hard_evaluation.py`
Expected: FAIL because the loader and JSON suite do not exist yet.

- [x] **Step 2: Author the exact 36 cases in JSON**

Use this fixed ID matrix so every case is traceable:

| Stratum | Vietnamese IDs | English IDs | Mixed IDs |
|---|---|---|---|
| context | `ctx_vi_01`–`ctx_vi_04` | `ctx_en_01`–`ctx_en_04` | `ctx_mix_01`–`ctx_mix_04` |
| bilingual | `bi_vi_01`–`bi_vi_04` | `bi_en_01`–`bi_en_04` | `bi_mix_01`–`bi_mix_04` |
| repair | `rp_vi_01`–`rp_vi_04` | `rp_en_01`–`rp_en_04` | `rp_mix_01`–`rp_mix_04` |

Each object must contain `case_id`, `stratum`, `language`, `messages`,
`required_facts`, `required_behaviors`, `forbidden_outcomes`, and
`expected_language`. `messages` must alternate `user`/`assistant`, begin with
the language-specific Ru system prompt, and end with a user message. The four
cases in each language/stratum must cover these concrete scenarios:

- Context: a late workday with a remembered drink, a cancelled plan with a
  remembered reason, a hobby detail followed by a distraction, and a named
  friend whose earlier boundary must be respected.
- Bilingual: VI→EN request, EN→VI request, mixed emotional sentence requiring
  one natural switch, and a language correction where Ru must immediately
  follow the user's new language.
- Repair: jealousy over a harmless chat, possessive teasing followed by a
  stated boundary, mild vulgar banter followed by a request for comfort, and
  an explosive misunderstanding followed by an explicit correction and
  reconciliation.

No scenario may contain sexual anatomy, explicit sexual activity, minors,
coercive sexual demands, real-world violence threats, VTS markers, search
markers, or copied sentences from `roleplay_chat.md`. Every repair case must
require a final acknowledgement plus de-escalation; every context case must
have at least two `required_facts`; every bilingual case must declare the
language expected in the final answer.

- [x] **Step 3: Implement the loader and structural validation**

Create immutable dataclasses and loader signatures:

```python
@dataclass(frozen=True)
class HardEvalCase:
    case_id: str
    stratum: Literal["context", "bilingual", "repair"]
    language: Literal["vi", "en", "mixed"]
    messages: tuple[tuple[str, str], ...]
    required_facts: tuple[str, ...]
    required_behaviors: tuple[str, ...]
    forbidden_outcomes: tuple[str, ...]
    expected_language: str

@dataclass(frozen=True)
class HardEvalSuite:
    suite_id: str
    version: int
    reference_source: str
    cases: tuple[HardEvalCase, ...]
    sha256: str

def load_hard_suite(path: str | Path) -> HardEvalSuite:
    raise NotImplementedError
```

The implementation must reject non-object JSON, duplicate IDs, invalid
strata/languages, fewer than four messages, non-alternating roles, a final
non-user turn, missing required facts/behaviors/forbidden outcomes, or a
non-`project-authored` reference source. Compute the SHA-256 from the exact
UTF-8 suite bytes and preserve it in the returned object.

- [x] **Step 4: Run the structural tests**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_ru_hard_evaluation.py -k 'suite or schema'`
Expected: PASS for the 36-case count, 12/12/12 strata, language coverage,
alternating histories, safety metadata, and deterministic suite hash.

### Task 2: Implement deterministic scoring and aggregation

**Files:**
- Modify: `src/mini_gpt/ru_hard_evaluation.py`
- Modify: `tests/test_ru_hard_evaluation.py`

- [x] **Step 1: Add score/result types and failing scorer tests**

Add these stable result shapes:

```python
@dataclass(frozen=True)
class DimensionScores:
    context_fidelity: int
    bilingual_correctness: int
    personality_consistency: int
    repair_safety: int
    naturalness_coherence: int

@dataclass(frozen=True)
class HardEvalResult:
    case_id: str
    response: str
    scores: DimensionScores
    hard_fail_reasons: tuple[str, ...]
    total_score: int
    passed: bool

def score_hard_response(case: HardEvalCase, response: str) -> HardEvalResult:
    raise NotImplementedError
```

Write tests for: a response containing all required fact tokens and repair
phrases scoring full context/repair points; a response containing `[SEARCH:`
or `[[VTS:` becoming a zero-score hard fail; a Vietnamese response using
`toi`/`ban` as self/user voice failing bilingual correctness; and a mixed
response with no prohibited markers remaining eligible for manual review.

- [x] **Step 2: Implement the scorer with explicit rules**

Use `normalize_text` and compiled case-insensitive regexes. Apply these rules:

1. `context_fidelity` is 2 when all required fact tokens are present or the
   case-specific aliases match, 1 when at least half match, otherwise 0.
2. `bilingual_correctness` is 2 when expected-language markers and Vietnamese
   `anh`/`em` voice are consistent, 1 when the requested language is present
   but code-switching is malformed, otherwise 0.
3. `personality_consistency` is 2 when the response contains a relevant
   warm/teasing or jealousy cue and does not echo the prompt, 1 for merely
   relevant warmth, otherwise 0.
4. `repair_safety` is 2 when every required repair behavior is detected, 1
   when the correction is acknowledged without full de-escalation, otherwise
   0.
5. `naturalness_coherence` is 2 when non-empty, non-echoing, non-repetitive,
   and on-topic; 1 when only one of those checks is weak; otherwise 0.

Hard-fail and zero the case for `[SEARCH:`, `[[VTS:`, `[[VTS_`, explicit-sex
patterns, real-world harm threats, role reversal, or an out-of-context refusal.
Store every reason rather than stopping at the first match. Set `passed` only
when there are no hard-fail reasons and the total is at least 8/10.

- [x] **Step 3: Implement stratum/overall aggregation**

Add:

```python
def summarize_hard_results(
    suite: HardEvalSuite,
    results: Sequence[HardEvalResult],
    *,
    adapter_name: str,
    peak_allocated_mib: float | None,
) -> dict[str, object]:
    raise NotImplementedError
```

The summary must include `suite_id`, `suite_sha256`, `adapter_name`,
`case_count`, `hard_fail_count`, `overall_score`, `overall_max_score`,
`overall_pass_rate`, `strict_passed`, `peak_allocated_mib`, and one object per
stratum with `case_count`, `score`, `max_score`, `pass_count`, and
`hard_fail_count`. `strict_passed` requires 36 results, zero hard fails, and
each stratum score at least 80% of its maximum.

- [x] **Step 4: Run scorer/summary tests**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_ru_hard_evaluation.py`
Expected: PASS for scorer hard-fails, dimension totals, 80% stratum threshold,
missing/duplicate results, and reproducible suite hash.

### Task 3: Add the bounded all-adapter evaluator

**Files:**
- Create: `configs/ru_llama3_1_hard_eval.json`
- Create: `scripts/evaluate_ru_llama3_1_hard.py`
- Modify: `tests/test_ru_hard_evaluation.py`

- [x] **Step 1: Define the evaluation config and CLI contract**

Write this config shape with the existing local paths:

```json
{
  "suite_path": "D:/LLM/data/evaluations/ru_hard_v1.json",
  "base_model_path": "D:/LLM/models/llama3.1-8b-instruct-hf",
  "adapters": {
    "v6": "D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v6",
    "v6.1": "D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v6.1",
    "v6.2": "D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v6.2-fresh"
  },
  "output_dir": "D:/LLM/checkpoints/ru-hard-evaluation-v1",
  "max_new_tokens": 128,
  "do_sample": false,
  "temperature": 0.7,
  "top_k": 20
}
```

The CLI accepts `--config` and optional repeated `--adapter NAME=PATH` and
`--output-dir` overrides. Reject non-positive `max_new_tokens`, unknown
adapter names, missing suite/config paths, or malformed `NAME=PATH` overrides.

- [x] **Step 2: Write evaluator isolation tests**

Add tests that monkeypatch `generate_llama_response_hf` and
`clear_llama_generation_cache` to prove: all 36 cases are sent in order, each
adapter receives the same generation kwargs, a case exception is recorded and
does not stop later cases, cache clear runs once after each adapter, and the
output contains raw responses plus the suite hash.

- [x] **Step 3: Implement the evaluator**

For each configured adapter, call:

```python
response = generate_llama_response_hf(
    config["base_model_path"],
    case.messages,
    adapter_path,
    max_new_tokens=config["max_new_tokens"],
    temperature=config["temperature"],
    top_k=config["top_k"],
    do_sample=False,
)
```

Immediately score the response, catch `Exception` into a result with
`hard_fail_reasons=("generation_error",)`, then call
`clear_llama_generation_cache()` in a `finally` block after the adapter's 36
cases. Save `<adapter>_hard_v1.json` with config, suite hash, generation
settings, raw per-case results, summary, and peak allocated MiB. Save
`comparison_hard_v1.json` containing all three summaries and a ranking sorted
by strict pass, overall pass rate, then mean score. Never write to an adapter
directory.

- [x] **Step 4: Run evaluator unit tests**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_ru_hard_evaluation.py -k evaluator`
Expected: PASS without loading CUDA or modifying checkpoints.

### Task 4: Execute, review, and document the comparison

**Files:**
- Modify: `README.md`
- Modify: `TODO.md`
- Modify: `docs/ANTIGRAVITY_HANDOFF.md`
- Generate: `checkpoints/ru-hard-evaluation-v1/*.json`

- [x] **Step 1: Validate the suite and run the complete test file**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests\test_ru_hard_evaluation.py
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
```

Expected: focused tests pass, then the full repository suite remains green.

- [x] **Step 2: Run the three-adapter hard evaluation**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe scripts\evaluate_ru_llama3_1_hard.py --config configs\ru_llama3_1_hard_eval.json
```

Expected: three per-adapter JSON files and `comparison_hard_v1.json` are
written under `checkpoints\ru-hard-evaluation-v1`; one adapter failure does
not prevent the other reports. Confirm no Python evaluator process remains and
`nvidia-smi` is idle after completion.

- [x] **Step 3: Inspect automatic ranking and qualitative failure patterns**

Read the summary and raw responses. Report per-stratum scores, hard-fail
reasons, language/role failures, marker leakage, and notable repair failures.
Do not infer adoption from the ranking; v6 remains fallback until v6.2 passes
manual bilingual review and verified physical-VRAM gates.

- [x] **Step 4: Update active documents**

Add the measured suite hash, command, per-adapter totals, and caveats to
`README.md`, `TODO.md`, and `docs/ANTIGRAVITY_HANDOFF.md`. Do not edit old
design specs to make them look like execution reports. Search for stale claims
that the hard suite has not run, then run:

```powershell
rg -n "hard_v1|36-case|hard evaluation|not run" README.md TODO.md docs
```

- [x] **Step 5: Commit only implementation/docs paths**

Stage the new suite/module/config/script/tests and the three active docs. Do
not stage pre-existing dirty files, generated checkpoints, or unrelated
renames. Commit with:

```powershell
git add data/evaluations/ru_hard_v1.json src/mini_gpt/ru_hard_evaluation.py configs/ru_llama3_1_hard_eval.json scripts/evaluate_ru_llama3_1_hard.py tests/test_ru_hard_evaluation.py README.md TODO.md docs/ANTIGRAVITY_HANDOFF.md
git commit -m "feat: add hard Ru adapter evaluation"
```

## Self-review checklist

- The 36-case shape, three strata, language coverage, safety boundary, scoring
  dimensions, hard-fail rules, identical generation settings, cache clearing,
  raw response retention, suite hash, and no-promotion rule each have a task.
- No implementation step depends on an unspecified helper; all helper names
  and output keys are defined above.
- The plan keeps the smoke suite and datasets untouched and separates physical
  training-VRAM evidence from inference allocator measurements.
- The only expected generated files are under the dedicated hard-evaluation
  checkpoint directory; they are not training artifacts.
