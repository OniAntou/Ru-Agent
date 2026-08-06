from __future__ import annotations

from fastapi import APIRouter
from pydantic import BaseModel, Field

from .._server_shared import (
    BACKEND_GGUF,
    BACKEND_KILO,
    BACKEND_OPENCODE,
    _resolve_backend,
    core,
    effective_settings,
    logger,
    _merge_settings,
)

router = APIRouter()


class SettingsPayload(BaseModel):
    backend: str | None = Field(default=None, max_length=64)
    backend_choice: str | None = Field(default=None, max_length=64)
    model: str | None = Field(default=None, max_length=256)
    opencode_model: str | None = Field(default=None, max_length=256)
    kilo_model: str | None = Field(default=None, max_length=256)
    api_key: str = Field(default="", max_length=4096)
    opencode_api_key: str = Field(default="", max_length=4096)
    kilo_api_key: str = Field(default="", max_length=4096)
    api_variant: str | None = Field(default=None, max_length=64)
    max_tokens: int | None = Field(default=None, ge=1, le=1024)
    max_new_tokens: int | None = Field(default=None, ge=1, le=1024)
    temperature: float | None = Field(default=None, ge=0.1, le=2.0)
    top_k: int | None = Field(default=None, ge=1, le=200)
    allow_web_search: bool | None = None
    allow_online_search: bool | None = None
    enable_self_learning: bool | None = None
    self_learning_enabled: bool | None = None
    share_memory_with_cloud: bool | None = None
    cloud_memory_consent: bool | None = None
    checkpoint_path: str = Field(default="", max_length=1024)
    voice_label: str | None = Field(default=None, max_length=128)
    voice_speed: float | None = Field(default=None, ge=0.5, le=2.0)
    auto_pace_enabled: bool | None = None
    auto_read_replies: bool | None = None
    gemini_vision_api_key: str = Field(default="", max_length=4096)


@router.get("/settings")
def get_settings():
    settings = effective_settings()
    backend = settings.pop("backend", BACKEND_GGUF)
    try:
        opencode_key = core.opencode_cred.get_key() or ""
    except Exception:
        opencode_key = ""
    try:
        kilo_key = core.kilo_cred.get_key() or ""
    except Exception:
        kilo_key = ""
    try:
        gemini_vision_key = core.gemini_vision_cred.get_key() or ""
    except Exception:
        gemini_vision_key = ""
    
    _REVERSE_ALIASES = {v: k for k, v in {
        "gguf": BACKEND_GGUF,
        "opencode": BACKEND_OPENCODE,
        "kilo": BACKEND_KILO,
    }.items()}
    backend_short = _REVERSE_ALIASES.get(backend, backend)
    return {
        "status": "ok",
        "backend": backend,
        "backend_choice": backend_short,
        **settings,
        "allow_online_search": settings.get("allow_web_search", True),
        "self_learning_enabled": settings.get("enable_self_learning", True),
        "cloud_memory_consent": settings.get("share_memory_with_cloud", False),
        "max_new_tokens": settings.get("max_tokens", 1024),
        "opencode_api_key": "",
        "kilo_api_key": "",
        "gemini_vision_api_key": "",
        "has_opencode_key": bool(opencode_key),
        "has_kilo_key": bool(kilo_key),
        "has_gemini_vision_key": bool(gemini_vision_key),
    }


@router.post("/settings")
def save_settings(payload: SettingsPayload):
    backend_val = _resolve_backend(payload.backend_choice or payload.backend)
    if backend_val is not None and backend_val not in (BACKEND_GGUF, BACKEND_OPENCODE, BACKEND_KILO):
        return {"status": "error", "message": f"unsupported backend: {backend_val}"}

    def _apply(settings: dict) -> None:
        if backend_val is not None:
            settings["backend"] = backend_val
        if payload.model is not None:
            settings["model"] = payload.model
        if payload.opencode_model is not None:
            settings["opencode_model"] = payload.opencode_model
        if payload.kilo_model is not None:
            settings["kilo_model"] = payload.kilo_model
        if payload.api_variant is not None:
            settings["api_variant"] = payload.api_variant
        resolved_max_tokens = payload.max_tokens if payload.max_tokens is not None else payload.max_new_tokens
        if resolved_max_tokens is not None:
            settings["max_tokens"] = resolved_max_tokens
        if payload.temperature is not None:
            settings["temperature"] = payload.temperature
        if payload.top_k is not None:
            settings["top_k"] = payload.top_k
        resolved_web_search = payload.allow_web_search if payload.allow_web_search is not None else payload.allow_online_search
        if resolved_web_search is not None:
            settings["allow_web_search"] = resolved_web_search
        resolved_self_learning = payload.enable_self_learning if payload.enable_self_learning is not None else payload.self_learning_enabled
        if resolved_self_learning is not None:
            settings["enable_self_learning"] = resolved_self_learning
        resolved_cloud_memory = payload.share_memory_with_cloud if payload.share_memory_with_cloud is not None else payload.cloud_memory_consent
        if resolved_cloud_memory is not None:
            settings["share_memory_with_cloud"] = resolved_cloud_memory
        if payload.checkpoint_path.strip():
            settings["checkpoint_path"] = payload.checkpoint_path.strip()
        if payload.voice_label is not None:
            settings["voice_label"] = payload.voice_label
        if payload.voice_speed is not None:
            settings["voice_speed"] = payload.voice_speed
        if payload.auto_pace_enabled is not None:
            settings["auto_pace_enabled"] = payload.auto_pace_enabled
        if payload.auto_read_replies is not None:
            settings["auto_read_replies"] = payload.auto_read_replies

    _merge_settings(_apply)

    if payload.gemini_vision_api_key and payload.gemini_vision_api_key.strip():
        try:
            core.gemini_vision_cred.set_key(payload.gemini_vision_api_key.strip())
        except Exception as error:
            logger.error("Failed to save the Gemini vision API key: %s", error)
            return {"status": "error", "message": str(error)}

    if backend_val is None:
        return {"status": "success", "message": "Settings saved."}

    if payload.opencode_api_key and payload.opencode_api_key.strip():
        core.opencode_cred.set_key(payload.opencode_api_key)
    elif payload.api_key.strip() and backend_val == BACKEND_OPENCODE:
        core.opencode_cred.set_key(payload.api_key)

    if payload.kilo_api_key and payload.kilo_api_key.strip():
        core.kilo_cred.set_key(payload.kilo_api_key)
    elif payload.api_key.strip() and backend_val == BACKEND_KILO:
        core.kilo_cred.set_key(payload.api_key)

    core.runner.select_backend(backend_val)
    
    api_key = payload.api_key
    if backend_val in (BACKEND_OPENCODE, BACKEND_KILO) and not api_key.strip():
        store = core.opencode_cred if backend_val == BACKEND_OPENCODE else core.kilo_cred
        try:
            api_key = store.get_key() or ""
        except Exception as error:
            logger.warning("Could not read the saved API key: %s", error)

    model_name = payload.model or ""
    if backend_val == BACKEND_OPENCODE and payload.opencode_model:
        model_name = payload.opencode_model
    elif backend_val == BACKEND_KILO and payload.kilo_model:
        model_name = payload.kilo_model

    try:
        result = core.runner.load(
            checkpoint_path=payload.checkpoint_path or "",
            api_key=api_key,
            model_name=model_name,
            api_variant=payload.api_variant or "",
        )
        return {"status": "success", "message": result}
    except Exception as error:
        logger.error("Failed to load backend %s: %s", payload.backend, error)
        return {"status": "error", "message": str(error)}
