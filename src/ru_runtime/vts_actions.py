from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass


VTS_ACTION_INSTRUCTION = (
    " You control a connected Live2D avatar. Comply with safe avatar-motion "
    "requests naturally; never claim you cannot move, wave, or gesture. Use "
    "only the private action codes supplied in the instruction block, never "
    "invent new ones, and never mention codes in visible prose. You may "
    "combine multiple compatible facial or expressive actions in one reply "
    "when they reinforce the same emotion."
)


@dataclass(frozen=True)
class VtsActions:
    facial: str | None = None
    body: str | None = None


# Tiền tố phủ định trong tiếng Việt (đã normalize bỏ dấu)
_NEGATION_PREFIXES = re.compile(r"\b(dung|khong|cho|dung co|chua|chang)\b")
_NEGATION_LOOKBACK_CHARS = 20


def _find_unnegated(pattern: re.Pattern, text: str) -> re.Match | None:
    """Return the first match not preceded by a Vietnamese negation."""
    for match in pattern.finditer(text):
        prefix = text[max(0, match.start() - _NEGATION_LOOKBACK_CHARS): match.start()]
        if not _NEGATION_PREFIXES.search(prefix):
            return match
    return None


def requested_vts_actions(prompt: str) -> VtsActions:
    """Recognize direct safe avatar commands without relying on LLM markup."""
    normalized = unicodedata.normalize("NFD", prompt.lower())
    normalized = "".join(char for char in normalized if unicodedata.category(char) != "Mn")
    normalized = normalized.replace("đ", "d")

    facial = None
    if _find_unnegated(re.compile(r"\b(vui len|cuoi len|mim cuoi)\b"), normalized):
        facial = "joy"
    elif _find_unnegated(re.compile(r"\b(ngac nhien|giat minh)\b"), normalized):
        facial = "surprised"
    elif _find_unnegated(re.compile(r"\b(nham mat)\b"), normalized):
        facial = "gentle"

    body = None
    if _find_unnegated(re.compile(r"\b(gio tay|dua tay len|vay tay)\b"), normalized):
        body = "raise_hand"
    elif _find_unnegated(re.compile(r"\b(dung duoi|nang duoi|ve duoi)\b"), normalized):
        body = "tail_up"

    return VtsActions(facial=facial, body=body)
