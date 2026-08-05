from __future__ import annotations

from fastapi import APIRouter, Query

from .._server_shared import (
    BACKEND_KILO,
    BACKEND_OPENCODE,
    _MEMORY_TURN_LOCK,
    _resolve_backend,
    core,
    load_history,
    logger,
)
from ..activity_log import read

router = APIRouter()


@router.get("/status")
async def get_status():
    return {"status": "ok", "message": "Backend is running and ready for the WinUI3 UI."}


@router.get("/activity-log")
async def get_activity_log(limit: int = Query(default=200, ge=1, le=500)):
    return {"status": "ok", "events": read(limit=limit)}


@router.get("/models")
async def get_models(backend: str = ""):
    backend = _resolve_backend(backend) or backend
    if backend not in (BACKEND_OPENCODE, BACKEND_KILO):
        return {"status": "error", "message": f"unsupported backend: {backend!r}"}
    from mini_gpt.openai_runtime import KILO_GATEWAY_BASE_URL, OPENCODE_BASE_URL

    cred = core.opencode_cred if backend == BACKEND_OPENCODE else core.kilo_cred
    try:
        api_key = cred.get_key() or ""
    except Exception as error:
        logger.warning("Could not read the saved API key: %s", error)
        api_key = ""
    if not api_key:
        return {"status": "error", "message": "API key is not set. Save it in Model Settings first."}
    base_url = OPENCODE_BASE_URL if backend == BACKEND_OPENCODE else KILO_GATEWAY_BASE_URL
    try:
        from mini_gpt.openai_runtime import OpenAIRuntime

        models = OpenAIRuntime.fetch_models(api_key, base_url)
    except Exception as error:
        logger.warning("Failed to fetch models for %s: %s", backend, error)
        return {"status": "error", "message": str(error)}
    return {"status": "ok", "models": [model["id"] for model in models]}


@router.get("/history")
async def get_history():
    return load_history()


@router.delete("/memory")
async def clear_memory():
    if not _MEMORY_TURN_LOCK.acquire(blocking=False):
        return {
            "status": "error",
            "message": "Memory is busy (self-learning in progress); try again in a moment.",
        }
    try:
        core.clear_memory()
        return {
            "status": "success",
            "message": "Structured memory cleared. Session history was retained.",
        }
    finally:
        _MEMORY_TURN_LOCK.release()
