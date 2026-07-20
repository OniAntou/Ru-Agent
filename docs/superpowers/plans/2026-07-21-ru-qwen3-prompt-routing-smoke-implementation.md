# Ru Qwen3 Prompt-Routing Smoke Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Test whether language-specific system prompts make the already local Qwen3-4B base follow Vietnamese, English, and mixed conversations naturally.

**Architecture:** A pure prompt-policy function selects a system instruction from the evaluator's declared case language. A smoke evaluator reuses eight fixed cases and the existing Qwen3 generation runtime; it writes a separate ignored artifact and never touches model weights.

**Tech Stack:** Python 3.11, pytest, PyTorch, Transformers, existing Qwen3-4B 4-bit local runtime.

---

## Task 1: Add a testable language-specific Ru prompt policy

**Files:**

- Modify: src/mini_gpt/ru_style.py
- Create: tests/test_ru_style.py

- [x] **Step 1: Write failing public-policy tests.**

Create tests that assert:

~~~python
assert ru_system_prompt_for("vi") == "You are Ru. Reply only in natural Vietnamese. Call the user anh and refer to yourself as em."
assert ru_system_prompt_for("en") == "You are Ru. Reply only in natural English. Never use Vietnamese words or Vietnamese forms of address."
assert ru_system_prompt_for("mixed") == "You are Ru, a friendly bilingual companion. Reply in a natural mix matching the user's language. When using Vietnamese address, call the user anh and refer to yourself as em."
with pytest.raises(ValueError, match="vi, en, or mixed"):
    ru_system_prompt_for("other")
~~~

Keep RU_SYSTEM_PROMPT unchanged for prior corpus and baseline evidence compatibility.

- [x] **Step 2: Confirm RED.**

~~~powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_ru_style.py -q
~~~

Expected: ru_system_prompt_for cannot be imported.

- [x] **Step 3: Implement the immutable prompt policy.**

Add:

~~~python
RU_PROMPTS = {
    "vi": "You are Ru. Reply only in natural Vietnamese. Call the user anh and refer to yourself as em.",
    "en": "You are Ru. Reply only in natural English. Never use Vietnamese words or Vietnamese forms of address.",
    "mixed": "You are Ru, a friendly bilingual companion. Reply in a natural mix matching the user's language. When using Vietnamese address, call the user anh and refer to yourself as em.",
}

def ru_system_prompt_for(language: str) -> str:
    try:
        return RU_PROMPTS[language]
    except KeyError as error:
        raise ValueError("language must be vi, en, or mixed") from error
~~~

- [x] **Step 4: Confirm GREEN and commit.**

~~~powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_ru_style.py -q
git add src\mini_gpt\ru_style.py tests\test_ru_style.py
git diff --cached --check
git commit -m "feat: add Ru language prompt policy"
~~~

## Task 2: Build the eight-case routed smoke evaluator

**Files:**

- Modify: src/mini_gpt/ru_qwen_evaluation.py
- Create: scripts/evaluate_ru_qwen_prompt_routing.py
- Create: scripts/record_ru_prompt_routing_smoke_review.py
- Modify: tests/test_ru_qwen_evaluation.py
- Modify: tests/test_evaluate_ru_qwen_script.py

- [x] **Step 1: Write failing suite and serialization tests.**

Add a test for PROMPT_ROUTING_SMOKE_SUITE asserting this exact ordered id tuple:

~~~python
(
    "vi_greeting",
    "vi_context_work",
    "en_greeting",
    "en_tired",
    "en_context_work",
    "en_small_talk",
    "mixed_better",
    "mixed_context",
)
~~~

For every case, assert its first message is system and equals ru_system_prompt_for(case.language). Add script-help tests for `evaluate_ru_qwen_prompt_routing.py` containing `Prompt-routing smoke` and `record_ru_prompt_routing_smoke_review.py` containing `Record a manual Ru prompt-routing smoke decision`.

Add pure-decision tests for `record_prompt_routing_smoke_decision` with an eight-case synthetic passing review, an English-language failure, and a role-reversal failure. Each routed review must include `case_id`, `natural`, `relevant`, `expected_language`, `voice`, `role_consistent`, and `notes`.

- [x] **Step 2: Confirm RED.**

~~~powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_ru_qwen_evaluation.py tests\test_evaluate_ru_qwen_script.py -q
~~~

Expected: smoke suite and script are absent.

- [x] **Step 3: Implement a routed copy helper and suite.**

In ru_qwen_evaluation.py, define:

~~~python
SMOKE_CASE_IDS = (
    "vi_greeting", "vi_context_work", "en_greeting", "en_tired",
    "en_context_work", "en_small_talk", "mixed_better", "mixed_context",
)

def routed_case(case: ConversationCase) -> ConversationCase:
    return ConversationCase(
        case_id=case.case_id,
        language=case.language,
        messages=(("system", ru_system_prompt_for(case.language)), *case.messages[1:]),
        prompt=case.prompt,
    )

PROMPT_ROUTING_SMOKE_SUITE = tuple(
    routed_case(next(case for case in V4_CONVERSATION_SUITE if case.case_id == case_id))
    for case_id in SMOKE_CASE_IDS
)
~~~

The helper uses existing test turns unchanged and changes only the system instruction.

Add a frozen `PromptRoutingReview` dataclass with the seven review fields above. Add `record_prompt_routing_smoke_decision(...)` beside the existing base-only recorder. It must reject duplicate or incomplete reviews, and it must return `phase: "prompt_routing_smoke"`, `manual_required_count: 7`, and these gates:

~~~python
manual_natural_count = sum(
    review.natural and review.relevant and review.expected_language and review.voice
    for review in reviews
)
english_language_gate_passed = all(
    review.natural and review.expected_language
    for review in reviews
    if review.case_id in {"en_greeting", "en_tired", "en_context_work", "en_small_talk"}
)
role_gate_passed = all(review.role_consistent for review in reviews)
~~~

The verdict is `passed` only when the automatic gate has zero failures, all four English cases pass the English language gate, every review passes the role gate, and `manual_natural_count >= 7`. Include explicit failure reasons for each failed gate.

- [x] **Step 4: Implement the smoke script.**

The script accepts --config and --output, defaults to configs/ru_qwen3_4b.json and checkpoints/ru-qwen3-4b/prompt_routing_smoke.json, resets CUDA peak memory, generates exactly PROMPT_ROUTING_SMOKE_SUITE, then writes:

~~~python
{
  "profile_name": config["profile_name"],
  "phase": "prompt_routing_smoke",
  "prompt_policy": "language_specific_v1",
  "base_model_id": profile.model_id,
  "cases": [
    {
      "case_id": case.case_id,
      "language": case.language,
      "system_prompt": case.messages[0][1],
      "prompt": case.prompt,
      "base_response": response,
      "base_assessment": {"passed": assessment.passed, "failures": list(assessment.failures)},
    }
  ],
  "peak_allocated_mib": bytes_to_mib(torch.cuda.max_memory_allocated()),
  "verdict": "not_reviewed"
}
~~~

Always clear the Qwen cache after all eight responses and use the same atomic partial-file replacement as the existing evaluator.

- [x] **Step 5: Implement the smoke decision recorder.**

Create `record_ru_prompt_routing_smoke_review.py` with the same `--evaluation`, mutually exclusive `--review` / `--automatic-only`, UTF-8 JSON validation, and atomic replacement conventions as `record_ru_qwen_base_review.py`. It parses `PromptRoutingReview`, invokes `record_prompt_routing_smoke_decision`, attaches `manual_review` when supplied, updates the evaluation's `decision` and `verdict`, then prints the decision JSON. Its parser description must contain `Record a manual Ru prompt-routing smoke decision`.

- [x] **Step 6: Confirm GREEN and commit.**

~~~powershell
& .\.venv\Scripts\python.exe -m pytest tests\test_ru_qwen_evaluation.py tests\test_evaluate_ru_qwen_script.py -q
git add src\mini_gpt\ru_qwen_evaluation.py scripts\evaluate_ru_qwen_prompt_routing.py scripts\record_ru_prompt_routing_smoke_review.py tests\test_ru_qwen_evaluation.py tests\test_evaluate_ru_qwen_script.py
git diff --cached --check
git commit -m "feat: add Ru prompt routing smoke"
~~~

## Task 3: Measure, review, and document the smoke result

**Files:**

- Create at runtime: checkpoints/ru-qwen3-4b/prompt_routing_smoke.json
- Create at runtime: checkpoints/ru-qwen3-4b/prompt_routing_smoke_review.json
- Modify: README.md
- Modify: TODO.md
- Modify: docs/superpowers/specs/2026-07-21-ru-qwen3-prompt-routing-smoke-design.md
- Modify: docs/superpowers/plans/2026-07-21-ru-qwen3-prompt-routing-smoke-implementation.md

- [x] **Step 1: Run the isolated smoke.**

~~~powershell
& .\.venv\Scripts\python.exe scripts\evaluate_ru_qwen_prompt_routing.py --config configs\ru_qwen3_4b.json --output checkpoints\ru-qwen3-4b\prompt_routing_smoke.json
~~~

Do not reuse or overwrite the previous 32-case Qwen3 artifact. Stop on OOM or peak allocated MiB above 5,500.


- [x] **Step 2: Perform all eight manual reviews and persist the decision.**

Write eight review objects with case_id, natural, relevant, expected_language, voice, role_consistent, and notes. Mark an English case expected_language false if it contains Vietnamese words or Vietnamese forms of address. Mark `role_consistent` false if Ru claims the user's experience as its own or reverses the speaker roles. Then run:

~~~powershell
& .\.venv\Scripts\python.exe scripts\record_ru_prompt_routing_smoke_review.py --evaluation checkpoints\ru-qwen3-4b\prompt_routing_smoke.json --review checkpoints\ru-qwen3-4b\prompt_routing_smoke_review.json
~~~

The recorder is the only authority for the pass/fail decision: it requires all four English cases to be natural English, no role reversal, and at least seven fully natural/relevant/right-language/right-voice rows.

- [x] **Step 3: Decide the next permitted action.**

If smoke fails, write not_adopted in the smoke JSON and stop: no 32-case rerun, fine-tuning, corpus, adapter, or GUI change.

If smoke passes, document that only a new approved plan may run the 32-case routed evaluation. Do not start it in this plan.

- [x] **Step 4: Run full regression and document real evidence.**

~~~powershell
& .\.venv\Scripts\python.exe -m pytest -q
rg -n "prompt.routing|Qwen3|Gemma|planned|not adopted|passed" README.md TODO.md docs
git diff --check
~~~

Record only actual responses, peak MiB, manual score, verdict, and the absence of training. Commit source and documentation separately with exact paths.
