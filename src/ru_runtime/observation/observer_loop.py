from __future__ import annotations

import contextlib
import logging
import threading
import time
from typing import Any

from ru_runtime.observation.gemini_vision import analyze_screenshot_with_gemini
from ru_runtime.observation.screen_capture import capture_screen_jpeg
from ru_runtime.observation.screen_reaction import VisionObservation
from ru_runtime.persistence.activity_log import append as append_activity_log
from ru_runtime.voice.vietnamese_tts_normalizer import normalize_vietnamese_tts_text

logger = logging.getLogger(__name__)
_GEMINI_TIMEOUT_SECONDS = 10.0
_STOP_JOIN_TIMEOUT_SECONDS = _GEMINI_TIMEOUT_SECONDS + 2.0


class ScreenObserverLoop:
    """Manages the background loop for continuous screen observation & live audio reaction."""

    def __init__(self, shared: Any, interval_seconds: float = 1.0) -> None:
        self._shared = shared
        self._interval_seconds = max(0.5, min(interval_seconds, 120.0))
        self._running = False
        self._thread: threading.Thread | None = None
        self._stop_event = threading.Event()
        self._lock = threading.Lock()
        self._last_observation: VisionObservation | None = None
        self._last_reaction_text: str | None = None
        self._last_reaction_id: str | None = None
        self._last_reaction_at: float | None = None
        self._last_reaction_time: float = 0.0
        self._last_check_time: float = 0.0
        self._last_error: str | None = None
        self._frame_count = 0
        self._vision_count = 0
        self._emitted_count = 0
        self._stale_drops = 0
        self._started = False

    @property
    def is_running(self) -> bool:
        thread = self._thread
        return self._running or (thread is not None and thread.is_alive())

    def start(self, interval_seconds: float | None = None) -> bool:
        with self._lock:
            if interval_seconds is not None:
                self._interval_seconds = max(0.5, min(interval_seconds, 120.0))
            if self._running:
                return True
            if self._thread is not None and self._thread.is_alive():
                logger.warning("Screen observer cannot restart while the previous thread is still stopping")
                return False
            self._stop_event.clear()
            self._running = True
            self._started = True
            self._last_observation = None
            self._last_reaction_text = None
            self._last_reaction_id = None
            self._last_reaction_at = None
            self._last_reaction_time = 0.0
            self._last_check_time = 0.0
            self._last_error = None
            self._frame_count = 0
            self._vision_count = 0
            self._emitted_count = 0
            self._stale_drops = 0
            self._thread = threading.Thread(
                target=self._run_loop,
                name="RuScreenObserverThread",
                daemon=True,
            )
            self._thread.start()
            logger.info("Screen observer loop started (interval: %.1fs)", self._interval_seconds)
            return True

    def stop(self) -> bool:
        with self._lock:
            was_running = self._running
            self._running = False
            self._stop_event.set()
            thread = self._thread
            if was_running:
                logger.info("Stopping screen observer loop...")
        if thread and thread.is_alive():
            thread.join(timeout=_STOP_JOIN_TIMEOUT_SECONDS)
        stopped = thread is None or not thread.is_alive()
        if not stopped:
            with self._lock:
                self._last_error = "Screen observer did not stop before the request timeout."
            logger.warning("Screen observer thread did not stop within %.1fs", _STOP_JOIN_TIMEOUT_SECONDS)
        return stopped

    def status(self) -> dict[str, Any]:
        with self._lock:
            thread = self._thread
            return {
                "running": self._running or (thread is not None and thread.is_alive()),
                "interval_seconds": self._interval_seconds,
                "last_check_time": self._last_check_time,
                "last_error": self._last_error,
                "last_scene": self._last_observation.scene if self._last_observation else None,
                "last_event": self._last_observation.event if self._last_observation else None,
                "last_reaction": self._last_reaction_text,
                "last_reaction_id": self._last_reaction_id,
                "last_reaction_at": self._last_reaction_at,
                "frame_count": self._frame_count,
                "vision_count": self._vision_count,
                "emitted_count": self._emitted_count,
                "stale_drops": self._stale_drops,
                "engine": "gemini" if self._started else "disabled",
            }

    def _run_loop(self) -> None:
        while self._running:
            if self._stop_event.wait(self._interval_seconds) or not self._running:
                break

            try:
                self._observe_once()
            except Exception as exc:
                logger.warning("Error in screen observation loop: %s", exc)
                with self._lock:
                    self._last_error = str(exc)

    def _observe_once(self) -> None:
        # If chat is active or turn lock is acquired, skip observation to avoid interrupting conversation
        if self._shared._TURN_LOCK.locked():
            return

        try:
            api_key = self._shared.core.gemini_vision_cred.get_key() or ""
        except Exception:
            api_key = ""

        if not api_key.strip():
            with self._lock:
                if self._last_error != "Gemini Vision API key is not configured.":
                    self._last_error = "Gemini Vision API key is not configured."
            return

        now = time.time()
        with self._lock:
            if (now - self._last_reaction_time) < 4.0:
                return

        # 1. Capture screen (<5ms GDI)
        try:
            image_bytes = capture_screen_jpeg(max_width=960, max_height=540, quality=55)
        except Exception as exc:
            logger.debug("Failed capturing screen: %s", exc)
            with self._lock:
                self._last_error = f"Screen capture error: {exc}"
            return
        with self._lock:
            self._frame_count += 1

        # 2. Analyze with Gemini Flash Vision
        observation = analyze_screenshot_with_gemini(
            image_bytes=image_bytes,
            api_key=api_key,
            timeout_seconds=_GEMINI_TIMEOUT_SECONDS,
        )

        with self._lock:
            self._vision_count += 1
            self._last_check_time = time.time()
            if observation is not None:
                self._last_observation = observation
                self._last_error = None

        with self._lock:
            if self._stop_event.is_set():
                self._stale_drops += 1
                return

        if observation is None:
            return

        logger.debug(
            "Screen observed: Scene='%s', Event='%s', Worthiness=%.2f",
            observation.scene,
            observation.event,
            observation.comment_worthiness,
        )

        # Do not publish a reaction after a chat turn starts during capture or
        # cloud analysis. The WinUI consumer also refuses to interrupt chat
        # playback, but this check avoids generating a stale event in the
        # first place.
        if self._shared._TURN_LOCK.locked():
            with self._lock:
                self._stale_drops += 1
            return

        # 3. Publish a reaction event. Audio playback belongs to the WinUI
        # VoicePlaybackService so subtitles, MediaPlayer, VTS speaking state,
        # and playback acknowledgements use one pipeline. The managed
        # observation adapter supplies any reaction text; this provider-only
        # loop must not call the retired Python Worker.
        observation_id = f"obs-{int(time.time() * 1000)}"
        reaction_text = observation.reaction

        if reaction_text:
            cleaned = normalize_vietnamese_tts_text(self._shared._strip_voice_markers(reaction_text)).strip()
            if cleaned:
                with self._lock:
                    self._last_reaction_text = cleaned
                    self._last_reaction_id = observation_id
                    self._last_reaction_at = time.time()
                    self._last_reaction_time = time.time()
                    self._emitted_count += 1
                logger.info("Published spontaneous screen reaction '%s' as %s", cleaned, observation_id)

                # Log activity event for UI speech bubbles / subtitles
                with contextlib.suppress(Exception):
                    append_activity_log({
                        "type": "observation_reaction",
                        "session_id": None,
                        "data": {
                            "text": cleaned,
                            "scene": observation.scene,
                            "event": observation.event,
                            "observation_id": observation_id,
                        },
                    })
