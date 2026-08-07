from __future__ import annotations

import base64
import binascii
import asyncio
import json
import math
from typing import Annotated, Literal

from fastapi import APIRouter, HTTPException, Response, WebSocket, WebSocketDisconnect
from pydantic import BaseModel, ConfigDict, Field, FiniteFloat, field_validator

from .._server_shared import (
    _TURN_LOCK,
    _MEMORY_TURN_LOCK,
    core,
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
    _valid_desktop_token,
    CapturedAudio,
    SpeechEngineConfig,
    SpeechRecognitionError,
    build_speech_recognizer,
    request_voice_audio,
    request_voice_control,
)
from ..headless_runner import BACKEND_KILO, BACKEND_OPENCODE
from ..voice_stream import VoiceStreamCoordinator, VoiceStreamEvent
from ..voice_style import voice_style_from_saved

router = APIRouter()


class VoiceTtsPayload(BaseModel):
    text: str = Field(max_length=12_000)
    voice_label: str | None = Field(default=None, max_length=128)
    speed: float | None = Field(default=None, ge=0.5, le=2.0)


class TranscribePayload(BaseModel):
    audio_b64: str = Field(max_length=7_500_000)
    sample_rate: int = Field(default=16_000, ge=8_000, le=192_000)


class VoiceLatencyPayload(BaseModel):
    model_config = ConfigDict(extra="forbid")

    submit_to_first_delta_ms: Annotated[
        FiniteFloat | None, Field(default=None, ge=0, le=120_000)
    ]
    submit_to_first_phrase_ms: Annotated[
        FiniteFloat | None, Field(default=None, ge=0, le=120_000)
    ]
    phrase_to_first_audio_ms: Annotated[
        FiniteFloat | None, Field(default=None, ge=0, le=120_000)
    ]
    submit_to_playback_requested_ms: Annotated[
        FiniteFloat | None, Field(default=None, ge=0, le=120_000)
    ]
    outcome: Literal["completed", "cancelled", "failed"]

    @field_validator(
        "submit_to_first_delta_ms",
        "submit_to_first_phrase_ms",
        "phrase_to_first_audio_ms",
        "submit_to_playback_requested_ms",
        mode="before",
    )
    @classmethod
    def reject_non_finite_duration(cls, value):
        if isinstance(value, float) and not math.isfinite(value):
            raise HTTPException(status_code=422, detail="duration must be finite")
        return value


class VoiceStreamStartPayload(VoiceTtsPayload):
    request_id: str = Field(min_length=1, max_length=128)


def _accept_playback_ack(coordinator: VoiceStreamCoordinator, payload: object) -> bool:
    if not isinstance(payload, dict):
        return False
    request_id = payload.get("request_id")
    state = payload.get("state")
    if not isinstance(request_id, str) or not isinstance(state, str):
        return False
    return coordinator.acknowledge_playback(request_id, state)


def _hosted_chat_is_active() -> bool:
    return (
        core.runner.backend in (BACKEND_OPENCODE, BACKEND_KILO)
        and _TURN_LOCK.locked()
    )


def _voice_stream_coordinator() -> VoiceStreamCoordinator:
    return VoiceStreamCoordinator(
        prepare_speech=lambda selection: _gpu_lifecycle().prepare_speech(selection),
        request_audio=request_voice_audio,
        # Hosted chat does not own the local GPU. Allow TTS to overlap its
        # response stream; local Llama keeps the memory/GPU lock boundary.
        memory_lock=None if _hosted_chat_is_active() else _MEMORY_TURN_LOCK,
        lock_timeout=_MEMORY_LOCK_TIMEOUT_SECONDS,
    )


async def _send_voice_stream_events(
    websocket: WebSocket,
    coordinator: VoiceStreamCoordinator,
    payload: VoiceStreamStartPayload,
) -> None:
    settings = effective_settings()
    voice_label = payload.voice_label or settings.get("voice_label", _DEFAULT_SETTINGS["voice_label"])
    voice_style = voice_style_from_saved(settings.get("voice_style_id"))
    speed_value = payload.speed
    if speed_value is None:
        speed_value = settings.get("voice_speed", _DEFAULT_SETTINGS["voice_speed"])
    speed = effective_voice_speed(speed_value, auto_pace_enabled=True, pace=voice_pace_for(payload.text))
    async for event in coordinator.start(
        request_id=payload.request_id,
        text=_strip_voice_markers(payload.text),
        voice_label=voice_label,
        speed=speed,
        voice_style_id=voice_style.id,
    ):
        await websocket.send_json(event.control_payload())
        if event.audio is not None:
            await websocket.send_bytes(event.audio)


async def _send_stream_failure(websocket: WebSocket, request_id: str, error_code: str) -> None:
    await websocket.send_json(
        VoiceStreamEvent("failed", request_id, 0, error_code=error_code).control_payload()
    )


@router.websocket("/voice/stream")
async def voice_stream(websocket: WebSocket):
    if not _valid_desktop_token(websocket.query_params.get("token", "")):
        await websocket.close(code=4401, reason="Unauthorized")
        return
    await websocket.accept()
    coordinator = _voice_stream_coordinator()
    active_task: asyncio.Task[None] | None = None
    active_request_id: str | None = None
    try:
        while True:
            if active_task is None:
                raw = await websocket.receive_text()
                try:
                    payload = json.loads(raw)
                except json.JSONDecodeError:
                    await _send_stream_failure(websocket, "", "invalid_request")
                    continue
                if not isinstance(payload, dict):
                    await _send_stream_failure(websocket, "", "invalid_request")
                    continue
                message_type = payload.get("type")
                if message_type == "cancel":
                    request_id = payload.get("request_id")
                    if isinstance(request_id, str):
                        coordinator.cancel(request_id)
                    continue
                if message_type == "playback":
                    _accept_playback_ack(coordinator, payload)
                    continue
                if message_type != "start":
                    await _send_stream_failure(websocket, "", "invalid_request")
                    continue
                try:
                    start_payload = VoiceStreamStartPayload.model_validate(payload)
                except Exception:
                    request_id = payload.get("request_id")
                    await _send_stream_failure(
                        websocket,
                        request_id if isinstance(request_id, str) else "",
                        "invalid_request",
                    )
                    continue
                if _TURN_LOCK.locked() and not _hosted_chat_is_active():
                    await _send_stream_failure(websocket, start_payload.request_id, "busy_chat")
                    continue
                active_request_id = start_payload.request_id
                active_task = asyncio.create_task(
                    _send_voice_stream_events(websocket, coordinator, start_payload)
                )
                continue

            receive_task = asyncio.create_task(websocket.receive_text())
            done, _ = await asyncio.wait(
                {active_task, receive_task},
                return_when=asyncio.FIRST_COMPLETED,
            )
            if active_task in done:
                active_task.result()
                active_task = None
                active_request_id = None
                if not receive_task.done():
                    receive_task.cancel()
                continue
            raw = receive_task.result()
            try:
                payload = json.loads(raw)
            except json.JSONDecodeError:
                await _send_stream_failure(websocket, active_request_id or "", "invalid_request")
                continue
            if isinstance(payload, dict) and payload.get("type") == "cancel":
                request_id = payload.get("request_id")
                if request_id == active_request_id:
                    coordinator.cancel(request_id)
            elif isinstance(payload, dict) and payload.get("type") == "playback":
                _accept_playback_ack(coordinator, payload)
            else:
                await _send_stream_failure(websocket, active_request_id or "", "stream_active")
    except WebSocketDisconnect:
        if active_request_id is not None:
            coordinator.cancel(active_request_id)
    except Exception:
        if active_request_id is not None:
            coordinator.cancel(active_request_id)
        raise


@router.get("/voice/status")
def voice_status():
    settings = effective_settings()
    return {
        "status": "ok",
        "available": voice_available(),
        "voice_label": settings.get("voice_label", _DEFAULT_SETTINGS["voice_label"]),
        "voice_style_id": settings.get(
            "voice_style_id", _DEFAULT_SETTINGS["voice_style_id"]
        ),
        "voice_speed": settings.get("voice_speed", _DEFAULT_SETTINGS["voice_speed"]),
        "auto_pace_enabled": settings.get("auto_pace_enabled", True),
        "auto_read_replies": settings.get(
            "auto_read_replies", _DEFAULT_SETTINGS["auto_read_replies"]
        ),
    }


@router.post("/voice/latency")
def voice_latency(payload: VoiceLatencyPayload):
    logger.info("VOICE_REPLY_LATENCY %s", payload.model_dump(exclude_none=True))
    return {"status": "ok"}


@router.post("/voice/tts")
def voice_tts(payload: VoiceTtsPayload):
    text = payload.text.strip()
    if not text:
        return {"status": "error", "message": "text is required"}
    settings = effective_settings()
    voice_label = payload.voice_label or settings.get("voice_label", _DEFAULT_SETTINGS["voice_label"])
    voice_style = voice_style_from_saved(settings.get("voice_style_id"))
    speed = payload.speed if payload.speed is not None else settings.get("voice_speed", _DEFAULT_SETTINGS["voice_speed"])
    pace = voice_pace_for(text)
    speed = effective_voice_speed(speed, auto_pace_enabled=True, pace=pace)
    text = _strip_voice_markers(text)
    try:
        if _TURN_LOCK.locked() and not _hosted_chat_is_active():
            return {
                "status": "error",
                "message": "Ru is generating a reply; try voice again after it finishes.",
            }
        if not _MEMORY_TURN_LOCK.acquire(timeout=_MEMORY_LOCK_TIMEOUT_SECONDS):
            return {"status": "error", "message": "Ru is busy with self-learning; try voice again in a moment."}
        try:
            _gpu_lifecycle().prepare_speech(voice_selection_from_saved(voice_label))
            audio = request_voice_audio(text, voice_label, speed, voice_style.id)
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
