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
    _trigger_vts_catalog_codes,
    VtsActions,
    VTS_CONNECT_TIMEOUT_SECONDS,
    VTSManager,
)

router = APIRouter()


class VtsActionPayload(BaseModel):
    facial: str | None = Field(default=None, max_length=64)
    body: str | None = Field(default=None, max_length=64)
    codes: str | None = Field(default=None, max_length=256)


class VtsSpeakingPayload(BaseModel):
    speaking: bool


@router.get("/vts/status")
def vts_status():
    manager = _VTS_MANAGER
    return {"status": "ok", "connected": bool(manager is not None and manager.is_connected)}


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
    manager.set_speaking(payload.speaking)
    return {"status": "ok", "connected": True}
