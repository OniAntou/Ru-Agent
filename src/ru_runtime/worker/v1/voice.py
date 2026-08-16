from __future__ import annotations

import base64
import binascii
import math
from typing import Any

from ru_runtime.worker.v1.http import RuntimeHttpResponse, RuntimeRequestError
from ru_runtime.voice.style import voice_style_from_saved


def voice_status() -> dict[str, object]:
    from ru_runtime.worker.v1 import runtime_services as shared

    settings = shared.effective_settings()
    defaults = shared._DEFAULT_SETTINGS
    return {
        "status": "ok",
        "available": shared.voice_available(),
        "voice_label": settings.get("voice_label", defaults["voice_label"]),
        "voice_style_id": settings.get("voice_style_id", defaults["voice_style_id"]),
        "voice_speed": settings.get("voice_speed", defaults["voice_speed"]),
        "auto_pace_enabled": settings.get(
            "auto_pace_enabled", defaults["auto_pace_enabled"]
        ),
        "auto_read_replies": settings.get(
            "auto_read_replies", defaults["auto_read_replies"]
        ),
        "show_subtitles": settings.get("show_subtitles", defaults["show_subtitles"]),
    }


def voice_latency(payload: object) -> dict[str, str]:
    from ru_runtime.worker.v1 import runtime_services as shared

    data = _require_object(payload)
    allowed = {
        "submit_to_first_delta_ms",
        "submit_to_first_phrase_ms",
        "phrase_to_first_audio_ms",
        "submit_to_playback_requested_ms",
        "outcome",
    }
    if set(data) - allowed or data.get("outcome") not in {
        "completed",
        "cancelled",
        "failed",
    }:
        raise RuntimeRequestError()
    for key in allowed - {"outcome"}:
        value = data.get(key)
        if value is None:
            continue
        if (
            isinstance(value, bool)
            or not isinstance(value, (int, float))
            or not math.isfinite(float(value))
            or float(value) < 0
            or float(value) > 120_000
        ):
            raise RuntimeRequestError()
    shared.logger.info(
        "VOICE_REPLY_LATENCY %s",
        {key: value for key, value in data.items() if value is not None},
    )
    return {"status": "ok"}


def voice_tts(payload: object) -> dict[str, str] | RuntimeHttpResponse:
    from ru_runtime.worker.v1 import runtime_services as shared

    data = _require_object(payload)
    text = _required_string(data, "text", maximum=12_000, allow_blank=True).strip()
    if not text:
        return {"status": "error", "message": "text is required"}
    voice_label = data.get("voice_label")
    if voice_label is not None and (
        not isinstance(voice_label, str) or len(voice_label) > 128
    ):
        raise RuntimeRequestError()
    speed_value = data.get("speed")
    if speed_value is not None:
        speed_value = _bounded_number(speed_value, 0.5, 2.0)

    settings = shared.effective_settings()
    defaults = shared._DEFAULT_SETTINGS
    voice_label = voice_label or settings.get("voice_label", defaults["voice_label"])
    voice_style = voice_style_from_saved(settings.get("voice_style_id"))
    speed = (
        speed_value
        if speed_value is not None
        else settings.get("voice_speed", defaults["voice_speed"])
    )
    speed = shared.effective_voice_speed(
        speed,
        auto_pace_enabled=settings.get(
            "auto_pace_enabled", defaults["auto_pace_enabled"]
        ),
        pace=shared.voice_pace_for(text),
    )
    text = shared._strip_voice_markers(text)
    try:
        if shared._TURN_LOCK.locked() and not _hosted_chat_is_active(shared):
            return {
                "status": "error",
                "message": "Ru is generating a reply; try voice again after it finishes.",
            }
        if not shared._MEMORY_TURN_LOCK.acquire(
            timeout=shared._MEMORY_LOCK_TIMEOUT_SECONDS
        ):
            return {
                "status": "error",
                "message": "Ru is busy with self-learning; try voice again in a moment.",
            }
        try:
            shared._gpu_lifecycle().prepare_speech(
                shared.voice_selection_from_saved(voice_label)
            )
            audio = shared.request_voice_audio(
                text,
                voice_label,
                speed,
                voice_style.id,
            )
            if not isinstance(audio, bytes) or not audio:
                raise RuntimeError("Voice runtime returned empty audio.")
            return RuntimeHttpResponse(200, "audio/wav", audio, {})
        finally:
            shared._MEMORY_TURN_LOCK.release()
    except Exception as error:
        shared.logger.error("TTS failed: %s", error)
        return {"status": "error", "message": str(error)}


def voice_warmup() -> dict[str, object]:
    from ru_runtime.worker.v1 import runtime_services as shared

    try:
        if shared._TURN_LOCK.locked():
            return {
                "status": "error",
                "message": "Ru is generating a reply; try warm-up after it finishes.",
            }
        selection = shared._voice_selection()
        if not shared._MEMORY_TURN_LOCK.acquire(
            timeout=shared._MEMORY_LOCK_TIMEOUT_SECONDS
        ):
            return {
                "status": "error",
                "message": "Ru is busy with self-learning; try warm-up again in a moment.",
            }
        try:
            shared._gpu_lifecycle().prepare_speech(selection)
            return {
                "status": "ok",
                "result": {"ready": True, "voice_profile": selection.profile_id},
            }
        finally:
            shared._MEMORY_TURN_LOCK.release()
    except Exception as error:
        shared.logger.error("Voice warmup failed: %s", error)
        return {"status": "error", "message": str(error)}


def voice_release() -> dict[str, object]:
    from ru_runtime.worker.v1 import runtime_services as shared

    try:
        result = shared.request_voice_control("/release", shared._voice_selection().label)
        return {"status": "ok", "result": result}
    except Exception as error:
        shared.logger.error("Voice release failed: %s", error)
        return {"status": "error", "message": str(error)}


def voice_transcribe(payload: object) -> dict[str, str]:
    from ru_runtime.worker.v1 import runtime_services as shared

    data = _require_object(payload)
    audio_b64 = _required_string(
        data,
        "audio_b64",
        maximum=7_500_000,
        allow_blank=True,
    )
    sample_rate = data.get("sample_rate", 16_000)
    if (
        isinstance(sample_rate, bool)
        or not isinstance(sample_rate, int)
        or sample_rate < 8_000
        or sample_rate > 192_000
    ):
        raise RuntimeRequestError()
    if not audio_b64.strip():
        return {"status": "error", "message": "audio is required"}
    try:
        raw = base64.b64decode(audio_b64, validate=True)
    except (binascii.Error, ValueError):
        return {"status": "error", "message": "invalid audio payload"}
    if not raw:
        return {"status": "error", "message": "audio is empty"}
    try:
        recognizer = shared.build_speech_recognizer(
            shared.SpeechEngineConfig(language="vi-VN")
        )
        text = recognizer.recognize(shared.CapturedAudio(raw, sample_rate, 2))
        return {"status": "ok", "text": text}
    except shared.SpeechRecognitionError as error:
        return {"status": "error", "message": str(error)}
    except Exception as error:
        shared.logger.error("Speech recognition failed: %s", error)
        return {"status": "error", "message": "Speech recognition failed."}


def _hosted_chat_is_active(shared: Any) -> bool:
    from ru_runtime.generation.runner import BACKEND_KILO, BACKEND_OPENCODE

    return (
        shared.core.runner.backend in (BACKEND_OPENCODE, BACKEND_KILO)
        and shared._TURN_LOCK.locked()
    )


def _require_object(payload: object) -> dict[str, Any]:
    if not isinstance(payload, dict):
        raise RuntimeRequestError()
    return payload


def _required_string(
    payload: dict[str, Any],
    key: str,
    *,
    maximum: int,
    allow_blank: bool = False,
) -> str:
    value = payload.get(key)
    if (
        not isinstance(value, str)
        or len(value) > maximum
        or (not allow_blank and not value.strip())
    ):
        raise RuntimeRequestError()
    return value


def _bounded_number(value: object, minimum: float, maximum: float) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise RuntimeRequestError()
    parsed = float(value)
    if not math.isfinite(parsed) or parsed < minimum or parsed > maximum:
        raise RuntimeRequestError()
    return parsed
