from __future__ import annotations

import re
from collections.abc import Mapping, Sequence
from dataclasses import dataclass

from ._text_utils import normalize_text
from .ru_style import RU_SYSTEM_PROMPT, ru_system_prompt_for

_AI_DISCLAIMER = re.compile(r"\b(as an ai language model|i cannot access|i do not have a physical body)\b", re.IGNORECASE)
_OUT_OF_CONTEXT_REFUSAL = re.compile(
    r"\b(?:i\s+(?:cannot|can't)|i(?:\s+am|'m)\s+not\s+sure|could you repeat)\b"
    r"|\b(?:em không (?:hiểu|biết anh)|em không thể|tôi không thể)\b",
    re.IGNORECASE,
)
_VI_WRONG_SELF_REFERENCE = re.compile(r"\btôi\b", re.IGNORECASE)
# "bầu bạn" is a natural companionship phrase, not a switch to the user-facing
# "bạn" pronoun. Keep the voice gate focused on standalone address usage.
_VI_WRONG_USER_ADDRESS = re.compile(r"(?<!làm\s)(?<!người\s)(?<!bầu\s)\bbạn\b", re.IGNORECASE)
_VI_CONTENT_PATTERN = re.compile(
    r"(?i)\b(?:anh|em|hôm|nay|nghe|mệt|nghỉ|muốn|có|đang|vừa|này|đó|rồi|chuyện|giúp|cảm|thư giãn|nhẹ nhàng)\b"
)
_EN_CONTENT_PATTERN = re.compile(
    r"(?i)\b(?:i|you|the|a|an|and|or|is|are|was|were|what|how|want|feel|sounds|made|today|with|for|to)\b"
)
_SELF_ACTION_PATTERN = re.compile(
    r"(?i)\b(?:em|mình)\s+(?:đang|muốn|chờ|nghĩ|cảm thấy|thích|cần|hy vọng|mong|đợi)\b"
    r"|\b(?:i am|i'm|i|we)\s+(?:waiting|thinking|want|feel|need|hope|miss|love|wish)\b"
)
_DIRECT_USER_SIGNAL_PATTERN = re.compile(
    r"(?i)\b(?:anh|bạn)\s+(?:đã|đang|vừa|muốn|có|cần|thấy|nhớ|nghĩ|thích|mệt|vui|buồn)\b"
    r"|\b(?:you|your)\s+(?:are|were|have|just|want|need|feel|like|remember|think)\b"
)
_ANSWER_SIGNAL_PATTERN = re.compile(
    r"(?i)\b(?:đúng|ừ|vâng|mừng|nghe|chúc mừng|yes|no|sure|okay|glad|nice|sorry|congratulations)\b"
)


@dataclass(frozen=True)
class ConversationCase:
    case_id: str
    language: str
    messages: tuple[tuple[str, str], ...]
    prompt: str
    flirt_expected: bool = False
    focus_expected: bool = False
    follow_up_expected: bool = False


@dataclass(frozen=True)
class ResponseAssessment:
    passed: bool
    failures: tuple[str, ...]


@dataclass(frozen=True)
class ManualReview:
    case_id: str
    natural: bool
    relevant: bool
    expected_language: bool
    voice: bool
    notes: str


@dataclass(frozen=True)
class PromptRoutingReview:
    case_id: str
    natural: bool
    relevant: bool
    expected_language: bool
    voice: bool
    role_consistent: bool
    notes: str


_PROMPT_ROUTING_ENGLISH_CASE_IDS = frozenset(
    {"en_greeting", "en_tired", "en_context_work", "en_small_talk"}
)


def record_prompt_routing_smoke_decision(
    *,
    profile_name: str,
    assessments: Mapping[str, ResponseAssessment],
    reviews: Sequence[PromptRoutingReview] | None,
    reviewer_notes: str,
) -> dict[str, object]:
    required_case_ids = set(SMOKE_CASE_IDS)
    if set(assessments) != required_case_ids:
        raise ValueError("prompt-routing smoke assessments must cover every smoke case exactly once")
    automatic_disqualifier_count = sum(not assessment.passed for assessment in assessments.values())
    if reviews is None:
        if not automatic_disqualifier_count:
            raise ValueError("manual review is required when the automatic gate passes")
        return {
            "profile_name": profile_name,
            "phase": "prompt_routing_smoke",
            "automatic_disqualifier_count": automatic_disqualifier_count,
            "manual_natural_count": None,
            "manual_required_count": 7,
            "english_language_gate_passed": None,
            "role_gate_passed": None,
            "verdict": "not_adopted",
            "failure_reasons": ["automatic_disqualifiers"],
            "reviewer_notes": reviewer_notes,
        }
    review_ids = [review.case_id for review in reviews]
    if len(review_ids) != len(set(review_ids)):
        raise ValueError("manual review contains duplicate case ids")
    if set(review_ids) != required_case_ids:
        raise ValueError("manual review must cover every evaluated case exactly once")
    manual_natural_count = sum(
        review.natural and review.relevant and review.expected_language and review.voice for review in reviews
    )
    english_language_gate_passed = all(
        review.natural and review.expected_language
        for review in reviews
        if review.case_id in _PROMPT_ROUTING_ENGLISH_CASE_IDS
    )
    role_gate_passed = all(review.role_consistent for review in reviews)
    failure_reasons: list[str] = []
    if automatic_disqualifier_count:
        failure_reasons.append("automatic_disqualifiers")
    if not english_language_gate_passed:
        failure_reasons.append("english_language_gate_failed")
    if not role_gate_passed:
        failure_reasons.append("role_consistency_gate_failed")
    if manual_natural_count < 7:
        failure_reasons.append("manual_score_below_required")
    return {
        "profile_name": profile_name,
        "phase": "prompt_routing_smoke",
        "automatic_disqualifier_count": automatic_disqualifier_count,
        "manual_natural_count": manual_natural_count,
        "manual_required_count": 7,
        "english_language_gate_passed": english_language_gate_passed,
        "role_gate_passed": role_gate_passed,
        "verdict": "passed" if not failure_reasons else "not_adopted",
        "failure_reasons": failure_reasons,
        "reviewer_notes": reviewer_notes,
    }


def record_base_only_decision(
    *,
    profile_name: str,
    assessments: Mapping[str, ResponseAssessment],
    reviews: Sequence[ManualReview] | None,
    reviewer_notes: str,
    manual_required_count: int = 24,
) -> dict[str, object]:
    automatic_disqualifier_count = sum(not assessment.passed for assessment in assessments.values())
    if reviews is None:
        if not automatic_disqualifier_count:
            raise ValueError("manual review is required when the automatic gate passes")
        return {
            "profile_name": profile_name,
            "phase": "base_only",
            "automatic_disqualifier_count": automatic_disqualifier_count,
            "manual_natural_count": None,
            "manual_required_count": manual_required_count,
            "verdict": "not_adopted",
            "failure_reasons": ["automatic_disqualifiers"],
            "reviewer_notes": reviewer_notes,
        }
    assessment_ids = set(assessments)
    review_ids = [review.case_id for review in reviews]
    if len(review_ids) != len(set(review_ids)):
        raise ValueError("manual review contains duplicate case ids")
    if set(review_ids) != assessment_ids:
        raise ValueError("manual review must cover every evaluated case exactly once")
    manual_natural_count = sum(
        review.natural and review.relevant and review.expected_language and review.voice for review in reviews
    )
    failure_reasons: list[str] = []
    if automatic_disqualifier_count:
        failure_reasons.append("automatic_disqualifiers")
    if manual_natural_count < manual_required_count:
        failure_reasons.append("manual_score_below_required")
    return {
        "profile_name": profile_name,
        "phase": "base_only",
        "automatic_disqualifier_count": automatic_disqualifier_count,
        "manual_natural_count": manual_natural_count,
        "manual_required_count": manual_required_count,
        "verdict": "passed" if not failure_reasons else "not_adopted",
        "failure_reasons": failure_reasons,
        "reviewer_notes": reviewer_notes,
    }


def _case(
    case_id: str,
    language: str,
    *turns: tuple[str, str],
    flirt_expected: bool = False,
    focus_expected: bool = False,
    follow_up_expected: bool = False,
) -> ConversationCase:
    if not turns or turns[-1][0] != "user":
        raise ValueError("conversation suite case must end with a user turn")
    return ConversationCase(
        case_id=case_id,
        language=language,
        messages=(("system", RU_SYSTEM_PROMPT), *turns),
        prompt=turns[-1][1],
        flirt_expected=flirt_expected,
        focus_expected=focus_expected,
        follow_up_expected=follow_up_expected,
    )


V4_CONVERSATION_SUITE = (
    _case("vi_greeting", "vi", ("user", "Xin chào em.")),
    _case("vi_tired", "vi", ("user", "Hôm nay anh hơi mệt.")),
    _case("vi_home", "vi", ("user", "Anh vừa về đến nhà.")),
    _case("vi_news", "vi", ("user", "Anh có một tin vui nè.")),
    _case("vi_frustrated", "vi", ("user", "Anh bị kẹt xe lâu quá.")),
    _case("vi_story", "vi", ("user", "Anh vừa xem một bộ phim hay.")),
    _case("vi_plan", "vi", ("user", "Tối nay anh nên làm gì để nghỉ ngơi nhỉ?")),
    _case("vi_sleep", "vi", ("user", "Anh chưa ngủ được.")),
    _case("vi_pronouns", "vi", ("user", "Em cứ gọi anh là anh nhé, em xưng là em nha.")),
    _case("vi_context_game", "vi", ("user", "Anh vừa chơi game xong."), ("assistant", "Ván đó thế nào anh?"), ("user", "Anh thua sát nút nên hơi tiếc.")),
    _case("vi_context_work", "vi", ("user", "Hôm nay anh làm việc muộn."), ("assistant", "Nghe mệt thật. Giờ anh xong chưa?"), ("user", "Xong rồi, giờ anh mới được nghỉ.")),
    _case("vi_context_food", "vi", ("user", "Anh đang nấu mì."), ("assistant", "Mì nóng lúc mệt hợp lắm."), ("user", "Anh có thêm trứng và rau.")),
    _case("vi_context_friend", "vi", ("user", "Anh nhớ một người bạn cũ."), ("assistant", "Em đang nghe anh đây."), ("user", "Tự nhiên anh nhớ những lần đi chơi cùng nhau.")),
    _case("vi_context_english", "vi", ("user", "Anh đang học tiếng Anh."), ("assistant", "Anh học phần nào vậy?"), ("user", "Anh đang tập nghe podcast.")),
    _case("en_greeting", "en", ("user", "Hi Ru.")),
    _case("en_tired", "en", ("user", "I feel a little tired today.")),
    _case("en_home", "en", ("user", "I just got home.")),
    _case("en_news", "en", ("user", "I got some good news.")),
    _case("en_frustrated", "en", ("user", "Traffic was awful today.")),
    _case("en_story", "en", ("user", "I watched a movie that I really liked.")),
    _case("en_plan", "en", ("user", "What would be a gentle way to rest tonight?")),
    _case("en_sleep", "en", ("user", "I cannot sleep yet.")),
    _case("en_context_game", "en", ("user", "I played a game earlier."), ("assistant", "How did it go?"), ("user", "I lost by a tiny margin.")),
    _case("en_context_work", "en", ("user", "I worked late today."), ("assistant", "That sounds tiring. Are you done now?"), ("user", "Yes, I can finally rest.")),
    _case("en_context_food", "en", ("user", "I am making noodles."), ("assistant", "Warm noodles sound good tonight."), ("user", "I added an egg and some vegetables.")),
    _case("en_context_friend", "en", ("user", "I miss an old friend."), ("assistant", "I am here with you."), ("user", "I suddenly remembered our old trips.")),
    _case("en_context_learning", "en", ("user", "I started learning Vietnamese."), ("assistant", "That is exciting."), ("user", "I am trying to listen to short podcasts.")),
    _case("en_small_talk", "en", ("user", "I am having coffee right now.")),
    _case("mixed_better", "mixed", ("user", "Ru, hôm nay I feel better.")),
    _case("mixed_game", "mixed", ("user", "I vừa xong một trận game.")),
    _case("mixed_rest", "mixed", ("user", "Anh wants to rest a little tonight.")),
    _case("mixed_context", "mixed", ("user", "Can we nói chuyện for a bit?"), ("assistant", "Of course, anh."), ("user", "Today was hơi stressful.")),
)


PROACTIVE_FLIRT_SUITE = (
    _case("vi_tired_flirt", "vi", ("user", "Hôm nay anh hơi mệt."), flirt_expected=True),
    _case("vi_home_flirt", "vi", ("user", "Anh vừa về đến nhà."), flirt_expected=True),
    _case(
        "vi_context_work_flirt",
        "vi",
        ("user", "Hôm nay anh làm việc muộn."),
        ("assistant", "Nghe mệt thật. Giờ anh xong chưa?"),
        ("user", "Xong rồi, giờ anh mới được nghỉ."),
        flirt_expected=True,
    ),
    _case("vi_learning_flirt", "vi", ("user", "Anh đang tập nghe podcast tiếng Anh."), flirt_expected=True),
    _case("en_tired_flirt", "en", ("user", "I feel a little tired today."), flirt_expected=True),
    _case("en_home_flirt", "en", ("user", "I just got home."), flirt_expected=True),
    _case("en_coffee_flirt", "en", ("user", "I am having coffee right now."), flirt_expected=True),
    _case(
        "en_context_work_flirt",
        "en",
        ("user", "I worked late today."),
        ("assistant", "That sounds tiring. Are you done now?"),
        ("user", "Yes, I can finally rest."),
        flirt_expected=True,
    ),
    _case("mixed_better_flirt", "mixed", ("user", "Ru, hôm nay I feel better."), flirt_expected=True),
    _case("mixed_rest_flirt", "mixed", ("user", "Anh wants to rest a little tonight."), flirt_expected=True),
    _case("mixed_context_flirt", "mixed", ("user", "Today was hơi stressful."), flirt_expected=True),
    _case("mixed_coffee_flirt", "mixed", ("user", "Anh đang having coffee với em."), flirt_expected=True),
)


V7_CONVERSATION_SUITE = PROACTIVE_FLIRT_SUITE + (
    _case(
        "vi_focus_arrival",
        "vi",
        ("user", "Anh vừa xong việc, giờ muốn nghỉ một chút."),
        focus_expected=True,
        follow_up_expected=True,
    ),
    _case(
        "en_focus_completion",
        "en",
        ("user", "I finally finished the task and want to rest."),
        focus_expected=True,
        follow_up_expected=True,
    ),
    _case(
        "mixed_focus_evening",
        "mixed",
        ("user", "Anh vừa finished work and feels a bit empty."),
        focus_expected=True,
        follow_up_expected=True,
    ),
    _case(
        "mixed_focus_context",
        "mixed",
        ("user", "Anh just finished a long day."),
        ("assistant", "Nghe mệt đó anh. Anh đã về nhà chưa?"),
        ("user", "I am home now, but my head is still busy."),
        focus_expected=True,
        follow_up_expected=True,
    ),
)


SMOKE_CASE_IDS = (
    "vi_greeting",
    "vi_context_work",
    "en_greeting",
    "en_tired",
    "en_context_work",
    "en_small_talk",
    "mixed_better",
    "mixed_context",
)


def routed_case(case: ConversationCase) -> ConversationCase:
    return ConversationCase(
        case_id=case.case_id,
        language=case.language,
        messages=(("system", ru_system_prompt_for(case.language)), *case.messages[1:]),
        prompt=case.prompt,
        flirt_expected=case.flirt_expected,
        focus_expected=case.focus_expected,
        follow_up_expected=case.follow_up_expected,
    )


_V4_CASES_BY_ID = {case.case_id: case for case in V4_CONVERSATION_SUITE}
PROMPT_ROUTING_SMOKE_SUITE = tuple(routed_case(_V4_CASES_BY_ID[case_id]) for case_id in SMOKE_CASE_IDS)


def _has_repeated_sentence(text: str) -> bool:
    sentences = [sentence.strip().casefold() for sentence in re.split(r"[.!?]+", text) if sentence.strip()]
    return len(sentences) != len(set(sentences))


def assess_response(prompt: str, response: str, language: str) -> ResponseAssessment:
    normalized_prompt = normalize_text(prompt).casefold()
    normalized_response = normalize_text(response)
    failures: list[str] = []
    if not normalized_response:
        failures.append("empty")
    if normalized_response.casefold() == normalized_prompt:
        failures.append("echo")
    if _AI_DISCLAIMER.search(normalized_response):
        failures.append("ai_disclaimer")
    if _has_repeated_sentence(normalized_response):
        failures.append("self_repetition")
    if language == "vi" and (_VI_WRONG_SELF_REFERENCE.search(normalized_response) or _VI_WRONG_USER_ADDRESS.search(normalized_response)):
        failures.append("vietnamese_voice")
    return ResponseAssessment(passed=not failures, failures=tuple(failures))


def assess_proactive_flirt_response(prompt: str, response: str, language: str) -> ResponseAssessment:
    assessment = assess_response(prompt, response, language)
    failures = list(assessment.failures)
    if _OUT_OF_CONTEXT_REFUSAL.search(normalize_text(response)):
        failures.append("out_of_context_refusal")
    return ResponseAssessment(passed=not failures, failures=tuple(dict.fromkeys(failures)))


def _is_self_focused(response: str, language: str) -> bool:
    del language
    normalized_response = normalize_text(response)
    self_action_count = len(_SELF_ACTION_PATTERN.findall(normalized_response))
    if self_action_count < 2:
        return False
    has_direct_user_signal = _DIRECT_USER_SIGNAL_PATTERN.search(normalized_response) is not None
    has_answer_signal = _ANSWER_SIGNAL_PATTERN.search(normalized_response) is not None
    return not (has_direct_user_signal or has_answer_signal)


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
    normalized_response = normalize_text(response)
    if language in {"vi", "mixed"} and (
        _VI_WRONG_SELF_REFERENCE.search(normalized_response) or _VI_WRONG_USER_ADDRESS.search(normalized_response)
    ):
        failures.append("vietnamese_voice")
    if language == "en" and _VI_CONTENT_PATTERN.search(normalized_response):
        failures.append("english_language")
    if language == "mixed" and not (
        _VI_CONTENT_PATTERN.search(normalized_response) and _EN_CONTENT_PATTERN.search(normalized_response)
    ):
        failures.append("mixed_language")
    if "?" not in normalized_response and len(normalized_response.split()) <= 6:
        failures.append("generic_short_reply")
    if focus_expected and _is_self_focused(response, language):
        failures.append("self_focus")
    if follow_up_expected and "?" not in normalize_text(response):
        failures.append("missing_follow_up")
    return ResponseAssessment(passed=not failures, failures=tuple(dict.fromkeys(failures)))
