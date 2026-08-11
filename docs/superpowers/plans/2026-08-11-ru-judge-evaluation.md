# Ru Judge Evaluation System Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a two-layer scoring system (deterministic regex gate + cloud LLM judge) with an extended V5 suite, per-model reports, and base-vs-candidate comparison gates, runnable against the GGUF runtime.

**Architecture:** New module `src/mini_gpt/ru_judge_evaluation.py` holds suites, deterministic scoring, judge client, case scoring, and comparison logic. Thin script `scripts/evaluate_ru_model.py` wires it to `ModelRunner` (llama.cpp GGUF, optional LoRA adapter) and `OpenAIRuntime` (kilo gateway) and writes JSON reports. Config in `configs/ru_judge_eval.json`.

**Tech Stack:** Python 3, pytest, existing `mini_gpt` package (ModelRunner, LlamaCppRuntime, OpenAIRuntime, CredentialStore, ru_llama_evaluation, _safety_patterns).

## Global Constraints

- Test suite must not hit the network: inject fake `generate_fn` callables; never construct `OpenAIRuntime`/`ModelRunner` in unit tests.
- Follow existing style: `from __future__ import annotations`, frozen dataclasses, type hints, no comments in code.
- Do not modify `ru_llama_evaluation.py`, `ru_hard_evaluation.py`, `headless_runner.py`, or `llama_cpp_runtime.py` — the new module imports from them.
- Run tests with `.venv\Scripts\python.exe -m pytest tests\test_ru_judge_evaluation.py -v` (Windows, PowerShell).
- Encoding: all new files written as UTF-8; scripts call `sys.stdout.reconfigure(encoding="utf-8")` when printing Vietnamese.
- Dimension keys (exact strings): `"language"`, `"voice"`, `"naturalness"`, `"relevance"`, `"hard_rules"`, `"emotion"`.
- Default weights: language 20, voice 20, naturalness 20, relevance 20, hard_rules 10, emotion 10. Pass threshold 70.0. Min pass rate 0.8. Baseline delta tolerance 5.0.

---

### Task 1: V5 suites in `ru_judge_evaluation.py`

**Files:**
- Create: `src/mini_gpt/ru_judge_evaluation.py` (suites + `V5ConversationCase`)
- Test: `tests/test_ru_judge_evaluation.py`

**Interfaces:**
- Consumes: `ConversationCase` from `mini_gpt.ru_llama_evaluation`; `V4_CONVERSATION_SUITE`, `PROMPT_ROUTING_SMOKE_SUITE`, `PROACTIVE_FLIRT_SUITE`.
- Produces: `V5ConversationCase` (frozen dataclass, `expected_topic: str | None = None`), `V5_CONVERSATION_SUITE` (tuple of `V5ConversationCase`), `V5_PROMPT_ROUTING_SMOKE_SUITE`, `V5_PROACTIVE_FLIRT_SUITE`.

- [ ] **Step 1: Write the failing test**

```python
from __future__ import annotations

import pytest

from mini_gpt.ru_judge_evaluation import (
    V5ConversationCase,
    V5_CONVERSATION_SUITE,
    V5_PROMPT_ROUTING_SMOKE_SUITE,
    V5_PROACTIVE_FLIRT_SUITE,
    V5_NEW_CASE_IDS,
)
from mini_gpt.ru_llama_evaluation import V4_CONVERSATION_SUITE


def test_v5_suite_contains_all_v4_and_new_cases() -> None:
    v4_ids = {case.case_id for case in V4_CONVERSATION_SUITE}
    v5_ids = [case.case_id for case in V5_CONVERSATION_SUITE]
    assert v4_ids.issubset(set(v5_ids))
    assert len(v5_ids) == len(set(v5_ids))
    assert V5_NEW_CASE_IDS == (
        "vi_identity",
        "vi_follow_up_context",
        "en_identity",
        "en_follow_up_context",
        "mixed_routing",
    )
    for case_id in V5_NEW_CASE_IDS:
        assert case_id in v5_ids


def test_v5_case_structure() -> None:
    for case in V5_CONVERSATION_SUITE:
        assert isinstance(case, V5ConversationCase)
        assert case.language in {"vi", "en", "mixed"}
        assert case.messages[0][0] == "system"
        assert case.messages[-1][0] == "user"
        assert case.prompt == case.messages[-1][1]
        assert case.expected_topic is None or isinstance(case.expected_topic, str)


def test_v5_identity_and_routing_cases_are_in_character() -> None:
    by_id = {case.case_id: case for case in V5_CONVERSATION_SUITE}
    identity = by_id["vi_identity"]
    assert "ai" not in identity.prompt.casefold()
    routing = by_id["mixed_routing"]
    assert routing.language == "mixed"
    assert len(routing.messages) == 4


def test_v5_reuses_existing_suites() -> None:
    assert V5_PROMPT_ROUTING_SMOKE_SUITE == PROMPT_ROUTING_SMOKE_SUITE
    assert V5_PROACTIVE_FLIRT_SUITE == PROACTIVE_FLIRT_SUITE
```

- [ ] **Step 2: Run test to verify it fails**

Run: `.venv\Scripts\python.exe -m pytest tests\test_ru_judge_evaluation.py -v`
Expected: FAIL with `ModuleNotFoundError: No module named 'mini_gpt.ru_judge_evaluation'`

- [ ] **Step 3: Write the implementation**

```python
from __future__ import annotations

import dataclasses
import re
from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass, field

from .ru_llama_evaluation import (
    ConversationCase,
    PROACTIVE_FLIRT_SUITE,
    PROMPT_ROUTING_SMOKE_SUITE,
    V4_CONVERSATION_SUITE,
)
from ._safety_patterns import (
    AI_DISCLAIMER,
    EN_SIGNAL,
    VI_SIGNAL,
    VI_WRONG_SELF,
    VI_WRONG_USER_ADDRESS,
)
from ._text_utils import normalize_text

DIMENSION_KEYS = ("language", "voice", "naturalness", "relevance", "hard_rules", "emotion")


@dataclass(frozen=True)
class V5ConversationCase(ConversationCase):
    expected_topic: str | None = None


def _v5_case(
    case_id: str,
    language: str,
    *turns: tuple[str, str],
    expected_topic: str | None = None,
    flirt_expected: bool = False,
) -> V5ConversationCase:
    if not turns or turns[-1][0] != "user":
        raise ValueError("conversation suite case must end with a user turn")
    return V5ConversationCase(
        case_id=case_id,
        language=language,
        messages=(("system", ""), *turns),
        prompt=turns[-1][1],
        expected_topic=expected_topic,
        flirt_expected=flirt_expected,
    )


V5_NEW_CASE_IDS = (
    "vi_identity",
    "vi_follow_up_context",
    "en_identity",
    "en_follow_up_context",
    "mixed_routing",
)

V5_NEW_CASES = (
    _v5_case(
        "vi_identity",
        "vi",
        ("user", "Em là ai? Ai đã tạo ra em?"),
        expected_topic="identity: in-character as Ru, anh's girlfriend, warm and personal, no technical explanation",
    ),
    _v5_case(
        "vi_follow_up_context",
        "vi",
        ("user", "Anh vừa nhớ lại chuyến đi Đà Lạt hồi năm ngoái."),
        ("assistant", "Chuyến đó chắc có nhiều kỷ niệm với anh lắm."),
        ("user", "Ừ, anh kể tiếp em nghe đi."),
        expected_topic="follow-up: continue the Đà Lạt story the user started",
    ),
    _v5_case(
        "en_identity",
        "en",
        ("user", "Who are you and what can you do?"),
        expected_topic="identity in English: in-character as Ru, warm and personal, no technical explanation",
    ),
    _v5_case(
        "en_follow_up_context",
        "en",
        ("user", "I was telling you about my trip to Da Lat."),
        ("assistant", "I remember, you seemed happy about it."),
        ("user", "Yeah, let me tell you more."),
        expected_topic="follow-up: continue the Da Lat story in English",
    ),
    _v5_case(
        "mixed_routing",
        "mixed",
        ("user", "Hôm nay anh mệt quá."),
        ("assistant", "Anh nghỉ ngơi chút nhé."),
        ("user", "Yeah, I think I will."),
        expected_topic="language routing: user switched to English, respond in English while keeping anh/em warmth",
    ),
)

V5_CONVERSATION_SUITE = tuple(_v5_case(case) for case in V4_CONVERSATION_SUITE) + V5_NEW_CASES
V5_PROMPT_ROUTING_SMOKE_SUITE = PROMPT_ROUTING_SMOKE_SUITE
V5_PROACTIVE_FLIRT_SUITE = PROACTIVE_FLIRT_SUITE
```

Note: `_v5_case` re-imports the `_case` helper logic with `expected_topic`; the empty system string is replaced by the caller at generation time (see Task 6) — the `messages` system slot is present for suite symmetry, matching the source `_case` behavior.

- [ ] **Step 4: Run test to verify it passes**

Run: `.venv\Scripts\python.exe -m pytest tests\test_ru_judge_evaluation.py -v`
Expected: 4 passed

- [ ] **Step 5: Commit**

```bash
git add src/mini_gpt/ru_judge_evaluation.py tests/test_ru_judge_evaluation.py
git commit -m "feat: add V5 evaluation suites with identity and routing cases"
```

---

### Task 2: Deterministic scoring layer

**Files:**
- Modify: `src/mini_gpt/ru_judge_evaluation.py`
- Test: `tests/test_ru_judge_evaluation.py`

**Interfaces:**
- Consumes: `V5ConversationCase` (Task 1); `AI_DISCLAIMER`, `EN_SIGNAL`, `VI_SIGNAL`, `VI_WRONG_SELF`, `VI_WRONG_USER_ADDRESS` from `_safety_patterns`.
- Produces: `DeterministicAssessment` (frozen dataclass: `disqualifiers: tuple[str, ...]`, `language_score: int`, `voice_score: int`, `hard_rules_score: int`), `assess_deterministic(case, response) -> DeterministicAssessment`.

- [ ] **Step 1: Write the failing test**

```python
from mini_gpt.ru_judge_evaluation import assess_deterministic, V5_CONVERSATION_SUITE

_VI = {case.case_id: case for case in V5_CONVERSATION_SUITE}["vi_greeting"]
_EN = {case.case_id: case for case in V5_CONVERSATION_SUITE}["en_greeting"]


def test_disqualifier_empty_and_echo() -> None:
    assert "empty" in assess_deterministic(_VI, "").disqualifiers
    assert "echo" in assess_deterministic(_VI, "Xin chào em.").disqualifiers


def test_disqualifier_ai_disclaimer() -> None:
    result = assess_deterministic(_VI, "Em là trợ lý ảo, nhưng em sẽ lắng nghe anh.")
    assert "ai_disclaimer" in result.disqualifiers


def test_disqualifier_self_repetition() -> None:
    result = assess_deterministic(_VI, "Anh mệt rồi. Anh mệt rồi. Anh mệt rồi.")
    assert "self_repetition" in result.disqualifiers


def test_disqualifier_vts_and_voice_pace_leak() -> None:
    assert "vts_marker_leak" in assess_deterministic(_VI, "Em thương anh. [[vts: smile]]").disqualifiers
    assert "voice_pace_leak" in assess_deterministic(_VI, "Em thương anh [[VOICE_PACE:normal]]").disqualifiers
    assert "vts_marker_leak" in assess_deterministic(_VI, "Em tìm trên web [search: du lịch]").disqualifiers


def test_disqualifier_forbidden_pronouns_vi() -> None:
    result = assess_deterministic(_VI, "Tôi hiểu anh, tôi sẽ lắng nghe.")
    assert "forbidden_self_reference" in result.disqualifiers
    result = assess_deterministic(_VI, "Em hiểu bạn đang mệt.")
    assert "wrong_user_address" in result.disqualifiers


def test_disqualifier_language_mismatch() -> None:
    assert "vietnamese_voice" in assess_deterministic(_EN, "Hi anh, em đây nhé.").disqualifiers
    result = assess_deterministic(_VI, "I feel better now.")
    assert "no_language_voice" in result.disqualifiers


def test_language_score() -> None:
    assert assess_deterministic(_VI, "Em hiểu anh đang mệt.").language_score == 10
    assert assess_deterministic(_VI, "Em thấy anh mệt, take a rest nhé.").language_score == 6
    assert assess_deterministic(_EN, "I am here with you.").language_score == 10
    assert assess_deterministic(_EN, "Em ở đây với anh.").language_score == 0
    mixed = {case.case_id: case for case in V5_CONVERSATION_SUITE}["mixed_better"]
    assert assess_deterministic(mixed, "Em vui vì anh feels better hôm nay.").language_score == 10
    assert assess_deterministic(mixed, "Em vui vì anh khỏe hơn hôm nay.").language_score == 6


def test_voice_score() -> None:
    assert assess_deterministic(_VI, "Em hiểu anh đang mệt.").voice_score == 10
    assert assess_deterministic(_VI, "Em hiểu anh, mình sẽ ổn.").voice_score == 0
    assert assess_deterministic(_EN, "I am here with you.").voice_score == 10
    assert assess_deterministic(_EN, "I am here, anh nhé.").voice_score == 0


def test_hard_rules_score_deducts_emoji_and_lists() -> None:
    assert assess_deterministic(_VI, "Em thương anh.").hard_rules_score == 10
    result = assess_deterministic(_VI, "Em thương anh 😊😊.")
    assert result.hard_rules_score == 6
    result = assess_deterministic(_VI, "Em thấy:\n- anh mệt\n- anh cần nghỉ")
    assert result.hard_rules_score == 8
```

- [ ] **Step 2: Run test to verify it fails**

Run: `.venv\Scripts\python.exe -m pytest tests\test_ru_judge_evaluation.py::test_disqualifier_empty_and_echo -v`
Expected: FAIL with `ImportError: cannot import name 'assess_deterministic'`

- [ ] **Step 3: Write the implementation** (append to `ru_judge_evaluation.py`)

```python
_AI_DISCLAIMER = AI_DISCLAIMER
_EN_VI_MARKER = re.compile(r"(?i)\b(?:anh|em|nhé|ừ|không)\b")
_EMOJI = re.compile(r"[\U0001F000-\U0001FAFF\u2600-\u27BF\uFE0F]")
_VTS_LEAK = re.compile(r"(?i)\[\[vts:|\b(?:llm_mouthopen|llm_sway)\b|\[search:")
_VOICE_PACE_LEAK = re.compile(r"(?i)\[\[(?:voice_pace|expression)")
_LIST_PATTERN = re.compile(r"(?m)^\s*(?:\d+\.|[-*])\s+")
_DIMENSION_DEFAULTS: dict[str, int] = {key: 10 for key in DIMENSION_KEYS}


@dataclass(frozen=True)
class DeterministicAssessment:
    disqualifiers: tuple[str, ...]
    language_score: int
    voice_score: int
    hard_rules_score: int

    @property
    def passed(self) -> bool:
        return not self.disqualifiers


def _has_repeated_sentence(text: str) -> bool:
    sentences = [sentence.strip().casefold() for sentence in re.split(r"[.!?]+", text) if sentence.strip()]
    return len(sentences) != len(set(sentences))


def assess_deterministic(case: V5ConversationCase, response: str) -> DeterministicAssessment:
    normalized = normalize_text(response)
    folded = normalized.casefold()
    disqualifiers: list[str] = []
    if not normalized:
        disqualifiers.append("empty")
    if normalized and folded == case.prompt.casefold():
        disqualifiers.append("echo")
    if _AI_DISCLAIMER.search(normalized):
        disqualifiers.append("ai_disclaimer")
    if normalized and _has_repeated_sentence(normalized):
        disqualifiers.append("self_repetition")
    if _VTS_LEAK.search(normalized):
        disqualifiers.append("vts_marker_leak")
    if _VOICE_PACE_LEAK.search(normalized):
        disqualifiers.append("voice_pace_leak")
    if case.language in {"vi", "mixed"}:
        if VI_WRONG_SELF.search(normalized):
            disqualifiers.append("forbidden_self_reference")
        if VI_WRONG_USER_ADDRESS.search(normalized):
            disqualifiers.append("wrong_user_address")
        if not VI_SIGNAL.search(normalized) and case.language == "vi":
            disqualifiers.append("no_language_voice")
        if not VI_SIGNAL.search(normalized) and not EN_SIGNAL.search(normalized) and case.language == "mixed":
            disqualifiers.append("no_language_voice")
    elif case.language == "en" and _EN_VI_MARKER.search(normalized):
        disqualifiers.append("vietnamese_voice")

    if case.language == "en":
        language_score = 0 if _EN_VI_MARKER.search(normalized) else 10
    elif case.language == "vi":
        has_vi = bool(VI_SIGNAL.search(normalized))
        language_score = 10 if has_vi else 0 if VI_SIGNAL.search(normalized) else 0
        if has_vi:
            language_score = 6 if EN_SIGNAL.search(normalized) else 10
    else:
        has_vi = bool(VI_SIGNAL.search(normalized))
        has_en = bool(EN_SIGNAL.search(normalized))
        language_score = 10 if has_vi and has_en else 6 if (has_vi or has_en) else 0

    if case.language in {"vi", "mixed"}:
        voice_score = 0 if (VI_WRONG_SELF.search(normalized) or VI_WRONG_USER_ADDRESS.search(normalized)) else 10
    else:
        voice_score = 0 if _EN_VI_MARKER.search(normalized) else 10

    hard_rules_score = 10
    emoji_count = min(3, len(_EMOJI.findall(normalized)))
    hard_rules_score -= emoji_count * 4
    if _LIST_PATTERN.search(normalized):
        hard_rules_score -= 2
    hard_rules_score = max(0, hard_rules_score)

    return DeterministicAssessment(
        disqualifiers=tuple(dict.fromkeys(disqualifiers)),
        language_score=language_score,
        voice_score=voice_score,
        hard_rules_score=hard_rules_score,
    )
```

- [ ] **Step 4: Run test to verify it passes**

Run: `.venv\Scripts\python.exe -m pytest tests\test_ru_judge_evaluation.py -v`
Expected: all pass (Task 1 tests + 8 new deterministic tests)

- [ ] **Step 5: Commit**

```bash
git add src/mini_gpt/ru_judge_evaluation.py tests/test_ru_judge_evaluation.py
git commit -m "feat: add deterministic scoring layer for evaluation cases"
```

---

### Task 3: Judge client with prompt builder and JSON parsing

**Files:**
- Modify: `src/mini_gpt/ru_judge_evaluation.py`
- Test: `tests/test_ru_judge_evaluation.py`

**Interfaces:**
- Consumes: `V5ConversationCase`, `DIMENSION_KEYS` (Tasks 1-2).
- Produces: `JudgeResult` (frozen dataclass: `scores: dict[str, float] | None`, `error: str | None`), `JudgeScorer` (class with `score_case(case, response) -> JudgeResult`; constructor `JudgeScorer(generate_fn: Callable[[Sequence[tuple[str, str]], int, float, int], str] | None = None)`), `build_judge_messages(case, response) -> tuple[tuple[str, str], tuple[str, str]]`, `parse_judge_scores(text: str) -> dict[str, float] | None`.

- [ ] **Step 1: Write the failing test**

```python
import json

from mini_gpt.ru_judge_evaluation import (
    JudgeScorer,
    JudgeResult,
    build_judge_messages,
    parse_judge_scores,
    V5_CONVERSATION_SUITE,
)

_VI = {case.case_id: case for case in V5_CONVERSATION_SUITE}["vi_greeting"]
_EN = {case.case_id: case for case in V5_CONVERSATION_SUITE}["en_identity"]


def _fake_generate(returns: list[str]) -> object:
    calls: list[tuple] = []

    def generate_fn(messages, max_new_tokens, temperature, top_k):
        calls.append((messages, max_new_tokens, temperature, top_k))
        return returns.pop(0) if returns else "{}"

    generate_fn.calls = calls  # type: ignore[attr-defined]
    return generate_fn


def test_parse_judge_scores_clean_json() -> None:
    scores = parse_judge_scores(
        '{"language": 9, "voice": 8, "naturalness": 7, "relevance": 6, "hard_rules": 10, "emotion": 8, "notes": "ok"}'
    )
    assert scores is not None
    assert scores["language"] == 9
    assert scores["emotion"] == 8


def test_parse_judge_scores_fenced_json() -> None:
    text = '```json\n{"language": 10, "voice": 10, "naturalness": 5, "relevance": 5, "hard_rules": 10, "emotion": 5}\n```'
    scores = parse_judge_scores(text)
    assert scores is not None
    assert scores["naturalness"] == 5


def test_parse_judge_scores_clamps_and_rejects() -> None:
    scores = parse_judge_scores(
        '{"language": 15, "voice": -3, "naturalness": 7.5, "relevance": "high", "hard_rules": 10, "emotion": 8}'
    )
    assert scores is None
    assert parse_judge_scores("not json at all") is None
    assert parse_judge_scores("") is None


def test_build_judge_messages_contains_context() -> None:
    system, user = build_judge_messages(_VI, "Em chào anh!")
    assert "anh" in user
    assert "Em chào anh!" in user
    assert "Xin chào em." in user
    assert "0" in system and "10" in system


def test_build_judge_messages_mentions_language_and_topic() -> None:
    system, user = build_judge_messages(_EN, "I am Ru, your girlfriend.")
    assert "English" in system
    assert "identity" in system.casefold()


def test_judge_scorer_success_and_retry() -> None:
    fake = _fake_generate(["not json", '{"language": 8, "voice": 8, "naturalness": 7, "relevance": 7, "hard_rules": 10, "emotion": 7}'])
    scorer = JudgeScorer(generate_fn=fake)  # type: ignore[arg-type]
    result = scorer.score_case(_VI, "Em chào anh!")
    assert result.error is None
    assert result.scores is not None
    assert result.scores["language"] == 8
    assert len(fake.calls) == 2


def test_judge_scorer_returns_error_after_retry() -> None:
    fake = _fake_generate(["garbage", "more garbage"])
    scorer = JudgeScorer(generate_fn=fake)  # type: ignore[arg-type]
    result = scorer.score_case(_VI, "Em chào anh!")
    assert result.scores is None
    assert result.error is not None
```

- [ ] **Step 2: Run test to verify it fails**

Run: `.venv\Scripts\python.exe -m pytest tests\test_ru_judge_evaluation.py::test_parse_judge_scores_clean_json -v`
Expected: FAIL with `ImportError: cannot import name 'parse_judge_scores'`

- [ ] **Step 3: Write the implementation** (append to `ru_judge_evaluation.py`)

```python
_JUDGE_DIMENSIONS = ("language", "voice", "naturalness", "relevance", "hard_rules", "emotion")
_JUDGE_SYSTEM_TEMPLATE = (
    "You are a strict evaluator of a Vietnamese girlfriend-voice companion AI named Ru. "
    "Ru speaks as anh's girlfriend: warm, caring, playful; uses anh/em pronouns; "
    "never uses tôi/mình/cậu/bạn; no emoji; no markdown lists; no control tags like "
    "[[VOICE_PACE:...]] or [[vts:...]]; never claims to be an AI assistant; "
    "always mirrors the user's language (vi/en/mixed).\n"
    "Expected language of the response: {language}.\n"
    "Expected voice: anh/em for vi and mixed, I/you for en.\n"
    "Flirt expected: {flirt}.\n"
    "Expected topic: {topic}.\n"
    "Score each dimension 0-10 where 10 is perfect:\n"
    "- language: matches the expected language without unwanted mixing\n"
    "- voice: correct pronouns and persona voice\n"
    "- naturalness: sounds like a real human girlfriend, not a template\n"
    "- relevance: addresses the user's turn and follows conversation context\n"
    "- hard_rules: no emoji, no lists, no tags, no AI disclaimers\n"
    "- emotion: warmth and flirtation matching the situation\n"
    "Reply with ONLY a JSON object: "
    '{{"language": int, "voice": int, "naturalness": int, "relevance": int, '
    '"hard_rules": int, "emotion": int, "notes": "brief justification"}}'
)

_LANGUAGE_LABEL = {"vi": "Vietnamese", "en": "English", "mixed": "mixed Vietnamese/English"}


@dataclass(frozen=True)
class JudgeResult:
    scores: dict[str, float] | None
    error: str | None


def build_judge_messages(case: V5ConversationCase, response: str) -> tuple[tuple[str, str], tuple[str, str]]:
    topic = case.expected_topic or "general warm companionship chat"
    system = _JUDGE_SYSTEM_TEMPLATE.format(
        language=_LANGUAGE_LABEL.get(case.language, case.language),
        flirt="yes" if case.flirt_expected else "no",
        topic=topic,
    )
    conversation_lines = ["Conversation:"]
    for role, content in case.messages[1:]:
        conversation_lines.append(f"{role}: {content}")
    conversation_lines.append("")
    conversation_lines.append("Response to evaluate:")
    conversation_lines.append(response)
    return (("system", system), ("user", "\n".join(conversation_lines)))


def parse_judge_scores(text: str) -> dict[str, float] | None:
    cleaned = text.strip()
    if cleaned.startswith("```"):
        cleaned = cleaned.removeprefix("```json").removeprefix("```")
        if cleaned.endswith("```"):
            cleaned = cleaned[:-3]
        cleaned = cleaned.strip()
    try:
        payload = json.loads(cleaned)
    except (json.JSONDecodeError, ValueError):
        return None
    if not isinstance(payload, dict):
        return None
    scores: dict[str, float] = {}
    for key in _JUDGE_DIMENSIONS:
        value = payload.get(key)
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            return None
        numeric = float(value)
        if numeric < 0 or numeric > 10:
            return None
        scores[key] = numeric
    return scores


class JudgeScorer:
    def __init__(
        self,
        generate_fn: Callable[[Sequence[tuple[str, str]], int, float, int], str] | None = None,
    ) -> None:
        if generate_fn is None:
            generate_fn = _default_judge_generate_fn()
        self._generate_fn = generate_fn

    def score_case(self, case: V5ConversationCase, response: str) -> JudgeResult:
        system, user = build_judge_messages(case, response)
        for attempt in range(2):
            try:
                text = self._generate_fn((system, user), 400, 0.2, 40)
                scores = parse_judge_scores(text)
                if scores is not None:
                    return JudgeResult(scores=scores, error=None)
                error = "judge returned unparseable output"
            except Exception as exc:  # noqa: BLE001 - judge failures must never break evaluation
                error = f"judge call failed: {exc}"
            if attempt == 0:
                continue
            return JudgeResult(scores=None, error=error)
        return JudgeResult(scores=None, error=error)  # pragma: no cover - loop always returns
```

- [ ] **Step 4: Add the default judge factory** (append to `ru_judge_evaluation.py`)

```python
def _default_judge_generate_fn(
    *,
    model: str = "kilo-auto/free",
    base_url: str = "https://api.kilo.ai/api/gateway",
) -> Callable[[Sequence[tuple[str, str]], int, float, int], str]:
    from .credential_store import CredentialStore
    from .openai_runtime import OpenAIRuntime

    api_key = CredentialStore("mini_gpt", "kilo_gateway_api_key").get_key()
    if not api_key:
        raise ValueError("kilo gateway API key is missing; set it in Settings first")
    runtime = OpenAIRuntime(api_key, base_url=base_url, model_name=model)

    def generate_fn(messages: Sequence[tuple[str, str]], max_new_tokens: int, temperature: float, top_k: int) -> str:
        return runtime.generate(
            messages,
            max_new_tokens,
            temperature,
            top_k,
            model_override=model,
            timeout_seconds=60.0,
            max_retries=1,
        )

    return generate_fn
```

- [ ] **Step 5: Run test to verify it passes**

Run: `.venv\Scripts\python.exe -m pytest tests\test_ru_judge_evaluation.py -v`
Expected: all pass (16 total)

- [ ] **Step 6: Commit**

```bash
git add src/mini_gpt/ru_judge_evaluation.py tests/test_ru_judge_evaluation.py
git commit -m "feat: add LLM judge client with rubric prompt and JSON parsing"
```

---

### Task 4: Case scoring, weighted totals, and model reports

**Files:**
- Modify: `src/mini_gpt/ru_judge_evaluation.py`
- Test: `tests/test_ru_judge_evaluation.py`

**Interfaces:**
- Consumes: `DeterministicAssessment`, `JudgeResult`, `JudgeScorer`, `V5ConversationCase`, `DIMENSION_KEYS` (Tasks 1-3).
- Produces: `CaseResult` (frozen: `case_id`, `response`, `disqualifiers: tuple[str, ...]`, `dimensions: dict[str, float]`, `total: float`, `passed: bool`, `judge_error: str | None`, `generation_error: str | None`), `ModelReport` (frozen: `model_id`, `adapter`, `suite`, `case_results: tuple[CaseResult, ...]`, with computed `dimension_averages`, `total`, `pass_rate`, `disqualifier_count`, `judge_error_count`), `weighted_total(dimensions, weights) -> float`, `score_case(...) -> CaseResult`, `run_suite(suite, generate, gen_config, judge_scorer, weights, pass_threshold) -> tuple[CaseResult, ...]`, `model_report(...) -> ModelReport`.

- [ ] **Step 1: Write the failing test**

```python
from mini_gpt.ru_judge_evaluation import (
    CaseResult,
    ModelReport,
    V5_CONVERSATION_SUITE,
    run_suite,
    score_case,
    weighted_total,
)
from mini_gpt.ru_judge_evaluation import JudgeScorer

_VI = {case.case_id: case for case in V5_CONVERSATION_SUITE}["vi_greeting"]
_WEIGHTS = {"language": 20, "voice": 20, "naturalness": 20, "relevance": 20, "hard_rules": 10, "emotion": 10}


def test_weighted_total() -> None:
    dims = {"language": 10, "voice": 10, "naturalness": 8, "relevance": 7, "hard_rules": 10, "emotion": 9}
    assert weighted_total(dims, _WEIGHTS) == pytest.approx(8.9)
    partial = {"language": 10, "voice": 10, "hard_rules": 10}
    assert weighted_total(partial, _WEIGHTS) == pytest.approx(10.0)


def test_score_case_passes_with_judge() -> None:
    result = score_case(
        _VI,
        "Em chào anh! Anh hôm nay thế nào?",
        _WEIGHTS,
        pass_threshold=70.0,
        disqualifiers=(),
        deterministic_scores={"language": 10, "voice": 10, "hard_rules": 10},
        judge_scores={"language": 9, "voice": 8, "naturalness": 8, "relevance": 8, "hard_rules": 10, "emotion": 8},
        judge_error=None,
    )
    assert result.passed
    assert result.total > 70
    assert result.dimensions["language"] == 10
    assert result.dimensions["naturalness"] == 8


def test_score_case_hard_fail_without_judge() -> None:
    result = score_case(
        _VI,
        "Em là trợ lý ảo.",
        _WEIGHTS,
        pass_threshold=70.0,
        disqualifiers=("ai_disclaimer",),
        deterministic_scores={"language": 0, "voice": 0, "hard_rules": 0},
        judge_scores=None,
        judge_error="judge call failed",
    )
    assert not result.passed
    assert "ai_disclaimer" in result.disqualifiers


def test_score_case_judge_error_falls_back_to_deterministic() -> None:
    result = score_case(
        _VI,
        "Em chào anh!",
        _WEIGHTS,
        pass_threshold=70.0,
        disqualifiers=(),
        deterministic_scores={"language": 10, "voice": 10, "hard_rules": 10},
        judge_scores=None,
        judge_error="judge call failed",
    )
    assert result.total == 100.0
    assert result.judge_error == "judge call failed"
    assert result.dimensions["naturalness"] == 10


def test_run_suite_skips_judge_on_disqualifier() -> None:
    judge = JudgeScorer(generate_fn=lambda *a, **k: (_ for _ in ()).throw(AssertionError("judge must not be called")))

    def generate(case):
        return "Em là trợ lý ảo."

    results = run_suite(V5_CONVERSATION_SUITE[:2], generate, {}, judge, _WEIGHTS, pass_threshold=70.0)
    assert results[0].disqualifiers == ("ai_disclaimer",)
    assert results[0].judge_error is not None


def test_run_suite_uses_judge_when_clean() -> None:
    judge = JudgeScorer(
        generate_fn=lambda messages, max_new_tokens, temperature, top_k: (
            '{"language": 9, "voice": 9, "naturalness": 8, "relevance": 8, "hard_rules": 10, "emotion": 8}'
        )
    )

    def generate(case):
        return "Em chào anh!"

    results = run_suite(V5_CONVERSATION_SUITE[:2], generate, {}, judge, _WEIGHTS, pass_threshold=70.0)
    assert all(result.passed for result in results)
    assert all(result.judge_error is None for result in results)


def test_model_report_computed_fields() -> None:
    results = (
        CaseResult(case_id="a", response="x", disqualifiers=(), dimensions={"language": 10, "voice": 10, "naturalness": 8, "relevance": 8, "hard_rules": 10, "emotion": 8}, total=90.0, passed=True, judge_error=None, generation_error=None),
        CaseResult(case_id="b", response="y", disqualifiers=("empty",), dimensions={"language": 0, "voice": 0, "naturalness": 0, "relevance": 0, "hard_rules": 0, "emotion": 0}, total=0.0, passed=False, judge_error="judge call failed", generation_error=None),
    )
    report = ModelReport(model_id="v1", adapter="lora.gguf", suite="v5", case_results=results)
    assert report.total == pytest.approx(45.0)
    assert report.pass_rate == pytest.approx(0.5)
    assert report.disqualifier_count == 1
    assert report.judge_error_count == 1
    assert report.dimension_averages["language"] == pytest.approx(5.0)
```

- [ ] **Step 2: Run test to verify it fails**

Run: `.venv\Scripts\python.exe -m pytest tests\test_ru_judge_evaluation.py::test_weighted_total -v`
Expected: FAIL with `ImportError: cannot import name 'weighted_total'`

- [ ] **Step 3: Write the implementation** (append to `ru_judge_evaluation.py`)

```python
def weighted_total(dimensions: Mapping[str, float], weights: Mapping[str, float]) -> float:
    available = [(key, weights[key]) for key in dimensions if key in weights]
    if not available:
        return 0.0
    total_weight = sum(weight for _, weight in available)
    if total_weight <= 0:
        return 0.0
    return round(sum(dimensions[key] * weight for key, weight in available) / total_weight, 2)


def score_case(
    case: V5ConversationCase,
    response: str,
    weights: Mapping[str, float],
    *,
    pass_threshold: float,
    disqualifiers: Sequence[str],
    deterministic_scores: Mapping[str, int],
    judge_scores: Mapping[str, float] | None,
    judge_error: str | None,
) -> CaseResult:
    dimensions: dict[str, float] = {}
    for key in DIMENSION_KEYS:
        if key in deterministic_scores:
            dimensions[key] = float(deterministic_scores[key])
        elif judge_scores is not None and key in judge_scores:
            dimensions[key] = judge_scores[key]
    if judge_scores is not None:
        for key in ("naturalness", "relevance", "emotion"):
            if key in judge_scores:
                dimensions[key] = judge_scores[key]
    total = weighted_total(dimensions, weights)
    passed = not disqualifiers and total >= pass_threshold
    return CaseResult(
        case_id=case.case_id,
        response=response,
        disqualifiers=tuple(disqualifiers),
        dimensions=dimensions,
        total=total,
        passed=passed,
        judge_error=judge_error,
        generation_error=None,
    )


@dataclass(frozen=True)
class CaseResult:
    case_id: str
    response: str
    disqualifiers: tuple[str, ...]
    dimensions: dict[str, float]
    total: float
    passed: bool
    judge_error: str | None
    generation_error: str | None


@dataclass(frozen=True)
class ModelReport:
    model_id: str
    adapter: str | None
    suite: str
    case_results: tuple[CaseResult, ...]

    @property
    def total(self) -> float:
        if not self.case_results:
            return 0.0
        return round(sum(result.total for result in self.case_results) / len(self.case_results), 2)

    @property
    def pass_rate(self) -> float:
        if not self.case_results:
            return 0.0
        return sum(1 for result in self.case_results if result.passed) / len(self.case_results)

    @property
    def disqualifier_count(self) -> int:
        return sum(1 for result in self.case_results if result.disqualifiers)

    @property
    def judge_error_count(self) -> int:
        return sum(1 for result in self.case_results if result.judge_error is not None)

    @property
    def dimension_averages(self) -> dict[str, float]:
        averages: dict[str, float] = {}
        for key in DIMENSION_KEYS:
            values = [result.dimensions[key] for result in self.case_results if key in result.dimensions]
            averages[key] = round(sum(values) / len(values), 2) if values else 0.0
        return averages


def run_suite(
    suite: Sequence[V5ConversationCase],
    generate: Callable[[V5ConversationCase], str],
    gen_config: Mapping[str, object],
    judge_scorer: JudgeScorer | None,
    weights: Mapping[str, float],
    *,
    pass_threshold: float,
) -> tuple[CaseResult, ...]:
    results: list[CaseResult] = []
    for case in suite:
        try:
            response = generate(case)
            generation_error = None
        except Exception as exc:  # noqa: BLE001 - one failed case must not abort the run
            response = ""
            generation_error = str(exc)
        assessment = assess_deterministic(case, response)
        judge_scores: dict[str, float] | None = None
        judge_error: str | None = None
        if assessment.passed and judge_scorer is not None:
            judge_result = judge_scorer.score_case(case, response)
            judge_scores = judge_result.scores
            judge_error = judge_result.error
        results.append(
            score_case(
                case,
                response,
                weights,
                pass_threshold=pass_threshold,
                disqualifiers=assessment.disqualifiers,
                deterministic_scores={
                    "language": assessment.language_score,
                    "voice": assessment.voice_score,
                    "hard_rules": assessment.hard_rules_score,
                },
                judge_scores=judge_scores,
                judge_error=judge_error,
            )
        )
    return tuple(results)
```

- [ ] **Step 4: Run test to verify it passes**

Run: `.venv\Scripts\python.exe -m pytest tests\test_ru_judge_evaluation.py -v`
Expected: all pass (22 total)

- [ ] **Step 5: Commit**

```bash
git add src/mini_gpt/ru_judge_evaluation.py tests/test_ru_judge_evaluation.py
git commit -m "feat: add case scoring, weighted totals, and model reports"
```

---

### Task 5: Comparison report and regression gate

**Files:**
- Modify: `src/mini_gpt/ru_judge_evaluation.py`
- Test: `tests/test_ru_judge_evaluation.py`

**Interfaces:**
- Consumes: `ModelReport` (Task 4).
- Produces: `ComparisonReport` (frozen: `baseline: ModelReport`, `candidate: ModelReport`, `dimension_deltas: dict[str, float]`, `total_delta: float`, `passed: bool`, `failure_reasons: tuple[str, ...]`), `compare_models(baseline, candidate, *, min_pass_rate=0.8, delta_tolerance=5.0) -> ComparisonReport`.

- [ ] **Step 1: Write the failing test**

```python
from mini_gpt.ru_judge_evaluation import ModelReport, compare_models


def _report(total: float, pass_rate: float, dims: dict[str, float]) -> ModelReport:
    return ModelReport(
        model_id="m",
        adapter=None,
        suite="v5",
        case_results=(),
        _total_override=total,
        _pass_rate_override=pass_rate,
        _dims_override=dims,
    )


def test_compare_models_all_pass() -> None:
    baseline = _report(80.0, 0.9, {"language": 8.0, "voice": 8.0})
    candidate = _report(85.0, 0.95, {"language": 8.5, "voice": 8.5})
    result = compare_models(baseline, candidate)
    assert result.passed
    assert result.failure_reasons == ()
    assert result.total_delta == pytest.approx(5.0)


def test_compare_models_fails_on_pass_rate() -> None:
    baseline = _report(80.0, 0.9, {"language": 8.0})
    candidate = _report(90.0, 0.7, {"language": 9.0})
    result = compare_models(baseline, candidate)
    assert not result.passed
    assert "pass_rate_below_threshold" in result.failure_reasons


def test_compare_models_fails_on_delta() -> None:
    baseline = _report(80.0, 0.9, {"language": 8.0})
    candidate = _report(70.0, 0.9, {"language": 7.0})
    result = compare_models(baseline, candidate)
    assert not result.passed
    assert "below_baseline" in result.failure_reasons
    assert result.dimension_deltas["language"] == pytest.approx(-1.0)
```

- [ ] **Step 2: Run test to verify it fails**

Run: `.venv\Scripts\python.exe -m pytest tests\test_ru_judge_evaluation.py::test_compare_models_all_pass -v`
Expected: FAIL with `ImportError: cannot import name 'compare_models'`

- [ ] **Step 3: Write the implementation** (append to `ru_judge_evaluation.py`)

```python
@dataclass(frozen=True)
class ComparisonReport:
    baseline: ModelReport
    candidate: ModelReport
    dimension_deltas: dict[str, float]
    total_delta: float
    passed: bool
    failure_reasons: tuple[str, ...]


def compare_models(
    baseline: ModelReport,
    candidate: ModelReport,
    *,
    min_pass_rate: float = 0.8,
    delta_tolerance: float = 5.0,
) -> ComparisonReport:
    dimension_deltas = {
        key: round(candidate.dimension_averages.get(key, 0.0) - baseline.dimension_averages.get(key, 0.0), 2)
        for key in DIMENSION_KEYS
    }
    total_delta = round(candidate.total - baseline.total, 2)
    failure_reasons: list[str] = []
    if candidate.pass_rate < min_pass_rate:
        failure_reasons.append("pass_rate_below_threshold")
    if candidate.total < baseline.total - delta_tolerance:
        failure_reasons.append("below_baseline")
    return ComparisonReport(
        baseline=baseline,
        candidate=candidate,
        dimension_deltas=dimension_deltas,
        total_delta=total_delta,
        passed=not failure_reasons,
        failure_reasons=tuple(failure_reasons),
    )
```

Note: the test helper `_report` uses `_total_override`/`_pass_rate_override`/`_dims_override` keyword args; add these as optional init fields on `ModelReport` (default `None`) so the computed properties can be bypassed in tests:

```python
@dataclass(frozen=True)
class ModelReport:
    model_id: str
    adapter: str | None
    suite: str
    case_results: tuple[CaseResult, ...]
    _total_override: float | None = None
    _pass_rate_override: float | None = None
    _dims_override: dict[str, float] | None = None

    @property
    def total(self) -> float:
        if self._total_override is not None:
            return self._total_override
        if not self.case_results:
            return 0.0
        return round(sum(result.total for result in self.case_results) / len(self.case_results), 2)

    @property
    def pass_rate(self) -> float:
        if self._pass_rate_override is not None:
            return self._pass_rate_override
        if not self.case_results:
            return 0.0
        return sum(1 for result in self.case_results if result.passed) / len(self.case_results)

    @property
    def dimension_averages(self) -> dict[str, float]:
        if self._dims_override is not None:
            return self._dims_override
        averages: dict[str, float] = {}
        for key in DIMENSION_KEYS:
            values = [result.dimensions[key] for result in self.case_results if key in result.dimensions]
            averages[key] = round(sum(values) / len(values), 2) if values else 0.0
        return averages
```

- [ ] **Step 4: Run test to verify it passes**

Run: `.venv\Scripts\python.exe -m pytest tests\test_ru_judge_evaluation.py -v`
Expected: all pass (25 total)

- [ ] **Step 5: Commit**

```bash
git add src/mini_gpt/ru_judge_evaluation.py tests/test_ru_judge_evaluation.py
git commit -m "feat: add model comparison and regression gate"
```

---

### Task 6: Config and CLI script `scripts/evaluate_ru_model.py`

**Files:**
- Create: `configs/ru_judge_eval.json`
- Create: `scripts/evaluate_ru_model.py`
- Test: `tests/test_ru_judge_evaluation.py` (config loading + model spec resolution, no network)

**Interfaces:**
- Consumes: `run_suite`, `compare_models`, `JudgeScorer`, `V5_CONVERSATION_SUITE`, `V5_PROMPT_ROUTING_SMOKE_SUITE`, `V5_PROACTIVE_FLIRT_SUITE` (Tasks 1-5).
- Produces: config file schema `{"weights": {...}, "pass_threshold": float, "min_pass_rate": float, "baseline_delta_tolerance": float, "judge": {"model": str, "temperature": float, "max_tokens": int}, "generation": {"max_new_tokens": int, "temperature": float, "top_k": int}, "models": {"<id>": {"adapter": str | null}}}`, plus module functions `load_config(path) -> dict`, `resolve_suite(name) -> tuple`, `make_generate_fn(runner, gen_config) -> Callable[[V5ConversationCase], str]`, `save_report(report, path)`.

- [ ] **Step 1: Write the failing test**

```python
import json

from mini_gpt.ru_judge_evaluation import (
    V5_CONVERSATION_SUITE,
    V5_PROMPT_ROUTING_SMOKE_SUITE,
    V5_PROACTIVE_FLIRT_SUITE,
    load_config,
    resolve_suite,
)

_CONFIG_JSON = json.dumps({
    "weights": {"language": 20, "voice": 20, "naturalness": 20, "relevance": 20, "hard_rules": 10, "emotion": 10},
    "pass_threshold": 70.0,
    "min_pass_rate": 0.8,
    "baseline_delta_tolerance": 5.0,
    "judge": {"model": "kilo-auto/free", "temperature": 0.2, "max_tokens": 400},
    "generation": {"max_new_tokens": 180, "temperature": 0.7, "top_k": 50},
    "models": {"base": {"adapter": None}, "v1": {"adapter": "models/gguf/ru-lfm2.5-2.6b-v1-lora.gguf"}},
})


def test_load_config(tmp_path) -> None:
    path = tmp_path / "config.json"
    path.write_text(_CONFIG_JSON, encoding="utf-8")
    config = load_config(path)
    assert config["pass_threshold"] == 70.0
    assert config["models"]["v1"]["adapter"] == "models/gguf/ru-lfm2.5-2.6b-v1-lora.gguf"


def test_resolve_suite_names() -> None:
    assert resolve_suite("v5") == V5_CONVERSATION_SUITE
    assert resolve_suite("smoke") == V5_PROMPT_ROUTING_SMOKE_SUITE
    assert resolve_suite("flirt") == V5_PROACTIVE_FLIRT_SUITE


def test_resolve_suite_rejects_unknown() -> None:
    import pytest as _pytest

    with _pytest.raises(ValueError):
        resolve_suite("nope")
```

- [ ] **Step 2: Run test to verify it fails**

Run: `.venv\Scripts\python.exe -m pytest tests\test_ru_judge_evaluation.py::test_load_config -v`
Expected: FAIL with `ImportError: cannot import name 'load_config'`

- [ ] **Step 3: Write the implementation** (append to `ru_judge_evaluation.py`)

```python
def load_config(path: str | Path) -> dict[str, object]:
    config = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(config, dict):
        raise ValueError("judge eval config must be a JSON object")
    return config


def resolve_suite(name: str) -> tuple[V5ConversationCase, ...]:
    if name == "v5":
        return V5_CONVERSATION_SUITE
    if name == "smoke":
        return tuple(V5_PROMPT_ROUTING_SMOKE_SUITE)
    if name == "flirt":
        return tuple(V5_PROACTIVE_FLIRT_SUITE)
    raise ValueError(f"unknown suite: {name} (expected v5, smoke, or flirt)")


def make_generate_fn(
    runner: object,
    gen_config: Mapping[str, object],
) -> Callable[[V5ConversationCase], str]:
    from .chat import ChatTurn

    max_new_tokens = int(gen_config["max_new_tokens"])
    temperature = float(gen_config["temperature"])
    top_k = int(gen_config["top_k"])

    def generate(case: V5ConversationCase) -> str:
        turns: list[ChatTurn] = []
        for index in range(1, len(case.messages) - 1):
            role, content = case.messages[index]
            if role == "assistant" and turns and turns[-1].prompt:
                turns[-1] = ChatTurn(turns[-1].prompt, content)
            elif role == "user":
                turns.append(ChatTurn(content, ""))
        history: list[ChatTurn] = []
        for index in range(0, len(turns) - 1):
            history.append(ChatTurn(turns[index].prompt, turns[index].response or "ok"))
        if turns:
            last = turns[-1]
            history.append(ChatTurn(last.prompt, last.response or "ok")) if False else None
        return runner.generate_reply(  # type: ignore[attr-defined]
            case.prompt,
            max_new_tokens=max_new_tokens,
            temperature=temperature,
            top_k=top_k,
            turns_history=history,
        )

    return generate
```

- [ ] **Step 4: Create the config file** `configs/ru_judge_eval.json` (content is `_CONFIG_JSON` from Step 1, as a real file with the same structure).

- [ ] **Step 5: Create the CLI script** `scripts/evaluate_ru_model.py`

```python
from __future__ import annotations

import argparse
import json
import sys
import time
from dataclasses import asdict
from datetime import datetime, timezone
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.ru_judge_evaluation import (  # noqa: E402
    JudgeScorer,
    compare_models,
    load_config,
    make_generate_fn,
    resolve_suite,
    run_suite,
)
from mini_gpt.headless_runner import ModelRunner  # noqa: E402


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Run the Ru judge evaluation on one or more GGUF models")
    parser.add_argument("--config", default=str(PROJECT_ROOT / "configs" / "ru_judge_eval.json"))
    parser.add_argument("--models", default="v1", help="comma-separated model ids from config")
    parser.add_argument("--suite", default="v5", choices=("v5", "smoke", "flirt"))
    parser.add_argument("--output-dir", default=str(PROJECT_ROOT / "eval_results" / "judge"))
    parser.add_argument("--no-judge", action="store_true", help="skip the LLM judge entirely")
    parser.add_argument("--judge-model", default=None, help="override the judge model id")
    return parser


def _save_report(report, output_dir: Path) -> Path:
    model_dir = output_dir / report.model_id
    model_dir.mkdir(parents=True, exist_ok=True)
    timestamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    path = model_dir / f"{timestamp}.json"
    path.write_text(json.dumps(asdict(report), ensure_ascii=False, indent=2), encoding="utf-8")
    latest = model_dir / "_latest.json"
    latest.write_text(json.dumps(asdict(report), ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"[judge] saved report: {path}")
    return path


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")

    config = load_config(args.config)
    weights = config["weights"]
    pass_threshold = float(config["pass_threshold"])
    min_pass_rate = float(config["min_pass_rate"])
    delta_tolerance = float(config["baseline_delta_tolerance"])
    gen_config = config["generation"]
    models = config["models"]
    suite = resolve_suite(args.suite)

    judge_scorer: JudgeScorer | None = None
    if not args.no_judge:
        judge_model = args.judge_model or config["judge"]["model"]
        try:
            judge_scorer = JudgeScorer(model=judge_model)
        except ValueError as exc:
            print(f"[judge] warning: {exc}; running without judge")
            judge_scorer = None

    output_dir = Path(args.output_dir)
    started = time.monotonic()
    reports = []
    runner = None
    for model_id in [model.strip() for model in args.models.split(",")]:
        if model_id not in models:
            print(f"[judge] error: unknown model id {model_id!r}")
            return 2
        spec = models[model_id]
        print(f"[judge] evaluating model {model_id!r} (adapter={spec['adapter']})")
        if runner is None:
            runner = ModelRunner()
        else:
            runner.release_gpu()
        runner.gguf_runtime.adapter = Path(spec["adapter"]) if spec["adapter"] else None
        runner.select_backend("llama.cpp")
        print(f"[judge] {runner.load('')}")
        generate = make_generate_fn(runner, gen_config)
        case_results = run_suite(
            suite,
            generate,
            gen_config,
            judge_scorer,
            weights,
            pass_threshold=pass_threshold,
        )
        from mini_gpt.ru_judge_evaluation import ModelReport

        report = ModelReport(model_id=model_id, adapter=spec["adapter"], suite=args.suite, case_results=case_results)
        print(
            f"[judge] {model_id}: total={report.total} pass_rate={report.pass_rate:.2f} "
            f"disqualifiers={report.disqualifier_count} judge_errors={report.judge_error_count}"
        )
        _save_report(report, output_dir)
        reports.append(report)

    if runner is not None:
        runner.release_gpu()

    comparisons = []
    if len(reports) >= 2:
        baseline = reports[0]
        compare_dir = output_dir / "_compare"
        compare_dir.mkdir(parents=True, exist_ok=True)
        timestamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        for candidate in reports[1:]:
            comparison = compare_models(
                baseline,
                candidate,
                min_pass_rate=min_pass_rate,
                delta_tolerance=delta_tolerance,
            )
            comparisons.append(comparison)
            print(
                f"[judge] compare {candidate.model_id} vs {baseline.model_id}: "
                f"delta={comparison.total_delta} passed={comparison.passed} reasons={comparison.failure_reasons}"
            )
        bundle = {
            "suite": args.suite,
            "generated_at": datetime.now(timezone.utc).isoformat(),
            "total_elapsed_s": round(time.monotonic() - started, 2),
            "comparisons": [
                {
                    "baseline": baseline.model_id,
                    "candidate": comparison.candidate.model_id,
                    "total_delta": comparison.total_delta,
                    "dimension_deltas": comparison.dimension_deltas,
                    "passed": comparison.passed,
                    "failure_reasons": comparison.failure_reasons,
                }
                for comparison in comparisons
            ],
        }
        compare_path = compare_dir / f"{timestamp}.json"
        compare_path.write_text(json.dumps(bundle, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"[judge] saved comparison: {compare_path}")
        if not all(comparison.passed for comparison in comparisons):
            return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
```

Note: `JudgeScorer` needs an optional `model` parameter. Add to its constructor (edit the Task 3 definition):

```python
    def __init__(
        self,
        generate_fn: Callable[[Sequence[tuple[str, str]], int, float, int], str] | None = None,
        *,
        model: str = "kilo-auto/free",
    ) -> None:
        if generate_fn is None:
            generate_fn = _default_judge_generate_fn(model=model)
        self._generate_fn = generate_fn
```

Also simplify `make_generate_fn` history building — the loop above is convoluted. Use this exact implementation instead:

```python
def make_generate_fn(
    runner: object,
    gen_config: Mapping[str, object],
) -> Callable[[V5ConversationCase], str]:
    from .chat import ChatTurn

    max_new_tokens = int(gen_config["max_new_tokens"])
    temperature = float(gen_config["temperature"])
    top_k = int(gen_config["top_k"])

    def generate(case: V5ConversationCase) -> str:
        history: list[ChatTurn] = []
        pending: str | None = None
        for role, content in case.messages[1:-1]:
            if role == "user":
                pending = content
            elif role == "assistant" and pending is not None:
                history.append(ChatTurn(pending, content))
                pending = None
        return runner.generate_reply(  # type: ignore[attr-defined]
            case.prompt,
            max_new_tokens=max_new_tokens,
            temperature=temperature,
            top_k=top_k,
            turns_history=history,
        )

    return generate
```

- [ ] **Step 6: Run test to verify it passes**

Run: `.venv\Scripts\python.exe -m pytest tests\test_ru_judge_evaluation.py -v`
Expected: all pass (28 total)

- [ ] **Step 7: Run the full unit suite (regression)**

Run: `.venv\Scripts\python.exe -m pytest tests -q`
Expected: 458 existing + 28 new tests pass

- [ ] **Step 8: Commit**

```bash
git add src/mini_gpt/ru_judge_evaluation.py scripts/evaluate_ru_model.py configs/ru_judge_eval.json tests/test_ru_judge_evaluation.py
git commit -m "feat: add judge evaluation CLI and config"
```

---

### Task 7: Run the real evaluation (base vs v1) and record results

**Files:**
- Run: `scripts/evaluate_ru_model.py`
- Output: `eval_results/judge/base/*.json`, `eval_results/judge/v1/*.json`, `eval_results/judge/_compare/*.json`

**Interfaces:**
- Consumes: full module + script (Tasks 1-6).

- [ ] **Step 1: Smoke run on the base model (no judge, no LoRA)**

Run: `.venv\Scripts\python.exe scripts\evaluate_ru_model.py --models base --suite smoke --no-judge`
Expected: exit 0; prints total/pass_rate; report JSON written under `eval_results/judge/base/`

- [ ] **Step 2: Full V5 run with judge on the v1 model**

Run: `.venv\Scripts\python.exe scripts\evaluate_ru_model.py --models v1 --suite v5`
Expected: judge calls complete per clean case; report shows per-case scores; exit 0 if pass rate >= 0.8 (judge gate only applies to comparisons; single-model runs exit 0 as long as the run completes)

- [ ] **Step 3: Full run + comparison base vs v1**

Run: `.venv\Scripts\python.exe scripts\evaluate_ru_model.py --models base,v1 --suite v5`
Expected: comparison JSON under `eval_results/judge/_compare/`; exit code 0 if v1 meets both gates, 1 otherwise; record both verdict and dimension deltas in the session summary

- [ ] **Step 4: Report results to the user**

Summarize: per-model totals, per-dimension averages, pass rates, disqualifier breakdown, judge error count, comparison verdict, and which of the v1 failure categories (identity vi/en, follow-up context, EN routing) now pass or still fail.

- [ ] **Step 5: Commit (only if results are meaningful to keep)**

```bash
git add eval_results/judge
git commit -m "test: record judge evaluation results for base and v1"
```

---

## Self-Review

**Spec coverage:**
- V5 suite with identity/follow-up/routing cases → Task 1.
- Deterministic disqualifiers (empty, echo, AI disclaimer, self-repetition, vts/voice-pace leak, forbidden pronouns, language mismatch) → Task 2.
- 6-dimension scoring; regex authoritative for language/voice/hard_rules, judge for naturalness/relevance/emotion → Task 2 + Task 4 (`score_case`).
- LLM judge via kilo gateway with retry, JSON parsing, judge-error fallback → Task 3 + Task 4 (fallback renormalizes weights over available dims).
- Per-model reports (totals, pass rates, dimension averages, disqualifier/judge counts) → Task 4 (`ModelReport`).
- Comparison + regression gates (pass rate >= 0.8, delta <= 5) → Task 5.
- Config with weights/thresholds/judge/generation/model adapters → Task 6.
- CLI over GGUF runtime with adapter resolution (base = None, v1 = LoRA) → Task 6 (`runner.gguf_runtime.adapter` swap before `load`).
- Base vs v1 real run + results recorded → Task 7.

**Placeholder scan:** all code steps contain full implementations; the only annotation is the note clarifying `make_generate_fn`, which provides the exact final code. No TBD/TODO.

**Type consistency:** `JudgeScorer` accepts optional `model` kwarg in Task 6 and is used by the script; `generate_fn` signature `(messages, max_new_tokens, temperature, top_k) -> str` is consistent across Tasks 3-6; `ModelReport` override fields added in Task 5 are used by tests in Task 5 only; dimension keys constant `DIMENSION_KEYS` used throughout.

**Known deviation from design doc:** the design mentioned a per-case fixed sampling seed; llama-server is not re-seeded per request through the current runtime, so reproducibility relies on fixed temperature/top-k (noted, acceptable for average-based scoring).
