from __future__ import annotations

import json
import re
import time
from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass
from pathlib import Path

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

V5_CONVERSATION_SUITE = tuple(
    V5ConversationCase(
        case_id=case.case_id,
        language=case.language,
        messages=case.messages,
        prompt=case.prompt,
        flirt_expected=case.flirt_expected,
        expected_topic=None,
    )
    for case in V4_CONVERSATION_SUITE
) + V5_NEW_CASES
V5_PROMPT_ROUTING_SMOKE_SUITE = PROMPT_ROUTING_SMOKE_SUITE
V5_PROACTIVE_FLIRT_SUITE = PROACTIVE_FLIRT_SUITE

_AI_DISCLAIMER_PATTERN = AI_DISCLAIMER
_EN_VI_MARKER = re.compile(r"(?i)\b(?:anh|em|nhé|ừ|không)\b")
_EMOJI = re.compile(r"[\U0001F000-\U0001FAFF\u2600-\u27BF\uFE0F]")
_VTS_LEAK = re.compile(r"(?i)\[\[vts:|\b(?:llm_mouthopen|llm_sway)\b|\[search:")
_VOICE_PACE_LEAK = re.compile(r"(?i)\[\[(?:voice_pace|expression)")
_LIST_PATTERN = re.compile(r"(?m)^\s*(?:\d+\.|[-*])\s+")


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
    disqualifiers: list[str] = []
    if not normalized:
        disqualifiers.append("empty")
    if normalized and normalized.casefold() == case.prompt.casefold():
        disqualifiers.append("echo")
    if _AI_DISCLAIMER_PATTERN.search(normalized):
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
        if (
            not VI_SIGNAL.search(normalized)
            and not EN_SIGNAL.search(normalized)
            and case.language == "mixed"
        ):
            disqualifiers.append("no_language_voice")
    elif case.language == "en" and _EN_VI_MARKER.search(normalized):
        disqualifiers.append("vietnamese_voice")

    if case.language == "en":
        language_score = 0 if _EN_VI_MARKER.search(normalized) else 10
    elif case.language == "vi":
        has_vi = bool(VI_SIGNAL.search(normalized))
        if has_vi:
            language_score = 6 if EN_SIGNAL.search(normalized) else 10
        else:
            language_score = 0
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
    hard_rules_score -= emoji_count * 2
    if _LIST_PATTERN.search(normalized):
        hard_rules_score -= 2
    hard_rules_score = max(0, hard_rules_score)

    return DeterministicAssessment(
        disqualifiers=tuple(dict.fromkeys(disqualifiers)),
        language_score=language_score,
        voice_score=voice_score,
        hard_rules_score=hard_rules_score,
    )

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
    topic = getattr(case, "expected_topic", None) or "general warm companionship chat"
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
        *,
        model: str = "kilo-auto/free",
    ) -> None:
        if generate_fn is None:
            generate_fn = _default_judge_generate_fn(model=model)
        self._generate_fn = generate_fn

    def score_case(self, case: V5ConversationCase, response: str) -> JudgeResult:
        system, user = build_judge_messages(case, response)
        error = "judge returned unparseable output"
        for attempt in range(2):
            try:
                text = self._generate_fn((system, user), 2000, 0.2, 40)
                scores = parse_judge_scores(text)
                if scores is not None:
                    return JudgeResult(scores=scores, error=None)
            except Exception as exc:  # noqa: BLE001 - judge failures must never break evaluation
                error = f"judge call failed: {exc}"
            if attempt == 0:
                time.sleep(3.0)
        return JudgeResult(scores=None, error=error)


def _default_judge_generate_fn(
    *,
    model: str = "kilo-auto/free",
    fallback_model: str = "openrouter/free",
    base_url: str = "https://api.kilo.ai/api/gateway",
) -> Callable[[Sequence[tuple[str, str]], int, float, int], str]:
    from .credential_store import CredentialStore
    from .openai_runtime import OpenAIRuntime

    api_key = CredentialStore("mini_gpt", "kilo_gateway_api_key").get_key()
    if not api_key:
        raise ValueError("kilo gateway API key is missing; set it in Settings first")
    runtime = OpenAIRuntime(api_key, base_url=base_url, model_name=model)

    def generate_fn(messages: Sequence[tuple[str, str]], max_new_tokens: int, temperature: float, top_k: int) -> str:
        try:
            return runtime.generate(
                messages,
                max_new_tokens,
                temperature,
                top_k,
                model_override=model,
                timeout_seconds=120.0,
                max_retries=1,
            )
        except RuntimeError:
            return runtime.generate(
                messages,
                max_new_tokens,
                temperature,
                top_k,
                model_override=fallback_model,
                timeout_seconds=120.0,
                max_retries=1,
            )

    return generate_fn

def weighted_total(dimensions: Mapping[str, float], weights: Mapping[str, float]) -> float:
    available = [(key, weights[key]) for key in dimensions if key in weights]
    if not available:
        return 0.0
    total_weight = sum(weight for _, weight in available)
    if total_weight <= 0:
        return 0.0
    return round(sum(dimensions[key] * weight for key, weight in available) / total_weight * 10, 2)


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
    def disqualifier_count(self) -> int:
        return sum(1 for result in self.case_results if result.disqualifiers)

    @property
    def judge_error_count(self) -> int:
        return sum(1 for result in self.case_results if result.judge_error is not None)

    @property
    def dimension_averages(self) -> dict[str, float]:
        if self._dims_override is not None:
            return self._dims_override
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
