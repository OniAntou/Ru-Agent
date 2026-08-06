from __future__ import annotations

import base64
import binascii
from typing import Annotated

from fastapi import APIRouter, Response
from pydantic import BaseModel, Field

from .._server_shared import (
    _TURN_LOCK,
    _MEMORY_TURN_LOCK,
    _VOICE_LOCK,
    _gpu_lifecycle,
    _post_voice,
    _strip_voice_markers,
    _voice_config,
    _voice_selection,
    effective_voice_speed,
    effective_settings,
    logger,
    voice_available,
    voice_pace_for,
    voice_selection_by_label,
    voice_selection_from_saved,
    _DEFAULT_SETTINGS,
    _VOICE_TTS_TIMEOUT_SECONDS,
    _VOICE_LIFECYCLE_TIMEOUT_SECONDS,
    _MEMORY_LOCK_TIMEOUT_SECONDS,
    _VOICE_PROBE_TIMEOUT_SECONDS,
    VOICE_START_TIMEOUT_SECONDS,
    CapturedAudio,
    SpeechEngineConfig,
    SpeechRecognitionError,
    build_speech_recognizer,
    request_voice_audio,
    request_voice_control,
)

router = APIRouter()


class VoiceTtsPayload(BaseModel):
    text: str = Field(max_length=12_000)
    voice_label: str | None = Field(default=None, max_length=128)
    speed: float | None = Field(default=None, ge=0.5, le=2.0)


class TranscribePayload(BaseModel):
    audio_b64: str = Field(max_length=7_500_000)
    sample_rate: int = Field(default=16_000, ge=8_000, le=192_000)


@router.get("/voice/status")
def voice_status():
    return {
        "status": "ok",
        "available": voice_available(),
    }


@router.post("/voice/tts")
def voice_tts(payload: VoiceTtsPayload):
    text = payload.text.strip()
    if not text:
        return {"status": "error", "message": "text is required"}
    settings = effective_settings()
    voice_label = payload.voice_label or settings.get("voice_label", _DEFAULT_SETTINGS["voice_label"])
    speed = payload.speed if payload.speed is not None else settings.get("voice_speed", _DEFAULT_SETTINGS["voice_speed"])
    pace = voice_pace_for(text)
    speed = effective_voice_speed(speed, auto_pace_enabled=True, pace=pace)
    text = _strip_voice_markers(text)
    try:
        if _TURN_LOCK.locked():
            return {
                "status": "error",
                "message": "Ru is generating a reply; try voice again after it finishes.",
            }
        if not _MEMORY_TURN_LOCK.acquire(timeout=_MEMORY_LOCK_TIMEOUT_SECONDS):
            return {"status": "error", "message": "Ru is busy with self-learning; try voice again in a moment."}
        try:
            _gpu_lifecycle().prepare_speech(voice_selection_from_saved(voice_label))
            audio = request_voice_audio(text, voice_label, speed)
            return Response(content=audio, media_type="audio/wav")
        finally:
            _MEMORY_TURN_LOCK.release()
    except Exception as error:
        logger.error("TTS failed: %s", error)
        return {"status": "error", "message": str(error)}


@router.post("/voice/warmup")
def voice_warmup():
    try:
        if _TURN_LOCK.locked():
            return {
                "status": "error",
                "message": "Ru is generating a reply; try warm-up after it finishes.",
            }
        selection = _voice_selection()
        if not _MEMORY_TURN_LOCK.acquire(timeout=_MEMORY_LOCK_TIMEOUT_SECONDS):
            return {"status": "error", "message": "Ru is busy with self-learning; try warm-up again in a moment."}
        try:
            _gpu_lifecycle().prepare_speech(selection)
            result = {"ready": True, "voice_profile": selection.profile_id}
            return {"status": "ok", "result": result}
        finally:
            _MEMORY_TURN_LOCK.release()
    except Exception as error:
        logger.error("Voice warmup failed: %s", error)
        return {"status": "error", "message": str(error)}


@router.post("/voice/release")
def voice_release():
    try:
        result = request_voice_control("/release", _voice_selection().label)
        return {"status": "ok", "result": result}
    except Exception as error:
        logger.error("Voice release failed: %s", error)
        return {"status": "error", "message": str(error)}


@router.post("/voice/transcribe")
def voice_transcribe(payload: TranscribePayload):
    if not payload.audio_b64.strip():
        return {"status": "error", "message": "audio is required"}
    try:
        raw = base64.b64decode(payload.audio_b64, validate=True)
    except (binascii.Error, ValueError):
        return {"status": "error", "message": "invalid audio payload"}
    if not raw:
        return {"status": "error", "message": "audio is empty"}
    if payload.sample_rate <= 0:
        return {"status": "error", "message": "sample rate must be positive"}
    try:
        recognizer = build_speech_recognizer(SpeechEngineConfig(language="vi-VN"))
        text = recognizer.recognize(CapturedAudio(raw, payload.sample_rate, 2))
        return {"status": "ok", "text": text}
    except SpeechRecognitionError as error:
        return {"status": "error", "message": str(error)}
    except Exception as error:
        logger.error("Speech recognition failed: %s", error)
        return {"status": "error", "message": "Speech recognition failed."}
