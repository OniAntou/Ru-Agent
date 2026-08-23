from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass
from typing import Literal

from ru_runtime.generation.tool_calls import ToolCall

VolumeIntentKind = Literal["not_action", "clarification_required", "execute"]

VOLUME_TOOL_ID = "system.volume"
VOLUME_TOOL_VERSION = "1.0.0"

_NUMBER = r"(?P<value>-?\d+(?:[.,]\d+)?)"
_VOLUME_TOPIC = re.compile(r"(?<!\w)(?:âm lượng|tiếng máy|tiếng|volume|sound)(?!\w)")
_NON_ACTION_PREFIX = re.compile(
    r"^(?:nếu|giả sử|giải thích|phân tích|if|hypothetically|explain)\b"
)
_NEGATION = re.compile(
    r"(?<!\w)(?:đừng|chớ|không được|do not|don't|dont)(?!\w)"
)
_DISCUSSION_CONTEXT = re.compile(
    r"(?:thảo luận|bàn luận|bàn về|nói về|giải thích|phân tích|mô tả|"
    r"article|documentation|guide|discuss|talk about|explains?|describe)"
)
_CAPABILITY_QUESTION = re.compile(
    r"(?:được không|có thể .* không|can you|are you able|do you have access)"
)
_COMMAND_WORD = re.compile(
    r"(?:tắt tiếng|bật(?: lại)? tiếng|turn down|đặt|chỉnh|hạ|giảm|tăng|"
    r"set|lower|decrease|increase|mute|unmute)"
)
_ABSOLUTE_PATTERNS = (
    re.compile(
        rf"(?:tăng).*?(?:âm lượng|tiếng máy|tiếng).*?"
        rf"(?:lên)\s*(?:mức\s*)?{_NUMBER}\s*%?"
    ),
    re.compile(
        rf"(?:đặt|chỉnh|hạ|giảm).*?(?:âm lượng|tiếng máy|tiếng).*?"
        rf"(?:xuống\s+)?(?:còn|về|tới|đến|ở mức)\s*{_NUMBER}\s*%?"
    ),
    re.compile(
        rf"(?:set|lower|turn down).*?(?:volume|sound).*?"
        rf"(?:to|at)\s*{_NUMBER}\s*(?:%|percent)?"
    ),
)
_RELATIVE_PATTERNS = (
    (
        "increase",
        re.compile(
            rf"(?:tăng).*?(?:âm lượng|tiếng máy|tiếng).*?"
            rf"(?:thêm)\s*{_NUMBER}\s*%?"
        ),
    ),
    (
        "decrease",
        re.compile(
            rf"(?:giảm|hạ).*?(?:âm lượng|tiếng máy|tiếng).*?"
            rf"(?:đi|bớt)\s*{_NUMBER}\s*%?"
        ),
    ),
    (
        "increase",
        re.compile(
            rf"increase.*?(?:volume|sound).*?by\s*{_NUMBER}\s*(?:%|percent)?"
        ),
    ),
    (
        "decrease",
        re.compile(
            rf"(?:decrease|lower).*?(?:volume|sound).*?by\s*{_NUMBER}\s*(?:%|percent)?"
        ),
    ),
)
_GET = re.compile(
    r"(?:âm lượng|tiếng máy).*(?:hiện tại|bao nhiêu)|"
    r"(?:current|what is the).*?(?:volume|sound)"
)
_UNMUTE = re.compile(r"(?:bật lại tiếng|bật tiếng|bỏ tắt tiếng|unmute)")
_MUTE = re.compile(r"(?:tắt tiếng|mute)")
_ACTION_WORD = re.compile(
    r"(?:đặt|chỉnh|hạ|giảm|tăng|tắt|bật|set|lower|decrease|increase|mute|unmute)"
)


@dataclass(frozen=True, slots=True)
class VolumeIntentDecision:
    kind: VolumeIntentKind
    tool_call: ToolCall | None = None
    reason_code: str | None = None

    def __post_init__(self) -> None:
        if self.kind == "execute" and (
            self.tool_call is None or self.reason_code is not None
        ):
            raise ValueError("invalid execute decision")
        if self.kind == "clarification_required" and (
            self.tool_call is not None or self.reason_code is None
        ):
            raise ValueError("invalid clarification decision")
        if self.kind == "not_action" and (
            self.tool_call is not None or self.reason_code is not None
        ):
            raise ValueError("invalid non-action decision")


def resolve_volume_intent(prompt: str) -> VolumeIntentDecision:
    if not isinstance(prompt, str) or not prompt.strip() or len(prompt) > 16_000:
        return VolumeIntentDecision("not_action")

    text = " ".join(unicodedata.normalize("NFKC", prompt).casefold().split())
    if not _VOLUME_TOPIC.search(text):
        return VolumeIntentDecision("not_action")
    if (
        _NON_ACTION_PREFIX.search(text)
        or _NEGATION.search(text)
        or _DISCUSSION_CONTEXT.search(text)
        or _contains_quoted_volume_command(text)
    ):
        return VolumeIntentDecision("not_action")
    if _CAPABILITY_QUESTION.search(text):
        return VolumeIntentDecision(
            "clarification_required",
            reason_code="volume_capability_question",
        )
    if len(_COMMAND_WORD.findall(text)) > 1:
        return VolumeIntentDecision(
            "clarification_required",
            reason_code="volume_command_ambiguous",
        )

    for pattern in _ABSOLUTE_PATTERNS:
        if match := pattern.search(text):
            return _numeric_decision("set", "level", match.group("value"), 0, 100)
    for operation, pattern in _RELATIVE_PATTERNS:
        if match := pattern.search(text):
            return _numeric_decision(
                operation,
                "amount",
                match.group("value"),
                1,
                50,
            )
    if _GET.search(text):
        return _execute({"operation": "get"})
    if _UNMUTE.search(text):
        return _execute({"operation": "unmute"})
    if _MUTE.search(text):
        return _execute({"operation": "mute"})
    if _ACTION_WORD.search(text):
        return VolumeIntentDecision(
            "clarification_required",
            reason_code="volume_command_ambiguous",
        )
    return VolumeIntentDecision("not_action")


def _numeric_decision(
    operation: str,
    field: str,
    raw_value: str,
    minimum: int,
    maximum: int,
) -> VolumeIntentDecision:
    if "." in raw_value or "," in raw_value:
        return VolumeIntentDecision(
            "clarification_required",
            reason_code="volume_command_ambiguous",
        )
    value = int(raw_value)
    if not minimum <= value <= maximum:
        return VolumeIntentDecision(
            "clarification_required",
            reason_code="volume_value_out_of_range",
        )
    return _execute({"operation": operation, field: value})


def _execute(arguments: dict[str, object]) -> VolumeIntentDecision:
    return VolumeIntentDecision(
        "execute",
        tool_call=ToolCall(VOLUME_TOOL_ID, VOLUME_TOOL_VERSION, arguments),
    )


def _contains_quoted_volume_command(text: str) -> bool:
    return (
        _VOLUME_TOPIC.search(text) is not None
        and (
            re.search(r'"[^"\n]+"', text) is not None
            or re.search(r"'[^'\n]+'", text) is not None
            or re.search(r"“[^”\n]+”", text) is not None
        )
    )
