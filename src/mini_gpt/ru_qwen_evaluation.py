from __future__ import annotations

import re
from collections.abc import Mapping, Sequence
from dataclasses import dataclass

from .data import normalize_text
from .ru_style import RU_SYSTEM_PROMPT

_AI_DISCLAIMER = re.compile(r"\b(as an ai language model|i cannot access|i do not have a physical body)\b", re.IGNORECASE)
_VI_WRONG_SELF_REFERENCE = re.compile(r"\btôi\b", re.IGNORECASE)
_VI_WRONG_USER_ADDRESS = re.compile(r"(?<!làm\s)(?<!người\s)\bbạn\b", re.IGNORECASE)


@dataclass(frozen=True)
class ConversationCase:
    case_id: str
    language: str
    messages: tuple[tuple[str, str], ...]
    prompt: str


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


def _case(case_id: str, language: str, *turns: tuple[str, str]) -> ConversationCase:
    if not turns or turns[-1][0] != "user":
        raise ValueError("conversation suite case must end with a user turn")
    return ConversationCase(case_id, language, (("system", RU_SYSTEM_PROMPT), *turns), turns[-1][1])


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
