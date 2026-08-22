"""Private-protocol worker used by the backend Harness.

This module deliberately contains no model, microphone, speaker, or VTube
Studio runtime imports. It only supplies fixed protocol fixtures.
"""

from __future__ import annotations

import asyncio
import argparse
import base64
import json
import os
from collections.abc import AsyncIterator, Iterator, Mapping
from dataclasses import dataclass
from typing import Any

from ru_runtime.worker.v1.protocol import (
    CHAT_CHUNK,
    CHAT_DONE,
    CHAT_RESET,
    CHAT_TOOL_REQUEST,
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

from .voice_fixtures import build_synthetic_wav, is_valid_transcription_fixture


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


def _harness_delay_seconds(name: str) -> float:
    raw = os.environ.get(name, "0")
    try:
        milliseconds = min(max(int(raw), 0), 5_000)
    except (TypeError, ValueError):
        milliseconds = 0
    return milliseconds / 1000.0


async def _chat(_payload: Mapping[str, Any], _cancelled: asyncio.Event) -> AsyncIterator[WorkerReply]:
    settings = _payload.get("settings")
    tool_queue = _payload.get("__worker_tool_result_queue")
    agent_mode = isinstance(settings, Mapping) and settings.get("agent_mode") is True
    if agent_mode and isinstance(tool_queue, asyncio.Queue):
        session_id = str(_payload.get("session_id") or "harness-agent-session")
        correlation_id = str(
            _payload.get("__worker_correlation_id") or "harness-agent-correlation"
        )
        tool_request_id = "harness-tool-volume-invalid"
        yield WorkerReply(CHAT_RESET, {})
        yield WorkerReply(
            CHAT_TOOL_REQUEST,
            {
                "request_id": tool_request_id,
                "session_id": session_id,
                "correlation_id": correlation_id,
                "tool_id": "system.volume",
                "tool_version": "1.0.0",
                "capability_class": "native_mutation",
                "arguments": {"operation": "set", "level": 101},
                "deadline_ms": 30_000,
            },
        )
        try:
            result = await asyncio.wait_for(tool_queue.get(), timeout=30.0)
        except asyncio.TimeoutError:
            result = {"status": "timed_out", "safe_summary": "Tool timed out."}
        valid_result = (
            isinstance(result, Mapping)
            and result.get("request_id") == tool_request_id
            and result.get("session_id") == session_id
            and result.get("correlation_id") == correlation_id
            and result.get("status") == "failed"
        )
        if not valid_result:
            # The visible fixture remains truthful even if the private receipt is malformed.
            result = None
        yield WorkerReply(
            CHAT_DONE,
            {"raw_reply": "Không thể thực hiện thay đổi âm lượng."},
        )
        return

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


async def _runtime(payload: Mapping[str, Any], cancelled: asyncio.Event) -> AsyncIterator[WorkerReply]:
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
        delay = _harness_delay_seconds("RU_HARNESS_TRANSCRIBE_DELAY_MS")
        if delay:
            await asyncio.sleep(delay)
        if cancelled.is_set():
            yield _json_response({"status": "error", "message": "cancelled"}, 499)
            return
        encoded = body.get("audio_b64")
        sample_rate = body.get("sample_rate", 16_000)
        valid = False
        if isinstance(encoded, str) and sample_rate == 16_000:
            try:
                valid = is_valid_transcription_fixture(
                    base64.b64decode(encoded, validate=True)
                )
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
        delay = _harness_delay_seconds("RU_HARNESS_VOICE_DELAY_MS")
        if delay:
            await asyncio.sleep(delay)
        audio = build_synthetic_wav()
        if cancelled.is_set():
            yield WorkerReply(VOICE_DONE, {"state": "cancelled", "request_id": request_id})
            return
        yield WorkerReply(
            VOICE_CONTROL,
            {
                "type": "audio",
                "state": "audio_ready",
                "request_id": request_id,
            },
            binary=audio,
        )
        yield WorkerReply(
            VOICE_DONE,
            {
                "type": "ended",
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


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Run the Ru deterministic Harness worker")
    parser.add_argument("--port", type=int, required=True)
    args = parser.parse_args(argv)
    import os

    token = os.environ.get("RU_WORKER_TOKEN", "").strip()
    if not token:
        parser.error("RU_WORKER_TOKEN is required")
    return serve_deterministic_worker(args.port, token)


if __name__ == "__main__":
    raise SystemExit(main())
