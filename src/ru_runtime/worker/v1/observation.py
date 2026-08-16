from __future__ import annotations

import re
import time
from typing import Any

from ru_runtime.persistence.activity_log import append as append_activity_log
from ru_runtime.worker.v1.http import RuntimeRequestError
from ru_runtime.observation.screen_reaction import (
    ReactionRequest,
    VisionObservation,
    format_observation_prompt,
)
from ru_runtime.vts.catalog import (
    catalog_instruction,
    split_reply_metadata,
)


_OBSERVATION_ID = re.compile(r"^[a-zA-Z0-9_-]{1,64}$")
_MAX_OBSERVATION_SCENE_CHARS = 200
_MAX_OBSERVATION_EVENT_CHARS = 1_500
_MAX_OBSERVATION_VISIBLE_TEXT_ITEMS = 8
_MAX_OBSERVATION_VISIBLE_TEXT_CHARS = 150


def observation_preflight() -> dict[str, object]:
    from ru_runtime.worker.v1 import runtime_services as shared
    from ru_runtime.generation.runner import BACKEND_KILO, BACKEND_OPENCODE

    if shared.core.runner.backend not in (BACKEND_OPENCODE, BACKEND_KILO):
        return {
            "status": "error",
            "message": (
                "Screen reactions require an active OpenCode or Kilo cloud backend."
            ),
        }
    if shared._TURN_LOCK.locked():
        return {
            "status": "error",
            "message": (
                "Ru is generating a reply; try the observer again after it finishes."
            ),
        }
    try:
        selection = shared._voice_selection()
        shared._gpu_lifecycle().prepare_speech(selection)
        settings = shared.effective_settings()
        return {
            "status": "ok",
            "voice_label": selection.label,
            "voice_speed": float(
                settings.get(
                    "voice_speed",
                    shared._DEFAULT_SETTINGS["voice_speed"],
                )
            ),
        }
    except Exception as error:
        shared.logger.warning(
            "Observation voice preflight failed: %s", type(error).__name__
        )
        return {"status": "error", "message": str(error)}


def observation_vision_config() -> dict[str, str]:
    from ru_runtime.worker.v1 import runtime_services as shared

    try:
        key = shared.core.gemini_vision_cred.get_key() or ""
    except Exception as error:
        shared.logger.warning("Could not read the Gemini vision API key: %s", error)
        key = ""
    if key.strip():
        return {"status": "ok", "engine": "gemini"}
    return {
        "status": "disabled",
        "engine": "local",
        "message": (
            "Local screen observation is temporarily disabled while waiting for a "
            "replacement vision model."
        ),
    }


def observation_react(payload: object) -> dict[str, object]:
    from ru_runtime.worker.v1 import runtime_services as shared

    observation_id, event_age_ms, observation = _parse_reaction(payload)
    request = ReactionRequest(
        observation_id=observation_id,
        event_age_ms=event_age_ms,
        observation=observation,
    )
    decision = shared._SCREEN_REACTIONS.begin(request)
    if not decision.allowed:
        return {"status": "skipped", "reason": decision.reason}
    if not shared._TURN_LOCK.acquire(blocking=False):
        shared._SCREEN_REACTIONS.fail(observation_id)
        return {"status": "skipped", "reason": "chat_busy"}
    if not shared._MEMORY_TURN_LOCK.acquire(blocking=False):
        shared._TURN_LOCK.release()
        shared._SCREEN_REACTIONS.fail(observation_id)
        return {"status": "skipped", "reason": "chat_busy"}

    manager = None
    try:
        manager = shared._VTS_MANAGER
        catalog = None
        if manager is not None and manager.is_connected:
            try:
                catalog = manager.current_catalog_sync()
            except Exception as error:
                shared.logger.debug(
                    "VTS catalog read failed for screen reaction: %s",
                    type(error).__name__,
                )
        if manager is not None:
            manager.note_generating(True)

        timing: dict[str, float | None] = {"seconds": None}
        started = time.perf_counter()

        def record_timing(seconds: float) -> None:
            timing["seconds"] = max(0.0, seconds)

        raw_reply = shared.core.runner.generate_screen_reaction(
            format_observation_prompt(
                request.observation,
                shared._SCREEN_REACTIONS.recent_reactions(),
            ),
            vts_instruction=catalog_instruction(catalog),
            is_cancelled=lambda: shared._SCREEN_REACTIONS.is_cancelled(observation_id),
            on_complete_timing=record_timing,
        )
        if manager is not None:
            manager.note_generating(False)
        pace = shared.voice_pace_for(raw_reply)
        cleaned, codes, emotion = split_reply_metadata(raw_reply, catalog)
        cleaned = shared._strip_voice_markers(cleaned)
        if manager is not None:
            manager.note_emotion(emotion)
        try:
            append_activity_log(
                {
                    "type": "emotion",
                    "session_id": None,
                    "data": {"emotion": emotion.value if emotion else None},
                }
            )
        except Exception:
            pass
        if not cleaned:
            shared._SCREEN_REACTIONS.fail(observation_id)
            return {"status": "skipped", "reason": "empty"}
        if shared._SCREEN_REACTIONS.is_cancelled(observation_id):
            shared._SCREEN_REACTIONS.fail(observation_id)
            return {"status": "skipped", "reason": "superseded"}
        if manager is not None and manager.is_connected and catalog is not None and codes:
            shared._trigger_vts_catalog_codes(manager, catalog, codes)

        selection = shared._voice_selection()
        settings = shared.effective_settings()
        voice_speed = shared.effective_voice_speed(
            settings.get(
                "voice_speed",
                shared._DEFAULT_SETTINGS["voice_speed"],
            ),
            auto_pace_enabled=settings.get(
                "auto_pace_enabled",
                shared._DEFAULT_SETTINGS["auto_pace_enabled"],
            ),
            pace=pace,
        )
        cloud_seconds = timing["seconds"]
        if cloud_seconds is None:
            cloud_seconds = max(0.0, time.perf_counter() - started)
        now = time.monotonic()
        with shared._PENDING_REACTION_LOCK:
            shared._PENDING_REACTION_TEXT[observation_id] = (cleaned, now)
            for stale_id, (_text, recorded_at) in list(
                shared._PENDING_REACTION_TEXT.items()
            ):
                if now - recorded_at > 60:
                    del shared._PENDING_REACTION_TEXT[stale_id]
        return {
            "status": "ok",
            "reaction": {
                "observation_id": observation_id,
                "text": cleaned,
                "voice_label": selection.label,
                "voice_speed": voice_speed,
                "cloud_ms": int(round(cloud_seconds * 1000)),
            },
        }
    except shared.OpenAIGenerationCancelled:
        shared._SCREEN_REACTIONS.fail(observation_id)
        return {"status": "skipped", "reason": "superseded"}
    except Exception as error:
        shared._SCREEN_REACTIONS.fail(observation_id)
        shared.logger.warning(
            "Screen reaction generation failed: %s", type(error).__name__
        )
        return {"status": "error", "message": str(error)}
    finally:
        if manager is not None:
            manager.note_generating(False)
        shared._MEMORY_TURN_LOCK.release()
        shared._TURN_LOCK.release()


def observation_cancel(payload: object) -> dict[str, str]:
    from ru_runtime.worker.v1 import runtime_services as shared

    observation_id = _parse_id_payload(payload)
    shared._SCREEN_REACTIONS.cancel(observation_id)
    return {"status": "ok"}


def observation_ack(payload: object) -> dict[str, str]:
    from ru_runtime.worker.v1 import runtime_services as shared

    data = _require_object(payload)
    observation_id = _required_observation_id(data.get("observation_id"))
    played = data.get("played")
    if not isinstance(played, bool):
        raise RuntimeRequestError()
    with shared._PENDING_REACTION_LOCK:
        entry = shared._PENDING_REACTION_TEXT.pop(observation_id, None)
    if played and entry is not None:
        shared._SCREEN_REACTIONS.complete(observation_id, entry[0])
    else:
        shared._SCREEN_REACTIONS.fail(observation_id)
    return {"status": "ok"}


def observation_start() -> dict[str, object]:
    return {"status": "disabled", "active": False}


def observation_stop() -> dict[str, object]:
    return {"status": "disabled", "active": False}


def observation_status() -> dict[str, object]:
    return {
        "status": "disabled",
        "active": False,
        "frame_count": 0,
        "vision_count": 0,
        "emitted_count": 0,
        "stale_drops": 0,
        "ms_before": 0,
        "ms_cloud": 0,
        "ms_after": 0,
        "last_motion_delta": 0.0,
        "engine": "disabled",
    }


def _parse_reaction(payload: object) -> tuple[str, int, VisionObservation]:
    data = _require_object(payload)
    observation_id = _required_observation_id(data.get("observation_id"))
    event_age_ms = data.get("event_age_ms")
    if (
        isinstance(event_age_ms, bool)
        or not isinstance(event_age_ms, int)
        or event_age_ms < 0
        or event_age_ms > 60_000
    ):
        raise RuntimeRequestError()
    raw = _require_object(data.get("observation"))
    scene = _bounded_text(raw.get("scene"), 500)
    event = _bounded_text(raw.get("event"), 2_000)
    confidence = _unit_number(raw.get("confidence"))
    novelty = _unit_number(raw.get("novelty"))
    worthiness = _unit_number(raw.get("comment_worthiness"))
    raw_visible = raw.get("visible_text", [])
    if not isinstance(raw_visible, list) or len(raw_visible) > 16:
        raise RuntimeRequestError()
    visible: list[str] = []
    for item in raw_visible:
        visible.append(_bounded_text(item, 200)[:_MAX_OBSERVATION_VISIBLE_TEXT_CHARS])
        if len(visible) >= _MAX_OBSERVATION_VISIBLE_TEXT_ITEMS:
            break
    return (
        observation_id,
        event_age_ms,
        VisionObservation(
            scene=scene[:_MAX_OBSERVATION_SCENE_CHARS],
            event=event[:_MAX_OBSERVATION_EVENT_CHARS],
            confidence=confidence,
            novelty=novelty,
            comment_worthiness=worthiness,
            visible_text=tuple(visible),
        ),
    )


def _parse_id_payload(payload: object) -> str:
    data = _require_object(payload)
    return _required_observation_id(data.get("observation_id"))


def _required_observation_id(value: object) -> str:
    if not isinstance(value, str) or _OBSERVATION_ID.fullmatch(value) is None:
        raise RuntimeRequestError()
    return value


def _require_object(payload: object) -> dict[str, Any]:
    if not isinstance(payload, dict):
        raise RuntimeRequestError()
    return payload


def _bounded_text(value: object, maximum: int) -> str:
    if not isinstance(value, str) or len(value) > maximum:
        raise RuntimeRequestError()
    return value


def _unit_number(value: object) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise RuntimeRequestError()
    parsed = float(value)
    if parsed < 0 or parsed > 1:
        raise RuntimeRequestError()
    return parsed
