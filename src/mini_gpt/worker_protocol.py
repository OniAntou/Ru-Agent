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
MEMORY_EXTRACT = "memory.extract"
MEMORY_FACTS = "memory.facts"
VOICE_START = "voice.start"
VOICE_CONTROL = "voice.control"
VOICE_DONE = "voice.done"
VTS_CATALOG = "vts.catalog"
VTS_ACTION = "vts.action"
WORKER_HEALTH = "worker.health"
WORKER_HEALTH_OK = "worker.health.ok"
ERROR = "error"

KNOWN_TYPES = frozenset(
    {
        HELLO,
        HELLO_OK,
        CHAT_START,
        CHAT_RESET,
        CHAT_CHUNK,
        CHAT_DONE,
        CHAT_CANCEL,
        MEMORY_EXTRACT,
        MEMORY_FACTS,
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
    return normalized


def encode_envelope(value: object) -> str:
    envelope = validate_envelope(value)
    return json.dumps(envelope, ensure_ascii=False, separators=(",", ":"))


def redacted_summary(value: object) -> str:
    envelope = validate_envelope(value)
    request_hash = hashlib.sha256(envelope["id"].encode("utf-8")).hexdigest()[:12]
    return (
        f"type={envelope['type']} id_hash={request_hash} "
        f"seq={envelope.get('seq', 'none')}"
    )
