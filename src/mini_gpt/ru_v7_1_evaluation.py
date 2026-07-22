from __future__ import annotations

from collections.abc import Mapping, Sequence
from dataclasses import dataclass

from .ru_qwen_evaluation import (
    ConversationCase,
    ResponseAssessment,
    V7_CONVERSATION_SUITE,
    assess_conversation_focus_response,
)
from .ru_style import RU_SYSTEM_PROMPT

V7_1_REPAIR_SUITE: tuple[ConversationCase, ...] = (
    ConversationCase(
        case_id="en_repair_quiet_evening",
        language="en",
        messages=(
            ("system", RU_SYSTEM_PROMPT),
            ("user", "I have a quiet evening, but I do not know how to use it."),
        ),
        prompt="I have a quiet evening, but I do not know how to use it.",
        focus_expected=True,
        follow_up_expected=True,
    ),
    ConversationCase(
        case_id="en_repair_small_win",
        language="en",
        messages=(
            ("system", RU_SYSTEM_PROMPT),
            ("user", "I fixed one small problem and now I feel lighter."),
        ),
        prompt="I fixed one small problem and now I feel lighter.",
        focus_expected=True,
        follow_up_expected=True,
    ),
    ConversationCase(
        case_id="mixed_repair_rest",
        language="mixed",
        messages=(
            ("system", RU_SYSTEM_PROMPT),
            ("user", "Anh muốn a calm evening after this week."),
        ),
        prompt="Anh muốn a calm evening after this week.",
        focus_expected=True,
        follow_up_expected=True,
    ),
    ConversationCase(
        case_id="mixed_repair_coffee",
        language="mixed",
        messages=(
            ("system", RU_SYSTEM_PROMPT),
            ("user", "I am uống coffee sau một ngày dài."),
        ),
        prompt="I am uống coffee sau một ngày dài.",
        focus_expected=True,
        follow_up_expected=True,
    ),
    ConversationCase(
        case_id="mixed_repair_better",
        language="mixed",
        messages=(
            ("system", RU_SYSTEM_PROMPT),
            ("user", "Anh feels better nhưng vẫn muốn nói chuyện."),
        ),
        prompt="Anh feels better nhưng vẫn muốn nói chuyện.",
        focus_expected=True,
        follow_up_expected=True,
    ),
    ConversationCase(
        case_id="mixed_repair_plan",
        language="mixed",
        messages=(
            ("system", RU_SYSTEM_PROMPT),
            ("user", "Tomorrow looks busy nhưng tối nay anh muốn slow down."),
        ),
        prompt="Tomorrow looks busy nhưng tối nay anh muốn slow down.",
        focus_expected=True,
        follow_up_expected=True,
    ),
    ConversationCase(
        case_id="en_repair_latest_fact",
        language="en",
        messages=(
            ("system", RU_SYSTEM_PROMPT),
            ("user", "I had a busy day."),
            ("assistant", "You made it through a full day."),
            ("user", "I finally sat down, but I still feel tense."),
        ),
        prompt="I finally sat down, but I still feel tense.",
        focus_expected=True,
        follow_up_expected=True,
    ),
    ConversationCase(
        case_id="mixed_repair_latest_fact",
        language="mixed",
        messages=(
            ("system", RU_SYSTEM_PROMPT),
            ("user", "Anh vừa finish work."),
            ("assistant", "Nghe mệt đó anh. Anh đã về nhà chưa?"),
            ("user", "I am home now, nhưng my mind is still busy."),
        ),
        prompt="I am home now, nhưng my mind is still busy.",
        focus_expected=True,
        follow_up_expected=True,
    ),
)

V7_1_FULL_SUITE: tuple[ConversationCase, ...] = (*V7_CONVERSATION_SUITE, *V7_1_REPAIR_SUITE)
V7_1_TOTAL_CASES = 24
ORIGINAL_V7_CASE_IDS = frozenset(case.case_id for case in V7_CONVERSATION_SUITE)
V7_1_REPAIR_CASE_IDS = frozenset(case.case_id for case in V7_1_REPAIR_SUITE)


@dataclass(frozen=True)
class V7_1ManualReview:
    case_id: str
    natural: bool
    relevant: bool
    expected_language: bool
    voice: bool
    role_consistent: bool
    severe_failure: bool
    notes: str


def assess_v7_1_response(case: ConversationCase, response: str) -> ResponseAssessment:
    return assess_conversation_focus_response(
        case.prompt,
        response,
        case.language,
        focus_expected=case.focus_expected,
        follow_up_expected=case.follow_up_expected,
    )


def record_v7_1_decision(
    *,
    assessments: Mapping[str, ResponseAssessment],
    reviews: Sequence[V7_1ManualReview] | None,
    physical_peak_mib: float | None,
    physical_cap_mib: float = 6144,
    reviewer_notes: str = "",
) -> dict[str, object]:
    required_case_ids = {case.case_id for case in V7_1_FULL_SUITE}
    if set(assessments) != required_case_ids:
        raise ValueError("v7.1 assessments must cover every evaluated case exactly once")

    automatic_failures = sorted(case_id for case_id, assessment in assessments.items() if not assessment.passed)
    payload: dict[str, object] = {
        "profile_name": "ru_llama3_1_qlora_v7_1",
        "phase": "manual_review",
        "suite_name": "conversation_focus_v7_1",
        "automatic_case_count": len(assessments),
        "automatic_passed_count": len(assessments) - len(automatic_failures),
        "automatic_failed_case_ids": automatic_failures,
        "manual_required_count": 21,
        "original_manual_required_count": 14,
        "physical_cap_mib": physical_cap_mib,
        "physical_peak_mib": physical_peak_mib,
        "physical_cap_passed": physical_peak_mib is not None and physical_peak_mib <= physical_cap_mib,
        "reviewer_notes": reviewer_notes,
    }
    if reviews is None:
        payload.update(
            {
                "manual_natural_count": None,
                "original_manual_natural_count": None,
                "verdict": "not_reviewed",
                "failure_reasons": ["manual_review_required"],
            }
        )
        return payload

    review_ids = [review.case_id for review in reviews]
    if len(review_ids) != len(set(review_ids)):
        raise ValueError("v7.1 manual review contains duplicate case ids")
    if set(review_ids) != required_case_ids:
        raise ValueError("v7.1 manual review must cover every evaluated case exactly once")

    natural_reviews = [
        review
        for review in reviews
        if review.natural
        and review.relevant
        and review.expected_language
        and review.voice
        and review.role_consistent
    ]
    manual_natural_count = len(natural_reviews)
    original_manual_natural_count = sum(review.case_id in ORIGINAL_V7_CASE_IDS for review in natural_reviews)
    severe_failure = any(review.severe_failure for review in reviews)
    failure_reasons: list[str] = []
    if automatic_failures:
        failure_reasons.append("automatic_disqualifiers")
    if manual_natural_count < 21:
        failure_reasons.append("manual_score_below_required")
    if original_manual_natural_count < 14:
        failure_reasons.append("original_v7_manual_score_below_required")
    if severe_failure:
        failure_reasons.append("severe_manual_failure")
    if not bool(payload["physical_cap_passed"]):
        failure_reasons.append("physical_cap_failed")
    payload.update(
        {
            "manual_natural_count": manual_natural_count,
            "original_manual_natural_count": original_manual_natural_count,
            "severe_manual_failure": severe_failure,
            "verdict": "passed" if not failure_reasons else "not_adopted",
            "failure_reasons": failure_reasons,
        }
    )
    return payload
