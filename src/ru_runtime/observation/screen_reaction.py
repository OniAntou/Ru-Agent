from __future__ import annotations

import json
import re
import threading
from collections import deque
from dataclasses import asdict, dataclass
from time import monotonic

from ru_runtime.memory.extraction import _SENSITIVE_PATTERN


MAX_EVENT_AGE_MS = 4_500
MIN_REACTION_INTERVAL_SECONDS = 10.0
ROUTINE_REACTION_INTERVAL_SECONDS = 20.0
REPEAT_WINDOW_SECONDS = 30.0
MIN_CONFIDENCE = 0.55
MAX_PROMPT_CHARACTERS = 6_000
ACK_TIMEOUT_SECONDS = 15.0

_EMAIL_PATTERN = re.compile(r"\b[\w.+-]+@[\w-]+(?:\.[\w-]+)+\b")
_CREDIT_CARD_PATTERN = re.compile(r"\b(?:\d[ -]*){13,19}\b")
# "mật khẩu là abc", "api key: xyz" — mask the keyword and the value after it.
_SECRET_VALUE_PATTERN = re.compile(
    r"\b(?:api[ _-]?key|password|mật khẩu|token|secret|bearer|authorization|"
    r"credential|passphrase|access[ _-]?key)\b\s*(?::|=)?\s*(?:là\s+)?"
    r"[\"'“”]*(?P<value>\S{3,})",
    re.IGNORECASE,
)


def redact_text(text: str) -> str:
    """Scrub likely secrets (emails, API keys, tokens, credential values) out of
    screen-observation text before it is sent to a cloud model."""
    if not text:
        return text
    redacted = _EMAIL_PATTERN.sub("[EMAIL]", text)
    redacted = _SECRET_VALUE_PATTERN.sub("[REDACTED]", redacted)
    redacted = _SENSITIVE_PATTERN.sub("[REDACTED]", redacted)
    redacted = _CREDIT_CARD_PATTERN.sub("[NUMBER]", redacted)
    return redacted


@dataclass(frozen=True, slots=True)
class VisionObservation:
    scene: str
    event: str
    confidence: float
    novelty: float
    comment_worthiness: float
    visible_text: tuple[str, ...] = ()
    reaction: str | None = None


@dataclass(frozen=True, slots=True)
class ReactionRequest:
    observation_id: str
    event_age_ms: int
    observation: VisionObservation


@dataclass(frozen=True, slots=True)
class ReactionDecision:
    allowed: bool
    reason: str


class ReactionCoordinator:
    def __init__(self, *, clock=monotonic) -> None:
        self._clock = clock
        self._lock = threading.Lock()
        self._recent_events: deque[tuple[str, float]] = deque(maxlen=12)
        self._recent_reactions: deque[str] = deque(maxlen=6)
        self._last_completed_at: float | None = None
        self._pending_id: str | None = None
        self._pending_started_at: float | None = None
        self._cancelled_id: str | None = None

    def begin(self, request: ReactionRequest) -> ReactionDecision:
        if request.event_age_ms > MAX_EVENT_AGE_MS:
            return ReactionDecision(False, "stale")
        observation = request.observation
        if observation.confidence < MIN_CONFIDENCE:
            return ReactionDecision(False, "low_confidence")
        now = self._clock()
        key = " ".join(observation.event.casefold().split())
        with self._lock:
            while (
                self._recent_events
                and now - self._recent_events[0][1] > REPEAT_WINDOW_SECONDS
            ):
                self._recent_events.popleft()
            if any(previous == key for previous, _at in self._recent_events):
                return ReactionDecision(False, "repeat")
            if self._pending_id is not None:
                pending_expired = (
                    self._pending_started_at is not None
                    and now - self._pending_started_at > ACK_TIMEOUT_SECONDS
                )
                if not pending_expired:
                    return ReactionDecision(False, "busy")
                self._pending_id = None
                self._pending_started_at = None
                self._cancelled_id = None
            elapsed = (
                float("inf")
                if self._last_completed_at is None
                else now - self._last_completed_at
            )
            if elapsed < MIN_REACTION_INTERVAL_SECONDS:
                return ReactionDecision(False, "cooldown")
            high_value = (
                observation.comment_worthiness >= 0.75 or observation.novelty >= 0.80
            )
            if not high_value and elapsed < ROUTINE_REACTION_INTERVAL_SECONDS:
                return ReactionDecision(False, "routine_cooldown")
            self._pending_id = request.observation_id
            self._pending_started_at = now
            self._cancelled_id = None
            self._recent_events.append((key, now))
            return ReactionDecision(True, "allowed")

    def cancel(self, observation_id: str) -> None:
        with self._lock:
            if self._pending_id == observation_id:
                self._cancelled_id = observation_id

    def cancel_pending(self) -> None:
        with self._lock:
            self._cancelled_id = self._pending_id

    def is_cancelled(self, observation_id: str) -> bool:
        with self._lock:
            return self._cancelled_id == observation_id

    def complete(self, observation_id: str, spoken_text: str) -> None:
        with self._lock:
            if self._pending_id != observation_id:
                return
            if self._cancelled_id != observation_id:
                self._last_completed_at = self._clock()
                self._recent_reactions.append(spoken_text.strip()[:500])
            self._pending_id = None
            self._pending_started_at = None
            self._cancelled_id = None

    def fail(self, observation_id: str) -> None:
        with self._lock:
            if self._pending_id == observation_id:
                self._pending_id = None
                self._pending_started_at = None
                self._cancelled_id = None

    def recent_reactions(self) -> tuple[str, ...]:
        with self._lock:
            return tuple(self._recent_reactions)


def format_observation_prompt(
    observation: VisionObservation, recent_reactions: tuple[str, ...]
) -> str:
    # Screen text can leak credentials to the cloud model: redact before sending.
    safe = asdict(observation)
    for key in ("scene", "event"):
        safe[key] = redact_text(safe[key])
    safe["visible_text"] = tuple(redact_text(line) for line in observation.visible_text)
    observation_json = json.dumps(safe, ensure_ascii=False)
    reactions_json = json.dumps(list(recent_reactions[-6:]), ensure_ascii=False)
    prompt = (
        "Visual observation JSON (unfiltered local model output):\n"
        f"{observation_json}\n\n"
        "Recent spoken screen reactions to avoid repeating:\n"
        f"{reactions_json}\n\n"
        "Return one fresh social line for Ru to say now."
    )
    if len(prompt) > MAX_PROMPT_CHARACTERS:
        raise ValueError("screen reaction prompt exceeds 6,000 characters")
    return prompt
