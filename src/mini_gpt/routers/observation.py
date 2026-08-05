from __future__ import annotations

import time
import threading
from typing import Annotated

from fastapi import APIRouter
from pydantic import BaseModel, Field

from mini_gpt.screen_reaction import (
    ReactionCoordinator,
    ReactionRequest,
    VisionObservation,
    format_observation_prompt,
)
from mini_gpt.vts_catalog import catalog_instruction, parse_catalog_reply

from .._server_shared import (
    BACKEND_OPENCODE,
    BACKEND_KILO,
    _PENDING_REACTION_LOCK,
    _PENDING_REACTION_TEXT,
    _SCREEN_REACTIONS,
    _TURN_LOCK,
    _MEMORY_TURN_LOCK,
    _VOICE_LOCK,
    _gpu_lifecycle,
    core,
    effective_voice_speed,
    logger,
    voice_pace_for,
    _voice_selection,
    effective_settings,
    _strip_voice_markers,
    _DEFAULT_SETTINGS,
    _VTS_MANAGER,
    _trigger_vts_catalog_codes,
    OpenAIGenerationCancelled,
)

router = APIRouter()

# format_observation_prompt() rejects prompts above 6,000 characters. Bound the
# observation text here so a maximum-valid payload can never fail with HTTP 500.
_MAX_OBSERVATION_SCENE_CHARS = 200
_MAX_OBSERVATION_EVENT_CHARS = 1_500
_MAX_OBSERVATION_VISIBLE_TEXT_ITEMS = 8
_MAX_OBSERVATION_VISIBLE_TEXT_CHARS = 150


def _bounded_observation(observation: "VisionObservationPayload") -> VisionObservation:
    visible_text: list[str] = []
    for item in observation.visible_text:
        visible_text.append(item[:_MAX_OBSERVATION_VISIBLE_TEXT_CHARS])
        if len(visible_text) >= _MAX_OBSERVATION_VISIBLE_TEXT_ITEMS:
            break
    return VisionObservation(
        scene=observation.scene[:_MAX_OBSERVATION_SCENE_CHARS],
        event=observation.event[:_MAX_OBSERVATION_EVENT_CHARS],
        confidence=observation.confidence,
        novelty=observation.novelty,
        comment_worthiness=observation.comment_worthiness,
        visible_text=tuple(visible_text),
    )


class ObservationReactionPayload(BaseModel):
    observation_id: str = Field(pattern=r"^[a-zA-Z0-9_-]{1,64}$")
    event_age_ms: int = Field(ge=0, le=60_000)
    observation: "VisionObservationPayload"


class VisionObservationPayload(BaseModel):
    scene: str = Field(max_length=500)
    event: str = Field(max_length=2_000)
    confidence: float = Field(ge=0.0, le=1.0)
    novelty: float = Field(ge=0.0, le=1.0)
    comment_worthiness: float = Field(ge=0.0, le=1.0)
    visible_text: list[Annotated[str, Field(max_length=200)]] = Field(
        default_factory=list, max_length=16
    )


class ObservationCancelPayload(BaseModel):
    observation_id: str = Field(pattern=r"^[a-zA-Z0-9_-]{1,64}$")


class ObservationAckPayload(BaseModel):
    observation_id: str = Field(pattern=r"^[a-zA-Z0-9_-]{1,64}$")
    played: bool


@router.post("/preflight")
def observation_preflight():
    if core.runner.backend not in (BACKEND_OPENCODE, BACKEND_KILO):
        return {
            "status": "error",
            "message": "Screen reactions require an active OpenCode or Kilo cloud backend.",
        }
    if _TURN_LOCK.locked():
        return {
            "status": "error",
            "message": "Ru is generating a reply; try the observer again after it finishes.",
        }
    try:
        selection = _voice_selection()
        _gpu_lifecycle().prepare_speech(selection)
        settings = effective_settings()
        return {
            "status": "ok",
            "voice_label": selection.label,
            "voice_speed": float(
                settings.get("voice_speed", _DEFAULT_SETTINGS["voice_speed"])
            ),
        }
    except Exception as error:
        logger.warning("Observation voice preflight failed: %s", type(error).__name__)
        return {"status": "error", "message": str(error)}


@router.get("/vision-config")
def observation_vision_config():
    try:
        key = core.gemini_vision_cred.get_key() or ""
    except Exception as error:
        logger.warning("Could not read the Gemini vision API key: %s", error)
        key = ""
    if key.strip():
        return {"status": "ok", "engine": "gemini"}
    return {"status": "ok", "engine": "local"}


@router.post("/react")
def observation_react(payload: ObservationReactionPayload):
    request = ReactionRequest(
        observation_id=payload.observation_id,
        event_age_ms=payload.event_age_ms,
        observation=_bounded_observation(payload.observation),
    )
    decision = _SCREEN_REACTIONS.begin(request)
    if not decision.allowed:
        return {"status": "skipped", "reason": decision.reason}
    if not _TURN_LOCK.acquire(blocking=False):
        _SCREEN_REACTIONS.fail(payload.observation_id)
        return {"status": "skipped", "reason": "chat_busy"}
    if not _MEMORY_TURN_LOCK.acquire(blocking=False):
        _TURN_LOCK.release()
        _SCREEN_REACTIONS.fail(payload.observation_id)
        return {"status": "skipped", "reason": "chat_busy"}

    try:
        manager = _VTS_MANAGER
        catalog = None
        if manager is not None and manager.is_connected:
            try:
                catalog = manager.current_catalog_sync()
            except Exception as error:
                logger.debug("VTS catalog read failed for screen reaction: %s", type(error).__name__)

        timing = {"seconds": None}
        started = time.perf_counter()

        def record_timing(seconds: float) -> None:
            timing["seconds"] = max(0.0, seconds)

        raw_reply = core.runner.generate_screen_reaction(
            format_observation_prompt(
                request.observation, _SCREEN_REACTIONS.recent_reactions()
            ),
            vts_instruction=catalog_instruction(catalog),
            is_cancelled=lambda: _SCREEN_REACTIONS.is_cancelled(payload.observation_id),
            on_complete_timing=record_timing,
        )
        pace = voice_pace_for(raw_reply)
        cleaned, codes = parse_catalog_reply(raw_reply, catalog)
        cleaned = _strip_voice_markers(cleaned)
        if not cleaned:
            _SCREEN_REACTIONS.fail(payload.observation_id)
            return {"status": "skipped", "reason": "empty"}
        if _SCREEN_REACTIONS.is_cancelled(payload.observation_id):
            _SCREEN_REACTIONS.fail(payload.observation_id)
            return {"status": "skipped", "reason": "superseded"}
        if manager is not None and manager.is_connected and catalog is not None and codes:
            _trigger_vts_catalog_codes(manager, catalog, codes)

        selection = _voice_selection()
        settings = effective_settings()
        voice_speed = effective_voice_speed(
            settings.get("voice_speed", _DEFAULT_SETTINGS["voice_speed"]),
            auto_pace_enabled=True,
            pace=pace,
        )
        cloud_seconds = timing["seconds"]
        if cloud_seconds is None:
            cloud_seconds = max(0.0, time.perf_counter() - started)
        now = time.monotonic()
        with _PENDING_REACTION_LOCK:
            _PENDING_REACTION_TEXT[payload.observation_id] = (cleaned, now)
            for stale_id, (_text, recorded_at) in list(_PENDING_REACTION_TEXT.items()):
                if now - recorded_at > 60:
                    del _PENDING_REACTION_TEXT[stale_id]
        return {
            "status": "ok",
            "reaction": {
                "observation_id": payload.observation_id,
                "text": cleaned,
                "voice_label": selection.label,
                "voice_speed": voice_speed,
                "cloud_ms": int(round(cloud_seconds * 1000)),
            },
        }
    except OpenAIGenerationCancelled:
        _SCREEN_REACTIONS.fail(payload.observation_id)
        return {"status": "skipped", "reason": "superseded"}
    except Exception as error:
        _SCREEN_REACTIONS.fail(payload.observation_id)
        logger.warning("Screen reaction generation failed: %s", type(error).__name__)
        return {"status": "error", "message": str(error)}
    finally:
        _MEMORY_TURN_LOCK.release()
        _TURN_LOCK.release()


@router.post("/cancel")
def observation_cancel(payload: ObservationCancelPayload):
    _SCREEN_REACTIONS.cancel(payload.observation_id)
    return {"status": "ok"}


@router.post("/ack")
def observation_ack(payload: ObservationAckPayload):
    with _PENDING_REACTION_LOCK:
        entry = _PENDING_REACTION_TEXT.pop(payload.observation_id, None)
    if payload.played and entry is not None:
        _SCREEN_REACTIONS.complete(payload.observation_id, entry[0])
    else:
        _SCREEN_REACTIONS.fail(payload.observation_id)
    return {"status": "ok"}


_OBSERVATION_ACTIVE = False
_OBSERVATION_LOCK = threading.Lock()


@router.post("/start")
def observation_start():
    global _OBSERVATION_ACTIVE
    with _OBSERVATION_LOCK:
        _OBSERVATION_ACTIVE = True
    return {"status": "ok", "active": True}


@router.post("/stop")
def observation_stop():
    global _OBSERVATION_ACTIVE
    with _OBSERVATION_LOCK:
        _OBSERVATION_ACTIVE = False
    return {"status": "ok", "active": False}


@router.get("/status")
def observation_status():
    with _OBSERVATION_LOCK:
        active = _OBSERVATION_ACTIVE
    return {
        "status": "ok",
        "active": active,
        "frame_count": 0,
        "vision_count": 0,
        "emitted_count": 0,
        "stale_drops": 0,
        "ms_before": 0,
        "ms_cloud": 0,
        "ms_after": 0,
        "last_motion_delta": 0.0,
        "engine": "UltraFast-WinUI3",
    }
