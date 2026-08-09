from __future__ import annotations

import threading

from fastapi import APIRouter
from pydantic import BaseModel, Field

from .._server_shared import (
    BACKEND_GGUF,
    BACKEND_KILO,
    BACKEND_OPENCODE,
    _merge_settings,
    _resolve_backend,
    _schedule_cloud_voice_warmup,
    core,
    effective_settings,
    logger,
)
from ..voice_style import VoiceStyleId

router = APIRouter()
_BACKEND_TRANSITION_LOCK = threading.RLock()


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
    kilo_reasoning_enabled: bool | None = None
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
    voice_style_id: VoiceStyleId | None = None
    voice_speed: float | None = Field(default=None, ge=0.5, le=2.0)
    auto_pace_enabled: bool | None = None
    auto_read_replies: bool | None = None
    show_subtitles: bool | None = None
    gemini_vision_api_key: str = Field(default="", max_length=4096)


@router.get("/settings")
def get_settings():
    settings = effective_settings()
    backend = settings.get("backend", BACKEND_GGUF)
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
        "cloud_memory_consent": settings.get("share_memory_with_cloud", True),
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

    if backend_val is None:
        return _save_settings_unlocked(payload, backend_val)
    # ModelRunner stores the selected backend as mutable instance state. Keep
    # the complete select/load/persist/rollback transition atomic so a second
    # settings request cannot load or persist against the first one's backend.
    with _BACKEND_TRANSITION_LOCK:
        return _save_settings_unlocked(payload, backend_val)


def _save_settings_unlocked(payload: SettingsPayload, backend_val: str | None):

    resolved_max_tokens = (
        payload.max_tokens if payload.max_tokens is not None else payload.max_new_tokens
    )
    resolved_web_search = (
        payload.allow_web_search
        if payload.allow_web_search is not None
        else payload.allow_online_search
    )
    resolved_self_learning = (
        payload.enable_self_learning
        if payload.enable_self_learning is not None
        else payload.self_learning_enabled
    )
    resolved_cloud_memory = (
        payload.share_memory_with_cloud
        if payload.share_memory_with_cloud is not None
        else payload.cloud_memory_consent
    )

    def _apply_backend_configuration(settings: dict) -> None:
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
        if payload.kilo_reasoning_enabled is not None:
            settings["kilo_reasoning_enabled"] = payload.kilo_reasoning_enabled
        if payload.checkpoint_path.strip():
            settings["checkpoint_path"] = payload.checkpoint_path.strip()

    def _apply_independent_preferences(settings: dict) -> None:
        if resolved_max_tokens is not None:
            settings["max_tokens"] = resolved_max_tokens
        if payload.temperature is not None:
            settings["temperature"] = payload.temperature
        if payload.top_k is not None:
            settings["top_k"] = payload.top_k
        if resolved_web_search is not None:
            settings["allow_web_search"] = resolved_web_search
        if resolved_self_learning is not None:
            settings["enable_self_learning"] = resolved_self_learning
        if resolved_cloud_memory is not None:
            settings["share_memory_with_cloud"] = resolved_cloud_memory
        if payload.voice_label is not None:
            settings["voice_label"] = payload.voice_label
        if payload.voice_style_id is not None:
            settings["voice_style_id"] = payload.voice_style_id
        if payload.voice_speed is not None:
            settings["voice_speed"] = payload.voice_speed
        if payload.auto_pace_enabled is not None:
            settings["auto_pace_enabled"] = payload.auto_pace_enabled
        if payload.auto_read_replies is not None:
            settings["auto_read_replies"] = payload.auto_read_replies
        if payload.show_subtitles is not None:
            settings["show_subtitles"] = payload.show_subtitles

    def _apply(settings: dict) -> None:
        _apply_backend_configuration(settings)
        _apply_independent_preferences(settings)

    # Keep a candidate in memory while a backend is loading. Persisting before
    # that load completes made a failed backend/model switch survive restart.
    candidate_settings = effective_settings()
    _apply(candidate_settings)

    independent_preference_change = any(
        value is not None
        for value in (
            resolved_max_tokens,
            payload.temperature,
            payload.top_k,
            resolved_web_search,
            resolved_self_learning,
            resolved_cloud_memory,
            payload.voice_label,
            payload.voice_style_id,
            payload.voice_speed,
            payload.auto_pace_enabled,
            payload.auto_read_replies,
            payload.show_subtitles,
        )
    )

    if backend_val is None:
        _merge_settings(_apply)
    elif independent_preference_change:
        # A failed backend/model switch must not become the saved runtime
        # configuration, but independent user preferences remain valid.
        _merge_settings(_apply_independent_preferences)

    voice_only_save = backend_val is None and any(
        value is not None
        for value in (
            payload.voice_label,
            payload.voice_style_id,
            payload.voice_speed,
            payload.auto_pace_enabled,
            payload.auto_read_replies,
        )
    )

    if payload.gemini_vision_api_key and payload.gemini_vision_api_key.strip():
        try:
            core.gemini_vision_cred.set_key(payload.gemini_vision_api_key.strip())
        except Exception as error:
            logger.error("Failed to save the Gemini vision API key: %s", error)
            return {"status": "error", "message": str(error)}

    if backend_val is None:
        if voice_only_save:
            _schedule_cloud_voice_warmup()
        return {"status": "success", "message": "Settings saved."}

    legacy_api_key = payload.api_key.strip()
    opencode_api_key = payload.opencode_api_key.strip()
    kilo_api_key = payload.kilo_api_key.strip()
    if not opencode_api_key and backend_val == BACKEND_OPENCODE:
        opencode_api_key = legacy_api_key
    if not kilo_api_key and backend_val == BACKEND_KILO:
        kilo_api_key = legacy_api_key
    credential_updates: list[tuple[object, str]] = []
    if opencode_api_key:
        credential_updates.append((core.opencode_cred, opencode_api_key))
    if kilo_api_key:
        credential_updates.append((core.kilo_cred, kilo_api_key))

    api_key = ""
    if backend_val == BACKEND_OPENCODE:
        api_key = opencode_api_key
    elif backend_val == BACKEND_KILO:
        api_key = kilo_api_key
    if backend_val in (BACKEND_OPENCODE, BACKEND_KILO) and not api_key:
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

    def restore_credentials(snapshots: list[tuple[object, str | None]]) -> None:
        for store, previous_key in reversed(snapshots):
            try:
                if previous_key is None:
                    store.delete_key()
                else:
                    store.set_key(previous_key)
            except Exception as rollback_error:
                logger.error("Could not restore a backend credential: %s", rollback_error)

    def persist_credentials() -> list[tuple[object, str | None]]:
        snapshots = [(store, store.get_key()) for store, _ in credential_updates]
        try:
            for store, key in credential_updates:
                store.set_key(key)
        except Exception:
            restore_credentials(snapshots)
            raise
        return snapshots

    previous_runner_state = {
        "backend": core.runner.backend,
        "checkpoint_path": core.runner.checkpoint_path,
        "openai_runtime": core.runner.openai_runtime,
        "kilo_reasoning_enabled": core.runner.kilo_reasoning_enabled,
    }
    credential_snapshots: list[tuple[object, str | None]] = []
    try:
        core.runner.select_backend(backend_val)
        result = core.runner.load(
            checkpoint_path=payload.checkpoint_path or "",
            api_key=api_key,
            model_name=model_name,
            api_variant=payload.api_variant or "",
            kilo_reasoning_enabled=candidate_settings["kilo_reasoning_enabled"],
        )
        credential_snapshots = persist_credentials()
        _merge_settings(_apply_backend_configuration)
        if backend_val in (BACKEND_OPENCODE, BACKEND_KILO):
            _schedule_cloud_voice_warmup()
        return {"status": "success", "message": result}
    except Exception as error:
        logger.error("Failed to load backend %s: %s", payload.backend, error)
        if credential_snapshots:
            restore_credentials(credential_snapshots)
        try:
            core.runner.select_backend(previous_runner_state["backend"])
            core.runner.checkpoint_path = previous_runner_state["checkpoint_path"]
            core.runner.openai_runtime = previous_runner_state["openai_runtime"]
            core.runner.kilo_reasoning_enabled = previous_runner_state[
                "kilo_reasoning_enabled"
            ]
            logger.warning(
                "Rolled back backend to %r after a failed load",
                previous_runner_state["backend"],
            )
        except Exception as rollback_error:
            logger.error(
                "Rollback to backend %r failed: %s",
                previous_runner_state["backend"],
                rollback_error,
            )
        return {"status": "error", "message": str(error)}
