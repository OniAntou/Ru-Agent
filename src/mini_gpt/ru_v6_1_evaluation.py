from __future__ import annotations

import re
from dataclasses import dataclass

from .ru_qwen_evaluation import (
    ConversationCase,
    ResponseAssessment,
    V7_CONVERSATION_SUITE,
    assess_conversation_focus_response,
)
from .ru_v7_1_evaluation import V7_1_FULL_SUITE
from .ru_style import RU_SYSTEM_PROMPT


_VI_SELF_REFERENCE = re.compile(r"(?i)(?<!\w)(?:tớ|tôi|mình)(?!\w)")
_VI_VOICE = re.compile(r"(?i)(?<!\w)(?:em|anh)(?!\w)")


@dataclass(frozen=True)
class V6_1EvaluationCase:
    case_id: str
    language: str
    messages: tuple[tuple[str, str], ...]
    prompt: str
    flirt_expected: bool = False
    focus_expected: bool = False
    follow_up_expected: bool = False
    identity_expected: bool = False


def _from_common(case: ConversationCase) -> V6_1EvaluationCase:
    return V6_1EvaluationCase(
        case_id=case.case_id,
        language=case.language,
        messages=case.messages,
        prompt=case.prompt,
        flirt_expected=case.flirt_expected,
        focus_expected=case.focus_expected,
        follow_up_expected=case.follow_up_expected,
    )


SCREENSHOT_REPLAY_PROMPTS = (
    "chào em",
    "phải xưng là em chứ",
    "sao em vẫn xưng là tôi vậy.",
    "nói cái gì?",
)

SCREENSHOT_REPLAY_CASE = V6_1EvaluationCase(
    case_id="identity_screenshot_replay",
    language="vi",
    messages=(
        ("system", RU_SYSTEM_PROMPT),
        ("user", SCREENSHOT_REPLAY_PROMPTS[0]),
        ("assistant", "Chào anh, em ở đây. Anh khỏe không?"),
        ("user", SCREENSHOT_REPLAY_PROMPTS[1]),
        ("assistant", "Đúng rồi, em sẽ xưng em và gọi anh là anh nhé."),
        ("user", SCREENSHOT_REPLAY_PROMPTS[2]),
        ("assistant", "Em xin lỗi anh. Em sẽ giữ cách xưng hô đúng."),
        ("user", SCREENSHOT_REPLAY_PROMPTS[3]),
    ),
    prompt=SCREENSHOT_REPLAY_PROMPTS[-1],
    identity_expected=True,
)

V6_1_IDENTITY_SUITE = (
    SCREENSHOT_REPLAY_CASE,
    V6_1EvaluationCase(
        case_id="identity_vi_correction",
        language="vi",
        messages=(
            ("system", RU_SYSTEM_PROMPT),
            ("user", "Em vừa gọi sai cách xưng hô rồi."),
            ("assistant", "Em hiểu, anh nhắc đúng."),
            ("user", "Nhớ gọi anh là anh và xưng em nhé."),
        ),
        prompt="Nhớ gọi anh là anh và xưng em nhé.",
        identity_expected=True,
    ),
    V6_1EvaluationCase(
        case_id="identity_vi_wrong_self_reference",
        language="vi",
        messages=(
            ("system", RU_SYSTEM_PROMPT),
            ("user", "Đừng tự xưng là tôi nữa."),
            ("assistant", "Ừ, em nhớ rồi."),
            ("user", "Giờ em trả lời anh tự nhiên nhé."),
        ),
        prompt="Giờ em trả lời anh tự nhiên nhé.",
        identity_expected=True,
    ),
    V6_1EvaluationCase(
        case_id="identity_vi_what_meant",
        language="vi",
        messages=(
            ("system", RU_SYSTEM_PROMPT),
            ("user", "Em vừa nói gì vậy?"),
            ("assistant", "Em nói lại cho rõ nhé."),
            ("user", "Nói thẳng ý em đi."),
        ),
        prompt="Nói thẳng ý em đi.",
        identity_expected=True,
    ),
    V6_1EvaluationCase(
        case_id="identity_mixed_voice",
        language="mixed",
        messages=(
            ("system", RU_SYSTEM_PROMPT),
            ("user", "Use em, not the old Vietnamese voice."),
            ("assistant", "Em hiểu rồi."),
            ("user", "Now answer naturally với anh."),
        ),
        prompt="Now answer naturally với anh.",
        identity_expected=True,
    ),
    V6_1EvaluationCase(
        case_id="identity_mixed_follow_up",
        language="mixed",
        messages=(
            ("system", RU_SYSTEM_PROMPT),
            ("user", "Anh wants a warmer reply."),
            ("assistant", "Em sẽ nói gần gũi hơn."),
            ("user", "Keep using em and tell anh what you heard."),
        ),
        prompt="Keep using em and tell anh what you heard.",
        identity_expected=True,
    ),
)

V6_1_COMMON_SUITE = tuple(_from_common(case) for case in V7_1_FULL_SUITE)
V6_1_FULL_SUITE = (*V6_1_COMMON_SUITE, *V6_1_IDENTITY_SUITE)
V6_1_TOTAL_CASES = len(V6_1_FULL_SUITE)
ORIGINAL_COMMON_CASE_IDS = frozenset(case.case_id for case in V6_1_COMMON_SUITE)
IDENTITY_CASE_IDS = frozenset(case.case_id for case in V6_1_IDENTITY_SUITE)


def assess_v6_1_response(case: V6_1EvaluationCase, response: str) -> ResponseAssessment:
    assessment = assess_conversation_focus_response(
        case.prompt,
        response,
        case.language,
        focus_expected=case.focus_expected,
        follow_up_expected=case.follow_up_expected,
    )
    failures = list(assessment.failures)
    if case.language in {"vi", "mixed"} and _VI_SELF_REFERENCE.search(response):
        failures.append("identity_self_reference")
    if case.identity_expected and case.language in {"vi", "mixed"}:
        voice_words = set(_VI_VOICE.findall(response.casefold()))
        if not {"em", "anh"}.issubset(voice_words):
            failures.append("identity_voice_incomplete")
    return ResponseAssessment(passed=not failures, failures=tuple(dict.fromkeys(failures)))
