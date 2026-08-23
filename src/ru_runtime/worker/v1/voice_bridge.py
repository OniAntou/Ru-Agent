from __future__ import annotations

import asyncio
import json
import math
import threading
from collections.abc import AsyncIterator
from typing import Any

from ru_runtime.generation.runner import OPENAI_COMPATIBLE_BACKENDS
from ru_runtime.voice.stream import VoiceStreamCoordinator, VoiceStreamEvent
from ru_runtime.voice.style import voice_style_from_saved
from ru_runtime.worker.v1.http import RuntimeHttpResponse


class RuntimeVoiceBridge:
    """Own the existing Python voice coordinator behind private worker calls."""

    def __init__(self, shared: Any) -> None:
        self._shared = shared
        self._lock = threading.RLock()
        self._active: dict[str, VoiceStreamCoordinator] = {}

    async def stream(
        self,
        payload: dict[str, Any],
        cancelled: asyncio.Event,
    ) -> AsyncIterator[VoiceStreamEvent]:
        request_id = _required_text(payload, "request_id", 128)
        text = _required_text(payload, "text", 12_000)
        settings = self._shared.effective_settings()
        voice_label = payload.get("voice_label")
        if voice_label is None or voice_label == "":
            voice_label = settings.get(
                "voice_label",
                self._shared._DEFAULT_SETTINGS["voice_label"],
            )
        if not isinstance(voice_label, str) or len(voice_label) > 128:
            raise ValueError("invalid voice label")
        speed_value = payload.get("speed")
        if speed_value is None:
            speed_value = settings.get(
                "voice_speed",
                self._shared._DEFAULT_SETTINGS["voice_speed"],
            )
        if isinstance(speed_value, bool) or not isinstance(speed_value, (int, float)):
            raise ValueError("invalid voice speed")
        speed = float(speed_value)
        if not math.isfinite(speed) or speed < 0.5 or speed > 2.0:
            raise ValueError("invalid voice speed")
        speed = self._shared.effective_voice_speed(
            speed,
            auto_pace_enabled=settings.get(
                "auto_pace_enabled",
                self._shared._DEFAULT_SETTINGS["auto_pace_enabled"],
            ),
            pace=self._shared.voice_pace_for(text),
        )
        style = voice_style_from_saved(
            payload.get("voice_style_id")
            or settings.get("voice_style_id")
        )
        hosted_chat_active = (
            self._shared.core.runner.backend in OPENAI_COMPATIBLE_BACKENDS
            and self._shared._TURN_LOCK.locked()
        )
        coordinator = VoiceStreamCoordinator(
            prepare_speech=lambda selection: self._shared._gpu_lifecycle().prepare_speech(
                selection
            ),
            request_audio=self._shared.request_voice_audio,
            memory_lock=None if hosted_chat_active else self._shared._MEMORY_TURN_LOCK,
            lock_timeout=self._shared._MEMORY_LOCK_TIMEOUT_SECONDS,
        )
        with self._lock:
            if request_id in self._active:
                raise ValueError("duplicate voice request")
            self._active[request_id] = coordinator

        async def watch_cancellation() -> None:
            await cancelled.wait()
            coordinator.cancel(request_id)

        watcher = asyncio.create_task(watch_cancellation())
        from ru_runtime.voice.vietnamese_tts_normalizer import normalize_vietnamese_tts_text

        cleaned_text = normalize_vietnamese_tts_text(self._shared._strip_voice_markers(text))
        try:
            async for event in coordinator.start(
                request_id=request_id,
                text=cleaned_text,
                voice_label=voice_label,
                speed=speed,
                voice_style_id=style.id,
            ):
                yield event
        finally:
            coordinator.cancel(request_id)
            watcher.cancel()
            with self._lock:
                if self._active.get(request_id) is coordinator:
                    self._active.pop(request_id, None)

    def handle_private_control(
        self,
        method: str,
        path_and_query: str,
        body: str,
    ) -> RuntimeHttpResponse | None:
        if method != "POST" or path_and_query not in {
            "/_worker/voice/cancel",
            "/_worker/voice/playback",
        }:
            return None
        try:
            payload = json.loads(body)
        except json.JSONDecodeError:
            payload = None
        if not isinstance(payload, dict):
            return _json_response({"status": "error", "message": "invalid control"}, 422)
        request_id = payload.get("request_id")
        if not isinstance(request_id, str) or not request_id or len(request_id) > 128:
            return _json_response({"status": "error", "message": "invalid control"}, 422)
        with self._lock:
            coordinator = self._active.get(request_id)
        if coordinator is None:
            return _json_response({"status": "ok", "accepted": False})
        if path_and_query.endswith("/cancel"):
            coordinator.cancel(request_id)
            return _json_response({"status": "ok", "accepted": True})
        state = payload.get("state")
        accepted = isinstance(state, str) and coordinator.acknowledge_playback(
            request_id,
            state,
        )
        return _json_response({"status": "ok", "accepted": accepted})


def _required_text(payload: dict[str, Any], key: str, maximum: int) -> str:
    value = payload.get(key)
    if not isinstance(value, str) or not value.strip() or len(value) > maximum:
        raise ValueError(f"invalid {key}")
    return value


def _json_response(payload: object, status_code: int = 200) -> RuntimeHttpResponse:
    return RuntimeHttpResponse(
        status_code,
        "application/json",
        json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8"),
        {},
    )
