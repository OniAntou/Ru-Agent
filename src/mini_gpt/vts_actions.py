from __future__ import annotations

from dataclasses import dataclass
import re
import unicodedata


VTS_ACTION_INSTRUCTION = (
    " You control a connected Live2D avatar. When the user asks for a safe "
    "avatar action, comply naturally and do not claim that you cannot move, "
    "wave, or gesture. If private avatar action-code instructions are supplied, "
    "follow only those instructions and never mention action codes in visible prose."
)


@dataclass(frozen=True)
class VtsActions:
    facial: str | None = None
    body: str | None = None


def requested_vts_actions(prompt: str) -> VtsActions:
    """Recognize direct safe avatar commands without relying on LLM markup."""
    normalized = unicodedata.normalize("NFD", prompt.lower())
    normalized = "".join(char for char in normalized if unicodedata.category(char) != "Mn")
    normalized = normalized.replace("đ", "d")

    facial = None
    if re.search(r"\b(vui len|cuoi len|mim cuoi)\b", normalized):
        facial = "joy"
    elif re.search(r"\b(ngac nhien|giat minh)\b", normalized):
        facial = "surprised"
    elif re.search(r"\b(nham mat)\b", normalized):
        facial = "gentle"

    body = None
    if re.search(r"\b(gio tay|dua tay len|vay tay)\b", normalized):
        body = "raise_hand"
    elif re.search(r"\b(dung duoi|nang duoi|ve duoi)\b", normalized):
        body = "tail_up"

    return VtsActions(facial=facial, body=body)
