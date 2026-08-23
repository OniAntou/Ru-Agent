from __future__ import annotations

import enum

from .context import MotionContextSnapshot


class VtsMotionState(enum.StrEnum):
    IDLE = "idle"
    LISTENING = "listening"
    THINKING = "thinking"
    SPEAKING = "speaking"
    POST_REPLY = "post_reply"


POST_REPLY_DWELL_SECONDS = 4.0
LISTENING_ENTER_IDLE_SECONDS = 6.0
MIN_STATE_DWELL_SECONDS = 0.5


class MotionStateMachine:
    def __init__(self) -> None:
        self._state = VtsMotionState.IDLE
        self._entry_at = 0.0

    @property
    def state(self) -> VtsMotionState:
        return self._state

    def state_elapsed(self, now: float) -> float:
        return max(0.0, now - self._entry_at)

    def reset(self, now: float) -> None:
        self._state = VtsMotionState.IDLE
        self._entry_at = now

    def update(self, snap: MotionContextSnapshot, now: float) -> VtsMotionState:
        if snap.speaking:
            desired = VtsMotionState.SPEAKING
        elif snap.generating:
            desired = VtsMotionState.THINKING
        elif snap.user_typing:
            desired = VtsMotionState.LISTENING
        else:
            desired = self._rest_target(now)

        if desired == self._state:
            return self._state
        if desired in (VtsMotionState.SPEAKING, VtsMotionState.THINKING) or self.state_elapsed(now) >= MIN_STATE_DWELL_SECONDS:
            self._enter(desired, now)
        return self._state

    def _rest_target(self, now: float) -> VtsMotionState:
        if self._state == VtsMotionState.POST_REPLY:
            if self.state_elapsed(now) >= POST_REPLY_DWELL_SECONDS:
                return VtsMotionState.IDLE
            return VtsMotionState.POST_REPLY
        if self._state == VtsMotionState.SPEAKING:
            return VtsMotionState.POST_REPLY
        if self._state == VtsMotionState.THINKING:
            return VtsMotionState.IDLE
        if self._state == VtsMotionState.LISTENING:
            if self.state_elapsed(now) >= LISTENING_ENTER_IDLE_SECONDS:
                return VtsMotionState.IDLE
            return VtsMotionState.LISTENING
        return VtsMotionState.IDLE

    def _enter(self, state: VtsMotionState, now: float) -> None:
        self._state = state
        self._entry_at = now
