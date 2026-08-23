from __future__ import annotations

import dataclasses
import threading
from dataclasses import dataclass

from .emotion import EmotionTag


@dataclass(frozen=True)
class MotionContextSnapshot:
    speaking: bool = False
    generating: bool = False
    user_typing: bool = False
    system_idle: bool = False
    emotion: EmotionTag = EmotionTag.NONE


class MotionContextProvider:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._snap = MotionContextSnapshot()

    def set_speaking(self, speaking: bool) -> None:
        with self._lock:
            self._snap = dataclasses.replace(self._snap, speaking=bool(speaking))

    def set_generating(self, generating: bool) -> None:
        with self._lock:
            self._snap = dataclasses.replace(self._snap, generating=bool(generating))

    def set_user_typing(self, typing: bool) -> None:
        with self._lock:
            self._snap = dataclasses.replace(self._snap, user_typing=bool(typing))

    def set_system_idle(self, idle: bool) -> None:
        with self._lock:
            self._snap = dataclasses.replace(self._snap, system_idle=bool(idle))

    def set_emotion(self, emotion: EmotionTag | str) -> None:
        if isinstance(emotion, str):
            try:
                emotion = EmotionTag(emotion)
            except ValueError:
                emotion = EmotionTag.NONE
        with self._lock:
            self._snap = dataclasses.replace(self._snap, emotion=emotion)

    def snapshot(self) -> MotionContextSnapshot:
        with self._lock:
            return self._snap
