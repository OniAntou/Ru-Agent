"""Private-protocol worker used by the backend Harness.

This module deliberately contains no model, microphone, speaker, or VTube
Studio runtime imports. It only supplies fixed protocol fixtures.
"""

from __future__ import annotations

import asyncio
import base64
import json
from collections.abc import AsyncIterator, Iterator, Mapping
from dataclasses import dataclass
from typing import Any

from ru_runtime.worker.v1.protocol import (
    CHAT_CHUNK,
    CHAT_DONE,
    CHAT_RESET,
    ERROR,
    MEMORY_FACTS,
    RUNTIME_RESPONSE,
    RUNTIME_STATE,
    VOICE_CONTROL,
    VOICE_DONE,
    VTS_ACTION,
    VTS_CATALOG,
)
from ru_runtime.worker.v1.server import WorkerHandlers, WorkerReply, serve_worker

from .voice_fixtures import build_synthetic_wav


@dataclass(frozen=True, slots=True)
class DeterministicVoiceEvent:
    type: str
    request_id: str
    binary: bytes | None = None


def collect_deterministic_stream(request_id: str) -> Iterator[DeterministicVoiceEvent]:
    audio = build_synthetic_wav()
    yield DeterministicVoiceEvent("audio", request_id, audio)
    yield DeterministicVoiceEvent("ended", request_id)


def deterministic_worker_dependencies() -> dict[str, bool]:
    return {"model": False, "microphone": False, "speaker": False, "vts": False}


async def _chat(_payload: Mapping[str, Any], _cancelled: asyncio.Event) -> AsyncIterator[WorkerReply]:
    yield WorkerReply(CHAT_RESET, {})
    yield WorkerReply(CHAT_CHUNK, {"text": "Em nghe đây. [[VT"})
    yield WorkerReply(
        CHAT_DONE,
        {
            "raw_reply": (
                "Em nghe đây. [[EMO:joy]] [[VTS:vts_01]] "
                "[[VOICE_PACE:quick]]"
            )
        },
    )


async def _memory(_payload: Mapping[str, Any], _cancelled: asyncio.Event) -> AsyncIterator[WorkerReply]:
    yield WorkerReply(MEMORY_FACTS, {"facts": []})


def _json_response(payload: Mapping[str, object], status_code: int = 200) -> WorkerReply:
    return WorkerReply(
        RUNTIME_RESPONSE,
        {
            "status_code": status_code,
            "content_type": "application/json",
            "headers": {},
            "body": json.dumps(dict(payload), ensure_ascii=False),
        },
    )


def _audio_response() -> WorkerReply:
    return WorkerReply(
        RUNTIME_RESPONSE,
        {
            "status_code": 200,
            "content_type": "audio/wav",
            "headers": {},
            "binary": True,
        },
        binary=build_synthetic_wav(),
    )


def _decode_json_body(payload: Mapping[str, Any]) -> Mapping[str, Any]:
    body = payload.get("body", "")
    if not isinstance(body, str) or not body:
        return {}
    decoded = json.loads(body)
    if not isinstance(decoded, Mapping):
        raise ValueError("request body must be an object")
    return decoded


async def _runtime(payload: Mapping[str, Any], _cancelled: asyncio.Event) -> AsyncIterator[WorkerReply]:
    if "method" not in payload and "path_and_query" not in payload:
        yield WorkerReply(
            RUNTIME_STATE,
            {"status": "success", "message": "Runtime loaded.", "backend": "harness"},
        )
        return

    path = str(payload.get("path_and_query") or "").split("?", 1)[0]
    try:
        body = _decode_json_body(payload)
    except (TypeError, ValueError, json.JSONDecodeError):
        yield _json_response({"status": "error", "message": "invalid fixture request"}, 422)
        return

    if path == "/voice/transcribe":
        encoded = body.get("audio_b64")
        sample_rate = body.get("sample_rate", 16_000)
        valid = False
        if isinstance(encoded, str) and sample_rate == 16_000:
            try:
                valid = base64.b64decode(encoded, validate=True) == build_synthetic_wav()
            except (ValueError, base64.binascii.Error):
                valid = False
        yield _json_response(
            {"status": "ok", "text": "Harness voice turn"}
            if valid
            else {"status": "error", "message": "invalid audio payload"}
        )
        return
    if path == "/voice/tts":
        yield _audio_response()
        return
    if path == "/status":
        yield _json_response({"status": "ok", "backend": "harness", "ready": True})
        return
    if path == "/models":
        yield _json_response({"status": "ok", "models": []})
        return
    if path == "/voice/status":
        yield _json_response(
            {
                "status": "ok",
                "voice_label": "Ru (OmniVoice)",
                "voice_style_id": "original",
                "voice_speed": 1.0,
                "auto_read_replies": True,
                "server_running": True,
                "show_subtitles": False,
            }
        )
        return
    if path.startswith("/vts/"):
        yield _json_response({"status": "ok", "enabled": False, "sensitivity": 1.0})
        return
    yield _json_response({"status": "ok"})


async def _voice(payload: Mapping[str, Any], cancelled: asyncio.Event) -> AsyncIterator[WorkerReply]:
    operation = payload.get("operation")
    request_id = str(payload.get("request_id") or "")
    if operation in {"stream", "tts"}:
        audio = build_synthetic_wav()
        if cancelled.is_set():
            yield WorkerReply(VOICE_DONE, {"state": "cancelled", "request_id": request_id})
            return
        yield WorkerReply(
            VOICE_CONTROL,
            {"state": "audio_ready", "request_id": request_id},
            binary=audio,
        )
        yield WorkerReply(
            VOICE_DONE,
            {
                "state": "ended" if operation == "stream" else "done",
                "request_id": request_id,
            },
        )
        return
    if operation in {"warmup", "release"}:
        yield WorkerReply(VOICE_DONE, {"state": "done", "request_id": request_id})
        return
    yield WorkerReply(ERROR, {"code": "unsupported_operation", "retryable": False})


async def _vts(payload: Mapping[str, Any], _cancelled: asyncio.Event) -> AsyncIterator[WorkerReply]:
    if payload.get("operation") == "catalog":
        yield WorkerReply(
            VTS_CATALOG,
            {
                "model_id": "synthetic-model",
                "model_name": "synthetic",
                "hotkey_codes": ["vts_01"],
                "expression_codes": [],
            },
        )
        return
    if payload.get("operation") == "action":
        yield WorkerReply(VTS_ACTION, {"status": "ok"})
        return
    yield WorkerReply(ERROR, {"code": "unsupported_operation", "retryable": False})


def create_deterministic_handlers() -> WorkerHandlers:
    return WorkerHandlers(
        chat=_chat,
        memory_extract=_memory,
        runtime=_runtime,
        voice=_voice,
        vts=_vts,
    )


def serve_deterministic_worker(port: int, token: str) -> int:
    asyncio.run(serve_worker("127.0.0.1", port, token, create_deterministic_handlers()))
    return 0
