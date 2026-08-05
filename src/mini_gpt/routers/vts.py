from __future__ import annotations

import time
from typing import Annotated

from fastapi import APIRouter
from pydantic import BaseModel, Field

from .._server_shared import (
    _VTS_LOCK,
    _VTS_MANAGER,
    _vts_manager,
    logger,
    _load_vts_motion_config,
    _save_vts_motion_config,
    _trigger_vts_catalog_codes,
    VtsActions,
    VTS_CONNECT_TIMEOUT_SECONDS,
    VTSManager,
)
from ..vts_config import MotionConfig

router = APIRouter()


class VtsActionPayload(BaseModel):
    facial: str | None = Field(default=None, max_length=64)
    body: str | None = Field(default=None, max_length=64)
    codes: str | None = Field(default=None, max_length=256)


class VtsSpeakingPayload(BaseModel):
    speaking: bool


class VtsConfigPayload(BaseModel):
    sway_amplitude_idle: float = Field(default=10.0)
    sway_amplitude_speaking: float = Field(default=16.0)
    head_scale: float = Field(default=100.0)
    eye_scale: float = Field(default=100.0)
    micro_action_min_delay: float = Field(default=8.0)
    micro_action_max_delay: float = Field(default=22.0)
    pose_min_interval: float = Field(default=90.0)
    pose_max_interval: float = Field(default=130.0)
    breath_amplitude: float = Field(default=1.5)
    breath_rate: float = Field(default=0.15)


class VtsActivityPayload(BaseModel):
    typing: bool


@router.get("/vts/status")
def vts_status():
    manager = _VTS_MANAGER
    connected = bool(manager is not None and manager.is_connected)
    config = manager.config.to_dict() if manager is not None else MotionConfig.defaults().to_dict()
    state = manager.current_state().value if manager is not None else "idle"
    return {"status": "ok", "connected": connected, "state": state, "config": config}


@router.post("/vts/connect")
def vts_connect():
    manager = _vts_manager()
    if manager.is_connected:
        return {"status": "ok", "connected": True}
    deadline = time.monotonic() + VTS_CONNECT_TIMEOUT_SECONDS
    while time.monotonic() < deadline:
        if manager.is_connected:
            return {"status": "ok", "connected": True}
        time.sleep(0.5)
    return {
        "status": "error",
        "message": "VTube Studio is not running or did not authenticate. Open VTube Studio, allow Ru, then retry.",
    }


@router.get("/vts/config")
def vts_config_get():
    return {"status": "ok", "config": _load_vts_motion_config().to_dict()}


@router.post("/vts/config")
def vts_config_save(payload: VtsConfigPayload):
    try:
        config = MotionConfig.from_dict(payload.model_dump())
    except ValueError as error:
        return {"status": "error", "message": str(error)}
    _save_vts_motion_config(config)
    manager = _VTS_MANAGER
    if manager is not None:
        manager.apply_config(config)
    return {"status": "ok", "config": config.to_dict()}


@router.post("/vts/activity")
def vts_activity(payload: VtsActivityPayload):
    manager = _VTS_MANAGER
    if manager is not None:
        manager.note_user_typing(payload.typing)
    return {"status": "ok"}


@router.post("/vts/action")
def vts_action(payload: VtsActionPayload):
    manager = _VTS_MANAGER
    if manager is None or not manager.is_connected:
        return {"status": "error", "message": "VTube Studio is not connected."}
    try:
        if payload.codes:
            codes = tuple(code.strip().lower() for code in payload.codes.replace(",", " ").split() if code.strip())
            catalog = manager.current_catalog_sync()
            if catalog is None:
                return {"status": "error", "message": "catalog not loaded yet; retry in a moment."}
            valid = {entry.code for entry in catalog.entries}
            unknown = [code for code in codes if code not in valid]
            if unknown:
                return {"status": "error", "message": f"unknown codes: {', '.join(unknown)}"}
            manager.trigger_catalog_actions_sync(catalog, codes)
            return {"status": "ok"}
        actions = VtsActions(facial=payload.facial or None, body=payload.body or None)
        if actions.facial is None and actions.body is None:
            return {"status": "error", "message": "provide codes, facial, or body"}
        if not manager.trigger_actions_sync(actions):
            return {"status": "error", "message": "requested action is unavailable or cooling down"}
        return {"status": "ok"}
    except Exception as error:
        logger.error("VTS action failed: %s", error)
        return {"status": "error", "message": str(error)}


@router.post("/vts/speaking")
def vts_speaking(payload: VtsSpeakingPayload):
    manager = _VTS_MANAGER
    if manager is None or not manager.is_connected:
        return {"status": "ok", "connected": False}
    manager.note_speaking(payload.speaking)
    return {"status": "ok", "connected": True}
