from __future__ import annotations

import logging
import threading
import time
import winsound
from typing import Any

from ru_runtime.observation.screen_capture import capture_screen_jpeg
from ru_runtime.observation.gemini_vision import analyze_screenshot_with_gemini
from ru_runtime.observation.screen_reaction import VisionObservation
from ru_runtime.voice.vietnamese_tts_normalizer import normalize_vietnamese_tts_text
from ru_runtime.voice.style import voice_style_from_saved

logger = logging.getLogger(__name__)


class ScreenObserverLoop:
    """Manages the background loop for continuous screen observation & live audio reaction."""

    def __init__(self, shared: Any, interval_seconds: float = 1.0) -> None:
        self._shared = shared
        self._interval_seconds = max(0.5, min(interval_seconds, 120.0))
        self._running = False
        self._thread: threading.Thread | None = None
        self._lock = threading.Lock()
        self._last_observation: VisionObservation | None = None
        self._last_reaction_text: str | None = None
        self._last_reaction_time: float = 0.0
        self._last_check_time: float = 0.0
        self._last_error: str | None = None

    @property
    def is_running(self) -> bool:
        return self._running

    def start(self, interval_seconds: float | None = None) -> bool:
        with self._lock:
            if interval_seconds is not None:
                self._interval_seconds = max(0.5, min(interval_seconds, 120.0))
            if self._running:
                return True
            self._running = True
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
            if not self._running:
                return True
            self._running = False
            logger.info("Stopping screen observer loop...")
        if self._thread and self._thread.is_alive():
            self._thread.join(timeout=3.0)
        return True

    def status(self) -> dict[str, Any]:
        with self._lock:
            return {
                "running": self._running,
                "interval_seconds": self._interval_seconds,
                "last_check_time": self._last_check_time,
                "last_error": self._last_error,
                "last_scene": self._last_observation.scene if self._last_observation else None,
                "last_event": self._last_observation.event if self._last_observation else None,
                "last_reaction": self._last_reaction_text,
            }

    def _run_loop(self) -> None:
        while self._running:
            sleep_ticks = max(1, int(self._interval_seconds / 0.2))
            for _ in range(sleep_ticks):
                if not self._running:
                    break
                time.sleep(0.2)

            if not self._running:
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

        # 2. Analyze with Gemini Flash Vision
        observation = analyze_screenshot_with_gemini(
            image_bytes=image_bytes,
            api_key=api_key,
            timeout_seconds=10.0,
        )

        with self._lock:
            self._last_check_time = time.time()
            if observation is not None:
                self._last_observation = observation
                self._last_error = None

        if observation is None:
            return

        logger.debug(
            "Screen observed: Scene='%s', Event='%s', Worthiness=%.2f",
            observation.scene,
            observation.event,
            observation.comment_worthiness,
        )

        # 3. Direct spontaneous speech reaction
        reaction_text = observation.reaction
        if not reaction_text and observation.comment_worthiness >= 0.35:
            # Fallback to runner if reaction was not in observation
            observation_id = f"obs-{int(time.time() * 1000)}"
            payload = {
                "observation_id": observation_id,
                "event_age_ms": 100,
                "observation": {
                    "scene": observation.scene,
                    "event": observation.event,
                    "confidence": observation.confidence,
                    "novelty": observation.novelty,
                    "comment_worthiness": observation.comment_worthiness,
                    "visible_text": list(observation.visible_text),
                },
            }
            from ru_runtime.worker.v1.observation import observation_react
            result = observation_react(payload)
            if result.get("status") == "ok":
                reaction_obj = result.get("reaction") or {}
                reaction_text = reaction_obj.get("text")

        if reaction_text:
            cleaned = normalize_vietnamese_tts_text(self._shared._strip_voice_markers(reaction_text)).strip()
            if cleaned:
                with self._lock:
                    self._last_reaction_text = cleaned
                    self._last_reaction_time = time.time()
                logger.info("Ru speaking spontaneous screen reaction: '%s'", cleaned)

                # Log activity event for UI speech bubbles / subtitles
                try:
                    self._shared.append_activity_log({
                        "type": "observation_reaction",
                        "session_id": None,
                        "data": {
                            "text": cleaned,
                            "scene": observation.scene,
                            "event": observation.event,
                        },
                    })
                except Exception:
                    pass

                # Synthesize and play audio out loud through Windows speakers
                try:
                    self._shared.ensure_voice_service()
                    selection = self._shared._voice_selection()
                    settings = self._shared.effective_settings()
                    voice_style = voice_style_from_saved(settings.get("voice_style_id"))
                    speed = settings.get("voice_speed", 1.0)

                    audio = self._shared.request_voice_audio(
                        cleaned,
                        selection.label,
                        speed,
                        voice_style.id,
                    )
                    if audio and len(audio) > 100:
                        threading.Thread(
                            target=winsound.PlaySound,
                            args=(audio, winsound.SND_MEMORY),
                            daemon=True,
                        ).start()
                except Exception as voice_err:
                    logger.debug("Voice reaction playback error: %s", voice_err)
