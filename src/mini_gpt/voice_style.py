from __future__ import annotations

from dataclasses import dataclass
from typing import Literal, TypeAlias


VoiceStyleId: TypeAlias = Literal[
    "original",
    "low_pitch",
    "high_pitch",
    "whisper",
]

ORIGINAL_STYLE_ID: VoiceStyleId = "original"


@dataclass(frozen=True, slots=True)
class VoiceStyle:
    id: VoiceStyleId
    label: str
    instruct: str | None


VOICE_STYLES: tuple[VoiceStyle, ...] = (
    VoiceStyle("original", "Giữ giọng gốc", None),
    VoiceStyle("low_pitch", "Trầm hơn", "low pitch"),
    VoiceStyle("high_pitch", "Cao hơn", "high pitch"),
    VoiceStyle("whisper", "Thì thầm", "whisper"),
)
_VOICE_STYLES_BY_ID: dict[str, VoiceStyle] = {
    style.id: style for style in VOICE_STYLES
}


def voice_style_by_id(value: str) -> VoiceStyle:
    if not isinstance(value, str):
        raise ValueError(f"Unsupported OmniVoice voice style: {value!r}")
    style = _VOICE_STYLES_BY_ID.get(value)
    if style is None:
        raise ValueError(f"Unsupported OmniVoice voice style: {value!r}")
    return style


def voice_style_from_saved(value: object) -> VoiceStyle:
    if not isinstance(value, str):
        return _VOICE_STYLES_BY_ID[ORIGINAL_STYLE_ID]
    return _VOICE_STYLES_BY_ID.get(value, _VOICE_STYLES_BY_ID[ORIGINAL_STYLE_ID])
