from __future__ import annotations

import hashlib
import json
import re
from collections.abc import Mapping
from typing import Any

from ru_runtime.worker.v1.action_outcome import (
    ActionOutcomeError,
    parse_action_outcome,
)

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
TOOL_VERIFICATION_STATES = frozenset(
    {"not_required", "pending", "verified", "failed"}
)
MAX_TOOL_SUMMARY_CHARS = 4096
MAX_TOOL_ARGUMENTS_BYTES = 64 * 1024
MAX_TOOL_OUTPUT_BYTES = 256 * 1024
_TOOL_RESULT_FIELDS = frozenset(
    {
        "request_id",
        "session_id",
        "correlation_id",
        "status",
        "output",
        "error_code",
        "safe_summary",
        "duration_ms",
        "verification_state",
        "endpoint_role",
        "endpoint_hash",
    }
)
_ENDPOINT_HASH = re.compile(r"^[0-9a-f]{16}$")

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
    elif message_type == CHAT_DONE:
        _validate_chat_done_payload(payload)

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


def _validate_chat_done_payload(payload: Mapping[str, Any]) -> None:
    if set(payload) not in ({"raw_reply"}, {"raw_reply", "action_outcome"}):
        raise ProtocolError("invalid_chat_done")
    raw_reply = payload.get("raw_reply")
    if (
        not isinstance(raw_reply, str)
        or not raw_reply.strip()
        or len(raw_reply) > 32_000
    ):
        raise ProtocolError("invalid_chat_done")
    if "action_outcome" in payload:
        try:
            parse_action_outcome(payload["action_outcome"])
        except ActionOutcomeError:
            raise ProtocolError("invalid_action_outcome") from None


def _validate_tool_request_payload(payload: Mapping[str, Any]) -> None:
    _validate_tool_context_payload(payload, "invalid_tool_request")
    _validate_tool_request_id(payload, "invalid_tool_request")
    for key, maximum in (
        ("tool_id", 128),
        ("tool_version", 64),
        ("capability_class", 64),
    ):
        value = payload.get(key)
        if not isinstance(value, str) or not value.strip() or len(value) > maximum:
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
    if set(payload) != _TOOL_RESULT_FIELDS:
        raise ProtocolError("invalid_tool_result")
    _validate_tool_context_payload(payload, "invalid_tool_result")
    _validate_tool_request_id(payload, "invalid_tool_result")
    status = payload.get("status")
    output = payload.get("output")
    safe_summary = payload.get("safe_summary")
    duration_ms = payload.get("duration_ms")
    verification_state = payload.get("verification_state")
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
    if (
        isinstance(duration_ms, bool)
        or not isinstance(duration_ms, int)
        or duration_ms < 0
        or duration_ms > 300_000
        or verification_state not in TOOL_VERIFICATION_STATES
    ):
        raise ProtocolError("invalid_tool_result")
    error_code = payload.get("error_code")
    if error_code is not None and (
        not isinstance(error_code, str) or not error_code.strip() or len(error_code) > 128
    ):
        raise ProtocolError("invalid_tool_result")
    endpoint_role = payload.get("endpoint_role")
    endpoint_hash = payload.get("endpoint_hash")
    if (
        (endpoint_role is None) != (endpoint_hash is None)
        or endpoint_role not in (None, "default_render_multimedia")
        or (
            endpoint_hash is not None
            and (
                not isinstance(endpoint_hash, str)
                or _ENDPOINT_HASH.fullmatch(endpoint_hash) is None
            )
        )
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
