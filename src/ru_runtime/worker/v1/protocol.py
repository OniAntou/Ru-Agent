from __future__ import annotations

import hashlib
import json
from collections.abc import Mapping
from typing import Any

PROTOCOL_VERSION = 1
MAX_REQUEST_ID_CHARS = 128

HELLO = "hello"
HELLO_OK = "hello.ok"
CHAT_START = "chat.start"
CHAT_RESET = "chat.reset"
CHAT_CHUNK = "chat.chunk"
CHAT_DONE = "chat.done"
CHAT_CANCEL = "chat.cancel"
CHAT_TOOL_REQUEST = "chat.tool.request"
CHAT_TOOL_RESULT = "chat.tool.result"
CHAT_TOOL_CANCEL = "chat.tool.cancel"
MEMORY_EXTRACT = "memory.extract"
MEMORY_FACTS = "memory.facts"
RUNTIME_LOAD = "runtime.load"
RUNTIME_STATE = "runtime.state"
RUNTIME_REQUEST = "runtime.request"
RUNTIME_RESPONSE = "runtime.response"
VOICE_START = "voice.start"
VOICE_CONTROL = "voice.control"
VOICE_DONE = "voice.done"
VTS_CATALOG = "vts.catalog"
VTS_ACTION = "vts.action"
WORKER_HEALTH = "worker.health"
WORKER_HEALTH_OK = "worker.health.ok"
ERROR = "error"

TOOL_RESULT_STATUSES = frozenset(
    {"succeeded", "failed", "denied", "approval_required", "cancelled", "timed_out"}
)
MAX_TOOL_SUMMARY_CHARS = 4096
MAX_TOOL_ARGUMENTS_BYTES = 64 * 1024
MAX_TOOL_OUTPUT_BYTES = 256 * 1024

KNOWN_TYPES = frozenset(
    {
        HELLO,
        HELLO_OK,
        CHAT_START,
        CHAT_RESET,
        CHAT_CHUNK,
        CHAT_DONE,
        CHAT_CANCEL,
        CHAT_TOOL_REQUEST,
        CHAT_TOOL_RESULT,
        CHAT_TOOL_CANCEL,
        MEMORY_EXTRACT,
        MEMORY_FACTS,
        RUNTIME_LOAD,
        RUNTIME_STATE,
        RUNTIME_REQUEST,
        RUNTIME_RESPONSE,
        VOICE_START,
        VOICE_CONTROL,
        VOICE_DONE,
        VTS_CATALOG,
        VTS_ACTION,
        WORKER_HEALTH,
        WORKER_HEALTH_OK,
        ERROR,
    }
)

ORDERED_RESPONSE_TYPES = frozenset(
    {
        HELLO_OK,
        CHAT_RESET,
        CHAT_CHUNK,
        CHAT_DONE,
        MEMORY_FACTS,
        RUNTIME_STATE,
        RUNTIME_RESPONSE,
        VOICE_CONTROL,
        VOICE_DONE,
        WORKER_HEALTH_OK,
    }
)


class ProtocolError(ValueError):
    def __init__(self, code: str) -> None:
        self.code = code
        super().__init__(code)


def validate_envelope(value: object) -> dict[str, Any]:
    if not isinstance(value, Mapping):
        raise ProtocolError("invalid_envelope")

    version = value.get("v")
    if isinstance(version, bool) or not isinstance(version, int):
        raise ProtocolError("invalid_version")
    if version != PROTOCOL_VERSION:
        raise ProtocolError("unsupported_version")

    request_id = value.get("id")
    if (
        not isinstance(request_id, str)
        or not request_id.strip()
        or len(request_id) > MAX_REQUEST_ID_CHARS
    ):
        raise ProtocolError("invalid_id")

    message_type = value.get("type")
    if not isinstance(message_type, str) or message_type not in KNOWN_TYPES:
        raise ProtocolError("unknown_type")

    payload = value.get("payload")
    if not isinstance(payload, Mapping):
        raise ProtocolError("invalid_payload")

    if message_type == CHAT_TOOL_REQUEST:
        _validate_tool_request_payload(payload)
    elif message_type == CHAT_TOOL_RESULT:
        _validate_tool_result_payload(payload)
    elif message_type == CHAT_TOOL_CANCEL:
        _validate_tool_context_payload(payload, "invalid_tool_cancel")
        _validate_tool_request_id(payload, "invalid_tool_cancel")

    sequence = value.get("seq")
    if sequence is not None and (
        isinstance(sequence, bool) or not isinstance(sequence, int) or sequence < 0
    ):
        raise ProtocolError("invalid_sequence")
    if message_type in ORDERED_RESPONSE_TYPES and sequence is None:
        raise ProtocolError("missing_sequence")

    normalized: dict[str, Any] = {
        "v": version,
        "id": request_id,
        "type": message_type,
        "payload": dict(payload),
    }
    if sequence is not None:
        normalized["seq"] = sequence

    correlation_id = value.get("correlation_id") or value.get("correlationId")
    if correlation_id is not None:
        if not isinstance(correlation_id, str) or not correlation_id.strip() or len(correlation_id) > 64:
            raise ProtocolError("invalid_correlation_id")
        normalized["correlation_id"] = correlation_id.strip()

    return normalized


def encode_envelope(value: object) -> str:
    envelope = validate_envelope(value)
    return json.dumps(envelope, ensure_ascii=False, separators=(",", ":"))


def _validate_tool_request_payload(payload: Mapping[str, Any]) -> None:
    _validate_tool_context_payload(payload, "invalid_tool_request")
    _validate_tool_request_id(payload, "invalid_tool_request")
    for key in ("tool_id", "tool_version"):
        value = payload.get(key)
        if not isinstance(value, str) or not value.strip() or len(value) > 128:
            raise ProtocolError("invalid_tool_request")

    arguments = payload.get("arguments")
    if not isinstance(arguments, Mapping):
        raise ProtocolError("invalid_tool_request")
    if len(json.dumps(arguments, ensure_ascii=False, separators=(",", ":")).encode("utf-8")) > MAX_TOOL_ARGUMENTS_BYTES:
        raise ProtocolError("invalid_tool_request")
    deadline_ms = payload.get("deadline_ms")
    if (
        isinstance(deadline_ms, bool)
        or not isinstance(deadline_ms, int)
        or deadline_ms < 1
        or deadline_ms > 300_000
    ):
        raise ProtocolError("invalid_tool_request")


def _validate_tool_result_payload(payload: Mapping[str, Any]) -> None:
    _validate_tool_request_id(payload, "invalid_tool_result")
    status = payload.get("status")
    output = payload.get("output")
    safe_summary = payload.get("safe_summary")
    if status not in TOOL_RESULT_STATUSES or not isinstance(output, Mapping):
        raise ProtocolError("invalid_tool_result")
    try:
        output_bytes = len(
            json.dumps(output, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        )
    except (TypeError, ValueError, OverflowError) as error:
        raise ProtocolError("invalid_tool_result") from error
    if output_bytes > MAX_TOOL_OUTPUT_BYTES:
        raise ProtocolError("invalid_tool_result")
    if not isinstance(safe_summary, str) or len(safe_summary) > MAX_TOOL_SUMMARY_CHARS:
        raise ProtocolError("invalid_tool_result")
    error_code = payload.get("error_code")
    if error_code is not None and (
        not isinstance(error_code, str) or not error_code.strip() or len(error_code) > 128
    ):
        raise ProtocolError("invalid_tool_result")


def _validate_tool_context_payload(payload: Mapping[str, Any], code: str) -> None:
    for key in ("session_id", "correlation_id"):
        value = payload.get(key)
        if not isinstance(value, str) or not value.strip() or len(value) > 128:
            raise ProtocolError(code)


def _validate_tool_request_id(payload: Mapping[str, Any], code: str) -> None:
    request_id = payload.get("request_id")
    if not isinstance(request_id, str) or not request_id.strip() or len(request_id) > MAX_REQUEST_ID_CHARS:
        raise ProtocolError(code)


def redacted_summary(value: object) -> str:
    envelope = validate_envelope(value)
    request_hash = hashlib.sha256(envelope["id"].encode("utf-8")).hexdigest()[:12]
    return (
        f"type={envelope['type']} id_hash={request_hash} "
        f"seq={envelope.get('seq', 'none')}"
    )
