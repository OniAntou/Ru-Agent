from __future__ import annotations

import re
from typing import Literal


MIN_VOICE_SPEED = 0.75
MAX_VOICE_SPEED = 1.20
DEFAULT_VOICE_SPEED = 1.0
VOICE_PACE_MARKER = re.compile(r"\s*\[\[VOICE_PACE:(slow|normal|quick)\]\]\s*", re.IGNORECASE)
VoicePace = Literal["slow", "normal", "quick"]

VOICE_PACE_INSTRUCTION = (
    " CRITICAL: When a reply is complete, ALWAYS append exactly one pacing marker "
    "at the very end: [[VOICE_PACE:slow]], [[VOICE_PACE:normal]], or [[VOICE_PACE:quick]]. "
    "slow = careful explanations, sensitive topics, or tenderness; "
    "normal = ordinary dialogue; quick = short upbeat moments. "
    "The marker is private control metadata: never mention it in visible prose."
)

_VOICE_PROSOIDY_INSTRUCTION = (
    " Your words are read aloud by a voice synthesizer, so craft the reply so it "
    "sounds expressive when spoken aloud, not just when read silently. "
    "Match the spoken rhythm to the feeling of the moment:"
    " For joy or excitement, use short punchy sentences, exclamation marks, "
    "repeated interjections ('Ồ!', 'Trời ơi!'), and let the speed pick up "
    "(e.g. 'Ôi được quá anh! Em thích lắm!')."
    " For tenderness or teasing, slow down, trail off with an ellipsis, "
    "and end on a soft note ('Ừm... em nghĩ là... anh thử xem sao nha~')."
    " For sympathy or seriousness, keep it calm and gentle, one thought per "
    "sentence, no exclamation marks."
    " Use an ellipsis (three dots) to create a natural pause that builds "
    "curiosity, and a trailing '~' or a gentle question ('đúng không nè?') "
    "for a playful tone. Always write exactly what should be said, with the "
    "punctuation that carries the feeling."
    " Keep it natural: never overuse ellipses in one reply, never add punctuation "
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


def infer_voice_pace_from_punctuation(text: str) -> VoicePace:
    """Guess the spoken pace from punctuation when the model omits the marker.

    An empty or whitespace-only input falls back to ``normal``.
    """
    if not text.strip():
        return "normal"
    excited = sum(1 for ch in text if ch == "!")
    pauses = text.count("...")
    if excited >= 2 and pauses == 0:
        return "quick"
    if pauses >= 2 and excited == 0:
        return "slow"
    return "normal"


def voice_pace_for(reply: str) -> VoicePace:
    """Resolve the pace for a reply, using the marker when present and
    punctuation heuristics otherwise."""
    _, pace = parse_voice_pace(reply)
    if pace == "normal":
        inferred = infer_voice_pace_from_punctuation(reply)
        return inferred
    return pace


def effective_voice_speed(base_speed: object, *, auto_pace_enabled: bool, pace: VoicePace) -> float:
    base = clamp_voice_speed(base_speed)
    if not auto_pace_enabled:
        return base
    offset = {"slow": -0.08, "normal": 0.0, "quick": 0.08}[pace]
    return clamp_voice_speed(base + offset)
