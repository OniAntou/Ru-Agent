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


class RuntimeRequestError(ValueError):
    def __init__(self, message: str = "Invalid runtime request.", status_code: int = 422):
        self.status_code = status_code
        super().__init__(message)


def dispatch_runtime_request(
    method: str,
    path_and_query: str,
    _content_type: str | None,
    body: str,
) -> RuntimeHttpResponse:
    """Dispatch a private runtime command through transport-neutral services.

    ASP.NET remains the only public HTTP owner when this function is reached
    through worker protocol v1.
    """
    from mini_gpt import runtime_observation as observation
    from mini_gpt import runtime_status as status
    from mini_gpt import runtime_voice as voice
    from mini_gpt import runtime_vts as vts

    normalized_method = method.upper()
    parsed = urlsplit(path_and_query)
    path = parsed.path
    query = parse_qs(parsed.query, keep_blank_values=True)
    try:
        payload = (
            _parse_json_body(body)
            if normalized_method in {"POST", "PUT", "PATCH"}
            else None
        )
    except RuntimeRequestError as error:
        return _validation_or_error_response(error)

    handlers: dict[tuple[str, str], Callable[[], Any]] = {
        ("GET", "/status"): status.get_status,
        ("GET", "/models"): lambda: status.get_models(_query_text(query, "backend")),
        ("GET", "/voice/status"): voice.voice_status,
        ("POST", "/voice/latency"): lambda: voice.voice_latency(payload),
        ("POST", "/voice/tts"): lambda: voice.voice_tts(payload),
        ("POST", "/voice/warmup"): voice.voice_warmup,
        ("POST", "/voice/release"): voice.voice_release,
        ("POST", "/voice/transcribe"): lambda: voice.voice_transcribe(payload),
        ("GET", "/vts/mouse-look"): vts.vts_mouse_look_get,
        ("POST", "/vts/mouse-look"): lambda: vts.vts_mouse_look_save(payload),
        ("GET", "/vts/overlay"): vts.vts_overlay_get,
        ("POST", "/vts/overlay"): lambda: vts.vts_overlay_save(payload),
        ("GET", "/vts/status"): vts.vts_status,
        ("POST", "/vts/connect"): vts.vts_connect,
        ("GET", "/vts/config"): vts.vts_config_get,
        ("POST", "/vts/config"): lambda: vts.vts_config_save(payload),
        ("POST", "/vts/activity"): lambda: vts.vts_activity(payload),
        ("POST", "/vts/action"): lambda: vts.vts_action(payload),
        ("POST", "/vts/speaking"): lambda: vts.vts_speaking(payload),
        ("POST", "/observation/preflight"): observation.observation_preflight,
        ("GET", "/observation/vision-config"): observation.observation_vision_config,
        ("POST", "/observation/react"): lambda: observation.observation_react(payload),
        ("POST", "/observation/cancel"): lambda: observation.observation_cancel(payload),
        ("POST", "/observation/ack"): lambda: observation.observation_ack(payload),
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
        raise RuntimeRequestError() from None


def _query_text(query: Mapping[str, list[str]], key: str) -> str:
    values = query.get(key)
    return values[0] if values else ""


def _coerce_response(value: object) -> RuntimeHttpResponse:
    if isinstance(value, RuntimeHttpResponse):
        return value
    return _json_response(value)


def _validation_or_error_response(error: Exception) -> RuntimeHttpResponse:
    if isinstance(error, RuntimeRequestError):
        return _json_response({"detail": str(error)}, status_code=error.status_code)
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
