from __future__ import annotations

import re
from typing import Literal


MIN_VOICE_SPEED = 0.75
MAX_VOICE_SPEED = 1.20
DEFAULT_VOICE_SPEED = 1.0
VOICE_PACE_MARKER = re.compile(r"\s*\[\[VOICE_PACE:(slow|normal|quick)\]\]\s*", re.IGNORECASE)
VoicePace = Literal["slow", "normal", "quick"]

VOICE_PACE_INSTRUCTION = (
    " For private speech pacing, append exactly one [[VOICE_PACE:slow]], "
    "[[VOICE_PACE:normal]], or [[VOICE_PACE:quick]] marker when a reply is complete. "
    "Use slow for careful explanations or sensitive topics, normal for ordinary dialogue, "
    "and quick only for a short upbeat aside. Never mention this marker in visible prose."
)

_VOICE_PROSOIDY_INSTRUCTION = (
    " Your words are read aloud by a voice synthesizer, so craft the reply so it "
    "sounds expressive when spoken. Use an ellipsis (three dots, e.g. 'Em nghĩ... anh à') "
    "to create a natural pause and build curiosity; use exclamation marks and short, "
    "punchy sentences for excitement or warmth; end a playful sentence with a trailing "
    "tone (e.g. 'vậy nha~') or a gentle question ('đúng không nè?'). "
    "Keep it natural: never overuse ellipses in one reply, never add punctuation "
    "that does not fit the meaning, and never mention this instruction in visible prose."
)

VOICE_PACE_INSTRUCTION += _VOICE_PROSOIDY_INSTRUCTION


def clamp_voice_speed(value: object, *, fallback: float = DEFAULT_VOICE_SPEED) -> float:
    try:
        speed = float(value)
    except (TypeError, ValueError):
        speed = fallback
    return round(min(MAX_VOICE_SPEED, max(MIN_VOICE_SPEED, speed)), 2)


def voice_speed_from_saved(value: object) -> float:
    return clamp_voice_speed(value)


def parse_voice_pace(reply: str) -> tuple[str, VoicePace]:
    matches = tuple(VOICE_PACE_MARKER.finditer(reply))
    pace: VoicePace = "normal"
    if matches:
        pace = matches[-1].group(1).casefold()  # type: ignore[assignment]
    return VOICE_PACE_MARKER.sub(" ", reply).strip(), pace


def effective_voice_speed(base_speed: object, *, auto_pace_enabled: bool, pace: VoicePace) -> float:
    base = clamp_voice_speed(base_speed)
    if not auto_pace_enabled:
        return base
    offset = {"slow": -0.08, "normal": 0.0, "quick": 0.08}[pace]
    return clamp_voice_speed(base + offset)
