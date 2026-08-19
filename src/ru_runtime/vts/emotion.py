from __future__ import annotations

import enum
import re
import threading
import unicodedata
from dataclasses import dataclass

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
    POUTING = "pouting"
    CARING = "caring"
    PLAYFUL = "playful"


class MoodTag(str, enum.Enum):
    NEUTRAL = "neutral"
    CHEERFUL = "cheerful"
    PLAYFUL = "playful"
    POUTING = "pouting"
    CARING = "caring"


@dataclass
class MoodState:
    current_mood: MoodTag = MoodTag.NEUTRAL
    intensity: float = 0.0
    turns_remaining: int = 0

    def describe_for_prompt(self) -> str | None:
        if self.current_mood == MoodTag.POUTING and self.turns_remaining > 0:
            return (
                "Tâm trạng hiện tại của em: Đang hơi dỗi anh một chút. "
                "Hãy đáp lại với giọng điệu hờn dỗi nhẹ, đáng yêu ('hứ', trêu nhẹ, làm nũng), "
                "nhưng tuyệt đối không cay nghiệt hay làm tổn thương anh."
            )
        if self.current_mood == MoodTag.CARING and self.turns_remaining > 0:
            return (
                "Tâm trạng hiện tại của em: Đang rất quan tâm, lo lắng và ấm áp với anh. "
                "Hãy đáp lại với giọng điệu ân cần, gần gũi và thương anh."
            )
        if self.current_mood == MoodTag.PLAYFUL and self.turns_remaining > 0:
            return (
                "Tâm trạng hiện tại của em: Đang rất vui vẻ và nghịch ngợm. "
                "Thích trêu ghẹo, cà khịa nhẹ nhàng và đáng yêu."
            )
        if self.current_mood == MoodTag.CHEERFUL and self.turns_remaining > 0:
            return "Tâm trạng hiện tại của em: Rất vui tươi, hào hứng và phấn khởi."
        return None


_POUTING_PATTERNS = (
    r"\b(doi roi|doi|hu|khong them|bat den|gian roi|bo di|ghe qua ha|ai them|biet tay em)\b",
)
_CARING_PATTERNS = (
    r"\b(thuong anh|lo cho anh|giu suc khoe|ngu som|nghi ngoi|dung thuc khuya|dung lo|co em day|an uong|om anh)\b",
)
_PLAYFUL_PATTERNS = (
    r"\b(leu leu|que chua|ga the|choc|ca khia|treu anh|hi hi|do anh|chac chua|lac dau)\b",
)
_JOY_PATTERNS = (
    r"\b(vui|mung|cuoi|ha ha|he he|phan khoi|thich lam|tuyet voi|yeu doi|sung suong)\b",
)
_SAD_PATTERNS = (
    r"\b(buon|buon ba|tang than|met moi|chan nan|tui than|that vong)\b",
)
_ANGRY_PATTERNS = (
    r"\b(gian|tuc|buc minh|nong gian|tuc dien|dien mau)\b",
)
_SURPRISED_PATTERNS = (
    r"\b(ngac nhien|giat minh|oi gioi|trui ui|that ha|u la troi|o kia)\b",
)
_SHY_PATTERNS = (
    r"\b(xau ho|ngai ngu|then tho|ngai qua|do mat)\b",
)

_COMFORT_PATTERNS = (
    r"\b(xin loi|dung gian|thuong ma|yeu ma|ngoan|dung doi|anh xin loi|thuong em|yeu em|cung em|anh thuong)\b",
)
_USER_VULNERABLE_PATTERNS = (
    r"\b(met|met moi|buon|stress|ap luc|om roi|dau dau|that vong|khoc)\b",
)


def _normalize(text: str) -> str:
    normalized = unicodedata.normalize("NFD", text.lower())
    normalized = "".join(char for char in normalized if unicodedata.category(char) != "Mn")
    return normalized.replace("đ", "d")


def _match_any(text: str, patterns: tuple[str, ...]) -> bool:
    return any(re.search(pattern, text) for pattern in patterns)


def _heuristic_emotion(reply: str) -> EmotionTag:
    text = _normalize(reply)
    if _match_any(text, _POUTING_PATTERNS):
        return EmotionTag.POUTING
    if _match_any(text, _CARING_PATTERNS):
        return EmotionTag.CARING
    if _match_any(text, _PLAYFUL_PATTERNS):
        return EmotionTag.PLAYFUL
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


class MoodTracker:
    def __init__(self) -> None:
        self._state = MoodState()
        self._lock = threading.Lock()

    @property
    def state(self) -> MoodState:
        with self._lock:
            return MoodState(
                current_mood=self._state.current_mood,
                intensity=self._state.intensity,
                turns_remaining=self._state.turns_remaining,
            )

    def observe_user_input(self, user_prompt: str) -> None:
        """Analyze user input for comfort/apology/vulnerability before response."""
        normalized = _normalize(user_prompt)
        with self._lock:
            # If user is comforting/apologizing while Ru is pouting:
            if self._state.current_mood == MoodTag.POUTING:
                if _match_any(normalized, _COMFORT_PATTERNS):
                    self._state.current_mood = MoodTag.CHEERFUL
                    self._state.intensity = 0.7
                    self._state.turns_remaining = 2
                    return

            # If user is expressing fatigue or sadness:
            if _match_any(normalized, _USER_VULNERABLE_PATTERNS):
                self._state.current_mood = MoodTag.CARING
                self._state.intensity = 0.9
                self._state.turns_remaining = 3
                return

    def update_from_reply_emotion(self, emotion: EmotionTag) -> None:
        """Update mood state based on the emotion tag extracted from Ru's reply."""
        with self._lock:
            if emotion == EmotionTag.POUTING:
                self._state.current_mood = MoodTag.POUTING
                self._state.intensity = 0.8
                self._state.turns_remaining = 2
            elif emotion in (EmotionTag.JOY, EmotionTag.SURPRISED):
                if self._state.current_mood != MoodTag.POUTING or self._state.turns_remaining <= 0:
                    self._state.current_mood = MoodTag.CHEERFUL
                    self._state.intensity = 0.7
                    self._state.turns_remaining = 2
            elif emotion == EmotionTag.PLAYFUL:
                self._state.current_mood = MoodTag.PLAYFUL
                self._state.intensity = 0.8
                self._state.turns_remaining = 2
            elif emotion == EmotionTag.CARING:
                self._state.current_mood = MoodTag.CARING
                self._state.intensity = 0.8
                self._state.turns_remaining = 2
            else:
                if self._state.turns_remaining > 0:
                    self._state.turns_remaining -= 1
                    if self._state.turns_remaining <= 0:
                        self._state.current_mood = MoodTag.NEUTRAL
                        self._state.intensity = 0.0