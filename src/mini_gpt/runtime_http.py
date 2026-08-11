from __future__ import annotations

import asyncio
import inspect
import json
from dataclasses import dataclass
from typing import Any, Callable, Mapping
from urllib.parse import parse_qs, urlsplit


RUNTIME_ROUTE_KEYS = frozenset(
    {
        ("GET", "/status"),
        ("GET", "/models"),
        ("GET", "/voice/status"),
        ("POST", "/voice/latency"),
        ("POST", "/voice/tts"),
        ("POST", "/voice/warmup"),
        ("POST", "/voice/release"),
        ("POST", "/voice/transcribe"),
        ("GET", "/vts/mouse-look"),
        ("POST", "/vts/mouse-look"),
        ("GET", "/vts/overlay"),
        ("POST", "/vts/overlay"),
        ("GET", "/vts/status"),
        ("POST", "/vts/connect"),
        ("GET", "/vts/config"),
        ("POST", "/vts/config"),
        ("POST", "/vts/activity"),
        ("POST", "/vts/action"),
        ("POST", "/vts/speaking"),
        ("POST", "/observation/preflight"),
        ("GET", "/observation/vision-config"),
        ("POST", "/observation/react"),
        ("POST", "/observation/cancel"),
        ("POST", "/observation/ack"),
        ("POST", "/observation/start"),
        ("POST", "/observation/stop"),
        ("GET", "/observation/status"),
    }
)


@dataclass(frozen=True, slots=True)
class RuntimeHttpResponse:
    status_code: int
    content_type: str
    body: bytes
    headers: Mapping[str, str] | None = None


def dispatch_runtime_request(
    method: str,
    path_and_query: str,
    _content_type: str | None,
    body: str,
) -> RuntimeHttpResponse:
    """Dispatch a private runtime command through the compatibility services.

    This module is deliberately transport-neutral. During migration it calls
    the existing Python route services directly; ASP.NET remains the only
    public HTTP owner when this function is reached through worker protocol v1.
    """
    from mini_gpt.routers import observation, status, voice, vts

    normalized_method = method.upper()
    parsed = urlsplit(path_and_query)
    path = parsed.path
    query = parse_qs(parsed.query, keep_blank_values=True)
    payload = _parse_json_body(body) if normalized_method in {"POST", "PUT", "PATCH"} else None

    handlers: dict[tuple[str, str], Callable[[], Any]] = {
        ("GET", "/status"): status.get_status,
        ("GET", "/models"): lambda: status.get_models(_query_text(query, "backend")),
        ("GET", "/voice/status"): voice.voice_status,
        ("POST", "/voice/latency"): lambda: voice.voice_latency(
            voice.VoiceLatencyPayload.model_validate(payload)
        ),
        ("POST", "/voice/tts"): lambda: voice.voice_tts(
            voice.VoiceTtsPayload.model_validate(payload)
        ),
        ("POST", "/voice/warmup"): voice.voice_warmup,
        ("POST", "/voice/release"): voice.voice_release,
        ("POST", "/voice/transcribe"): lambda: voice.voice_transcribe(
            voice.TranscribePayload.model_validate(payload)
        ),
        ("GET", "/vts/mouse-look"): vts.vts_mouse_look_get,
        ("POST", "/vts/mouse-look"): lambda: vts.vts_mouse_look_save(
            vts.VtsMouseLookPayload.model_validate(payload)
        ),
        ("GET", "/vts/overlay"): vts.vts_overlay_get,
        ("POST", "/vts/overlay"): lambda: vts.vts_overlay_save(
            vts.VtsOverlayPayload.model_validate(payload)
        ),
        ("GET", "/vts/status"): vts.vts_status,
        ("POST", "/vts/connect"): vts.vts_connect,
        ("GET", "/vts/config"): vts.vts_config_get,
        ("POST", "/vts/config"): lambda: vts.vts_config_save(
            vts.VtsConfigPayload.model_validate(payload)
        ),
        ("POST", "/vts/activity"): lambda: vts.vts_activity(
            vts.VtsActivityPayload.model_validate(payload)
        ),
        ("POST", "/vts/action"): lambda: vts.vts_action(
            vts.VtsActionPayload.model_validate(payload)
        ),
        ("POST", "/vts/speaking"): lambda: vts.vts_speaking(
            vts.VtsSpeakingPayload.model_validate(payload)
        ),
        ("POST", "/observation/preflight"): observation.observation_preflight,
        ("GET", "/observation/vision-config"): observation.observation_vision_config,
        ("POST", "/observation/react"): lambda: observation.observation_react(
            observation.ObservationReactionPayload.model_validate(payload)
        ),
        ("POST", "/observation/cancel"): lambda: observation.observation_cancel(
            observation.ObservationCancelPayload.model_validate(payload)
        ),
        ("POST", "/observation/ack"): lambda: observation.observation_ack(
            observation.ObservationAckPayload.model_validate(payload)
        ),
        ("POST", "/observation/start"): observation.observation_start,
        ("POST", "/observation/stop"): observation.observation_stop,
        ("GET", "/observation/status"): observation.observation_status,
    }
    handler = handlers.get((normalized_method, path))
    if frozenset(handlers) != RUNTIME_ROUTE_KEYS:
        raise RuntimeError("runtime route table drift")
    if handler is None:
        return _json_response(
            {"status": "error", "message": "Runtime route is not available."},
            status_code=404,
        )

    try:
        result = handler()
        if inspect.isawaitable(result):
            result = asyncio.run(result)
        return _coerce_response(result)
    except Exception as error:
        return _validation_or_error_response(error)


def _parse_json_body(body: str) -> object:
    if not body.strip():
        return {}
    try:
        return json.loads(body)
    except json.JSONDecodeError:
        return None


def _query_text(query: Mapping[str, list[str]], key: str) -> str:
    values = query.get(key)
    return values[0] if values else ""


def _coerce_response(value: object) -> RuntimeHttpResponse:
    try:
        from starlette.responses import Response
    except ImportError:
        Response = None  # type: ignore[assignment,misc]

    if Response is not None and isinstance(value, Response):
        content_type = value.headers.get("content-type", value.media_type or "")
        headers = {
            key: header_value
            for key, header_value in value.headers.items()
            if key.casefold() not in {"content-length", "content-type"}
        }
        return RuntimeHttpResponse(
            int(value.status_code),
            content_type,
            bytes(value.body),
            headers,
        )
    return _json_response(value)


def _validation_or_error_response(error: Exception) -> RuntimeHttpResponse:
    try:
        from fastapi import HTTPException
        from pydantic import ValidationError
    except ImportError:
        HTTPException = ()  # type: ignore[assignment,misc]
        ValidationError = ()  # type: ignore[assignment,misc]

    if ValidationError and isinstance(error, ValidationError):
        return _json_response({"detail": "Invalid runtime request."}, status_code=422)
    if HTTPException and isinstance(error, HTTPException):
        return _json_response({"detail": error.detail}, status_code=int(error.status_code))
    return _json_response(
        {"status": "error", "message": "Runtime request failed."},
        status_code=500,
    )


def _json_response(value: object, *, status_code: int = 200) -> RuntimeHttpResponse:
    return RuntimeHttpResponse(
        status_code,
        "application/json",
        json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8"),
        {},
    )
