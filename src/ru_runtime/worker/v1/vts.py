from __future__ import annotations

import math
import time
from typing import Any

from ru_runtime.persistence.activity_log import append as append_activity_log
from ru_runtime.vts.actions import VtsActions
from ru_runtime.vts.config import MotionConfig
from ru_runtime.vts.overlay import (
    overlay_error,
    overlay_process_ids,
    start_overlay,
    stop_overlay,
)
from ru_runtime.worker.v1.http import RuntimeRequestError


def vts_mouse_look_get() -> dict[str, object]:
    from ru_runtime.worker.v1 import runtime_services as shared

    state = shared._load_vts_gui_state()
    return {
        "status": "ok",
        "mouse_look": {
            "enabled": state["mouse_look_enabled"],
            "sensitivity": state["mouse_look_sensitivity"],
        },
    }


def vts_mouse_look_save(payload: object) -> dict[str, str]:
    from ru_runtime.worker.v1 import runtime_services as shared

    data = _require_object(payload)
    enabled = _required_bool(data, "enabled")
    sensitivity = _number(data.get("sensitivity", 1.0), 0.1, 3.0)
    manager = shared._vts_manager()
    manager.set_mouse_tracking(enabled, sensitivity)
    current = shared._load_vts_gui_state()
    shared._save_vts_gui_state(
        {
            "overlay_enabled": current["overlay_enabled"],
            "mouse_look_enabled": enabled,
            "mouse_look_sensitivity": sensitivity,
        }
    )
    return {"status": "ok"}


def vts_overlay_get() -> dict[str, object]:
    from ru_runtime.worker.v1 import runtime_services as shared

    state = shared._load_vts_gui_state()
    return {
        "status": "ok",
        "overlay": {
            "enabled": state["overlay_enabled"],
            "running": bool(overlay_process_ids()),
            "error": overlay_error(),
        },
    }


def vts_overlay_save(payload: object) -> dict[str, object]:
    from ru_runtime.worker.v1 import runtime_services as shared

    enabled = _required_bool(_require_object(payload), "enabled")
    if enabled:
        if not start_overlay():
            return {
                "status": "error",
                "message": overlay_error() or "overlay failed to start",
            }
    else:
        stop_overlay()
    shared._save_vts_gui_state(
        {**shared._load_vts_gui_state(), "overlay_enabled": enabled}
    )
    return {
        "status": "ok",
        "enabled": enabled,
        "running": bool(overlay_process_ids()),
        "error": overlay_error(),
    }


def vts_status() -> dict[str, object]:
    from ru_runtime.worker.v1 import runtime_services as shared

    manager = shared._vts_manager()
    connected = bool(manager is not None and manager.is_connected)
    config = (
        manager.config.to_dict()
        if manager is not None
        else MotionConfig.defaults().to_dict()
    )
    state = manager.current_state().value if manager is not None else "idle"
    gui = shared._load_vts_gui_state()
    return {
        "status": "ok",
        "connected": connected,
        "state": state,
        "config": config,
        "mouse_look_enabled": bool(gui["mouse_look_enabled"]),
        "mouse_look_sensitivity": gui["mouse_look_sensitivity"],
        "overlay_enabled": bool(gui["overlay_enabled"]),
        "overlay_running": bool(overlay_process_ids()),
        "connect_error": (
            getattr(manager, "last_connect_error", None)
            if manager is not None
            else None
        ),
    }


def vts_connect() -> dict[str, object]:
    from ru_runtime.worker.v1 import runtime_services as shared

    manager = shared._vts_manager()
    if manager.is_connected:
        return {"status": "ok", "connected": True}
    manager.trigger_connect()
    deadline = time.monotonic() + shared.VTS_CONNECT_TIMEOUT_SECONDS
    while time.monotonic() < deadline:
        if manager.is_connected:
            return {"status": "ok", "connected": True}
        time.sleep(0.5)
    return {
        "status": "error",
        "message": (
            "VTube Studio is not running or did not authenticate. "
            "Open VTube Studio, allow Ru, then retry."
        ),
    }


def vts_config_get() -> dict[str, object]:
    from ru_runtime.worker.v1 import runtime_services as shared

    return {"status": "ok", "config": shared._load_vts_motion_config().to_dict()}


def vts_config_save(payload: object) -> dict[str, object]:
    from ru_runtime.worker.v1 import runtime_services as shared

    data = _require_object(payload)
    defaults = {
        "sway_amplitude_idle": 10.0,
        "sway_amplitude_speaking": 16.0,
        "head_scale": 100.0,
        "eye_scale": 100.0,
        "micro_action_min_delay": 8.0,
        "micro_action_max_delay": 22.0,
        "pose_min_interval": 90.0,
        "pose_max_interval": 130.0,
        "breath_amplitude": 1.5,
        "breath_rate": 0.15,
    }
    if set(data) - set(defaults):
        raise RuntimeRequestError()
    normalized = {
        key: _number(data.get(key, default))
        for key, default in defaults.items()
    }
    try:
        config = MotionConfig.from_dict(normalized)
    except ValueError as error:
        shared.logger.warning("VTS config save rejected: %s", error)
        return {"status": "error", "message": str(error)}
    shared._save_vts_motion_config(config)
    manager = shared._VTS_MANAGER
    if manager is not None:
        manager.apply_config(config)
        shared.logger.info("Applied saved VTS motion config: %s", config.to_dict())
    else:
        shared.logger.info(
            "Saved VTS motion config (no active manager): %s", config.to_dict()
        )
    return {"status": "ok", "config": config.to_dict()}


def vts_activity(payload: object) -> dict[str, str]:
    from ru_runtime.worker.v1 import runtime_services as shared

    typing = _required_bool(_require_object(payload), "typing")
    manager = shared._VTS_MANAGER
    if manager is not None:
        manager.note_user_typing(typing)
    return {"status": "ok"}


def vts_action(payload: object) -> dict[str, str]:
    from ru_runtime.worker.v1 import runtime_services as shared

    data = _require_object(payload)
    facial = _optional_string(data, "facial", 64)
    body = _optional_string(data, "body", 64)
    codes_text = _optional_string(data, "codes", 256)
    manager = shared._VTS_MANAGER
    if manager is None or not manager.is_connected:
        return {"status": "error", "message": "VTube Studio is not connected."}
    try:
        if codes_text:
            codes = tuple(
                code.strip().lower()
                for code in codes_text.replace(",", " ").split()
                if code.strip()
            )
            catalog = manager.current_catalog_sync()
            if catalog is None:
                return {
                    "status": "error",
                    "message": "catalog not loaded yet; retry in a moment.",
                }
            valid = {entry.code for entry in catalog.entries}
            unknown = [code for code in codes if code not in valid]
            if unknown:
                return {
                    "status": "error",
                    "message": f"unknown codes: {', '.join(unknown)}",
                }
            manager.trigger_catalog_actions_sync(catalog, codes)
            append_activity_log(
                {
                    "type": "vts_action",
                    "session_id": None,
                    "data": {"codes": codes, "facial": None, "body": None},
                }
            )
            return {"status": "ok"}
        actions = VtsActions(facial=facial or None, body=body or None)
        if actions.facial is None and actions.body is None:
            return {"status": "error", "message": "provide codes, facial, or body"}
        if not manager.trigger_actions_sync(actions):
            return {
                "status": "error",
                "message": "requested action is unavailable or cooling down",
            }
        append_activity_log(
            {
                "type": "vts_action",
                "session_id": None,
                "data": {"codes": (), "facial": facial, "body": body},
            }
        )
        return {"status": "ok"}
    except Exception as error:
        shared.logger.error("VTS action failed: %s", error)
        return {"status": "error", "message": str(error)}


def vts_speaking(payload: object) -> dict[str, object]:
    from ru_runtime.worker.v1 import runtime_services as shared

    speaking = _required_bool(_require_object(payload), "speaking")
    manager = shared._vts_manager()
    if manager is None or not manager.is_connected:
        return {"status": "ok", "connected": False}
    manager.note_speaking(speaking)
    state = manager.current_state().value
    shared.logger.info("VTS speaking=%s state=%s", speaking, state)
    return {"status": "ok", "connected": True}


def _require_object(payload: object) -> dict[str, Any]:
    if not isinstance(payload, dict):
        raise RuntimeRequestError()
    return payload


def _required_bool(payload: dict[str, Any], key: str) -> bool:
    value = payload.get(key)
    if not isinstance(value, bool):
        raise RuntimeRequestError()
    return value


def _optional_string(payload: dict[str, Any], key: str, maximum: int) -> str | None:
    value = payload.get(key)
    if value is None:
        return None
    if not isinstance(value, str) or len(value) > maximum:
        raise RuntimeRequestError()
    return value


def _number(
    value: object,
    minimum: float | None = None,
    maximum: float | None = None,
) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise RuntimeRequestError()
    parsed = float(value)
    if not math.isfinite(parsed):
        raise RuntimeRequestError()
    if minimum is not None and parsed < minimum:
        raise RuntimeRequestError()
    if maximum is not None and parsed > maximum:
        raise RuntimeRequestError()
    return parsed
