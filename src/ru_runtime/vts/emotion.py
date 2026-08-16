from __future__ import annotations

import enum
import re
import unicodedata

EMOTION_MARKER = re.compile(r"\[\[EMO:([^\[\]]+?)\]\]", re.IGNORECASE)
# Strip any EMO marker, even a malformed/unmatched one, so it can never leak
# into TTS output; normalize whitespace inside the tag first.
_STRIP_EMOTION_MARKER = re.compile(r"\[\[EMO:[^\[\]]*\]\]", re.IGNORECASE)


class EmotionTag(str, enum.Enum):
    NONE = "none"
    JOY = "joy"
    SAD = "sad"
    ANGRY = "angry"
    SURPRISED = "surprised"
    SHY = "shy"


_JOY_PATTERNS = (r"\b(vui|mung|cuoi|ha ha|he he|phan khoi|thich lam|tuyet voi)\b",)
_SAD_PATTERNS = (r"\b(buon|buon ba|tang than|met moi|chan nan)\b",)
_ANGRY_PATTERNS = (r"\b(gian|tuc|buc min|nong gian)\b",)
_SURPRISED_PATTERNS = (r"\b(ngac nhien|giat minh|oi gioi|trui ui)\b",)
_SHY_PATTERNS = (r"\b(xau ho|ngai ngu|then tho)\b",)


def _normalize(text: str) -> str:
    normalized = unicodedata.normalize("NFD", text.lower())
    normalized = "".join(char for char in normalized if unicodedata.category(char) != "Mn")
    return normalized.replace("đ", "d")


def _match_any(text: str, patterns: tuple[str, ...]) -> bool:
    return any(re.search(pattern, text) for pattern in patterns)


def _heuristic_emotion(reply: str) -> EmotionTag:
    text = _normalize(reply)
    if _match_any(text, _JOY_PATTERNS):
        return EmotionTag.JOY
    if _match_any(text, _SAD_PATTERNS):
        return EmotionTag.SAD
    if _match_any(text, _ANGRY_PATTERNS):
        return EmotionTag.ANGRY
    if _match_any(text, _SURPRISED_PATTERNS):
        return EmotionTag.SURPRISED
    if _match_any(text, _SHY_PATTERNS):
        return EmotionTag.SHY
    return EmotionTag.NONE


def extract_reply_emotion(reply: str) -> tuple[str, EmotionTag]:
    cleaned = _STRIP_EMOTION_MARKER.sub("", reply)
    cleaned = re.sub(r"\s+", " ", cleaned).strip()
    match = EMOTION_MARKER.search(reply)
    if match:
        tag = "".join(match.group(1).split()).lower()
        try:
            return cleaned, EmotionTag(tag)
        except ValueError:
            return cleaned, _heuristic_emotion(reply)
    return cleaned, _heuristic_emotion(reply)