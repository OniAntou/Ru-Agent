from __future__ import annotations

import json
import re
import time
from collections.abc import Mapping
from dataclasses import dataclass
from typing import Literal

from ru_runtime.worker.v1.protocol import (
    MAX_TOOL_ARGUMENTS_BYTES,
    MAX_TOOL_OUTPUT_BYTES,
    MAX_TOOL_SUMMARY_CHARS,
    TOOL_RESULT_STATUSES,
)

_MAX_CAPABILITIES = 16
_MAX_SCOPES = 16
_MAX_TOOL_ID_CHARS = 128
_MAX_TOOL_VERSION_CHARS = 32
_MAX_CAPABILITY_CLASS_CHARS = 64
_MAX_AVAILABILITY_CHARS = 32
_MAX_SCOPE_CHARS = 64
_MAX_DURATION_MS = 300_000
_MAX_ERROR_CODE_CHARS = 128
_MAX_PROMPT_CHARS = 16_000
_KNOWN_AVAILABILITY = frozenset(("executable", "native_pending"))
_KNOWN_VERIFICATION_STATES = frozenset(("not_required", "pending", "verified", "failed"))
_CAPABILITY_FIELDS = frozenset(
    (
        "tool_id",
        "tool_version",
        "scopes",
        "availability",
        "model_enabled",
        "capability_class",
    )
)
_SELECTION_FIELDS = frozenset(("tool_id", "tool_version", "arguments"))
_RECEIPT_FIELDS = frozenset(
    (
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
    )
)
_ENDPOINT_HASH = re.compile(r"^[0-9a-f]{16}$")
_ERROR_CODE = re.compile(r"^[a-z0-9][a-z0-9_.-]*$")
_AUDIO_NOUNS = (
    "âm lượng",
    "tiếng máy",
    "loa",
    "volume",
    "sound",
    "mute",
    "unmute",
)
_ACTION_VERBS = (
    "tăng",
    "giảm",
    "hạ",
    "đặt",
    "tắt",
    "bật",
    "increase",
    "decrease",
    "set",
    "turn",
    "mute",
    "unmute",
)
_DIRECT_AUDIO_PHRASES = (
    "tăng tiếng",
    "giảm tiếng",
    "hạ tiếng",
    "đặt tiếng",
    "tắt tiếng",
    "bật tiếng",
)


class ActionProtocolError(ValueError):
    def __init__(self, code: str) -> None:
        self.code = code
        super().__init__(code)


@dataclass(frozen=True, slots=True)
class ModelCapability:
    tool_id: str
    tool_version: str
    scopes: tuple[str, ...]
    availability: Literal["executable", "native_pending"]
    model_enabled: bool
    capability_class: Literal["native_mutation"]


@dataclass(frozen=True, slots=True)
class ActionSelection:
    tool_id: str
    tool_version: str
    capability_class: str
    arguments: dict[str, object]


@dataclass(frozen=True, slots=True)
class ActionReceipt:
    status: str
    error_code: str | None
    safe_summary: str
    duration_ms: int
    verification_state: str


def parse_model_capabilities(settings: Mapping[str, object]) -> tuple[ModelCapability, ...]:
    raw_tools = settings.get("model_tools")
    if not isinstance(raw_tools, list) or len(raw_tools) > _MAX_CAPABILITIES:
        raise ActionProtocolError("invalid_model_tools")

    capabilities: list[ModelCapability] = []
    tool_ids: set[str] = set()
    for raw in raw_tools:
        if not isinstance(raw, Mapping) or set(raw) != _CAPABILITY_FIELDS:
            raise ActionProtocolError("invalid_model_tools")
        tool_id = _bounded_text(raw.get("tool_id"), _MAX_TOOL_ID_CHARS)
        tool_version = _bounded_text(raw.get("tool_version"), _MAX_TOOL_VERSION_CHARS)
        availability = _bounded_text(raw.get("availability"), _MAX_AVAILABILITY_CHARS)
        capability_class = _bounded_text(
            raw.get("capability_class"),
            _MAX_CAPABILITY_CLASS_CHARS,
        )
        raw_scopes = raw.get("scopes")
        if (
            tool_id is None
            or tool_version is None
            or availability not in _KNOWN_AVAILABILITY
            or capability_class != "native_mutation"
            or raw.get("model_enabled") is not True
            or not isinstance(raw_scopes, list)
            or len(raw_scopes) > _MAX_SCOPES
            or tool_id in tool_ids
        ):
            raise ActionProtocolError("invalid_model_tools")
        scopes = tuple(_bounded_text(scope, _MAX_SCOPE_CHARS) or "" for scope in raw_scopes)
        if any(not scope for scope in scopes) or len(scopes) != len(set(scopes)):
            raise ActionProtocolError("invalid_model_tools")

        tool_ids.add(tool_id)
        capabilities.append(
            ModelCapability(
                tool_id=tool_id,
                tool_version=tool_version,
                scopes=scopes,
                availability=availability,
                model_enabled=True,
                capability_class="native_mutation",
            )
        )
    return tuple(capabilities)


def detect_volume_intent(prompt: str) -> bool:
    if not isinstance(prompt, str) or not prompt.strip() or len(prompt) > _MAX_PROMPT_CHARS:
        return False
    normalized = " ".join(prompt.casefold().split())
    return any(_contains_term(normalized, phrase) for phrase in _DIRECT_AUDIO_PHRASES) or (
        any(_contains_term(normalized, term) for term in _AUDIO_NOUNS)
        and any(_contains_term(normalized, term) for term in _ACTION_VERBS)
    )


def validate_selection(
    text: str,
    capabilities: tuple[ModelCapability, ...],
) -> ActionSelection | None:
    if not isinstance(text, str):
        raise ActionProtocolError("invalid_selection")
    stripped = text.strip()
    if not stripped:
        return None
    if len(stripped.encode("utf-8")) > MAX_TOOL_ARGUMENTS_BYTES + 4096:
        raise ActionProtocolError("invalid_selection")
    try:
        parsed = json.loads(stripped)
    except (json.JSONDecodeError, RecursionError):
        if not stripped.startswith(("{", "[")):
            return None
        raise ActionProtocolError("invalid_selection") from None
    if not isinstance(parsed, Mapping):
        raise ActionProtocolError("invalid_selection")
    if set(parsed) != _SELECTION_FIELDS:
        raise ActionProtocolError("invalid_selection")

    tool_id = _bounded_text(parsed.get("tool_id"), _MAX_TOOL_ID_CHARS)
    tool_version = _bounded_text(parsed.get("tool_version"), _MAX_TOOL_VERSION_CHARS)
    arguments = parsed.get("arguments")
    if tool_id is None or tool_version is None or not isinstance(arguments, Mapping):
        raise ActionProtocolError("invalid_selection")
    capability = next(
        (
            item
            for item in capabilities
            if item.tool_id == tool_id and item.tool_version == tool_version
        ),
        None,
    )
    if capability is None:
        raise ActionProtocolError("capability_unavailable")
    if tool_id != "system.volume" or tool_version != "1.0.0":
        raise ActionProtocolError("capability_unavailable")

    copied_arguments = dict(arguments)
    _validate_volume_arguments(copied_arguments)
    if _json_size(copied_arguments, "invalid_selection") > MAX_TOOL_ARGUMENTS_BYTES:
        raise ActionProtocolError("invalid_selection")
    return ActionSelection(
        tool_id=tool_id,
        tool_version=tool_version,
        capability_class=capability.capability_class,
        arguments=copied_arguments,
    )


def validate_receipt(
    selection: ActionSelection,
    payload: Mapping[str, object],
    *,
    expected_request_id: str | None = None,
    expected_session_id: str | None = None,
    expected_correlation_id: str | None = None,
    deadline_at: float | None = None,
    now: float | None = None,
) -> ActionReceipt:
    if not isinstance(payload, Mapping):
        raise ActionProtocolError("invalid_receipt")
    if payload.get("status") == "succeeded" and payload.get("verification_state") != "verified":
        raise ActionProtocolError("receipt_unverified")
    if set(payload) != _RECEIPT_FIELDS:
        raise ActionProtocolError("invalid_receipt")
    if deadline_at is not None and (time.monotonic() if now is None else now) > deadline_at:
        raise ActionProtocolError("receipt_late")
    expected_context = (
        ("request_id", expected_request_id),
        ("session_id", expected_session_id),
        ("correlation_id", expected_correlation_id),
    )
    if any(expected is not None and payload.get(key) != expected for key, expected in expected_context):
        raise ActionProtocolError("receipt_mismatch")

    status = payload.get("status")
    verification_state = payload.get("verification_state")
    if status not in TOOL_RESULT_STATUSES or verification_state not in _KNOWN_VERIFICATION_STATES:
        raise ActionProtocolError("invalid_receipt")

    output = payload.get("output")
    safe_summary = payload.get("safe_summary")
    duration_ms = payload.get("duration_ms")
    error_code = payload.get("error_code")
    if (
        not isinstance(output, Mapping)
        or _json_size(output, "invalid_receipt") > MAX_TOOL_OUTPUT_BYTES
        or not isinstance(safe_summary, str)
        or not safe_summary.strip()
        or len(safe_summary) > MAX_TOOL_SUMMARY_CHARS
        or isinstance(duration_ms, bool)
        or not isinstance(duration_ms, int)
        or duration_ms < 0
        or duration_ms > _MAX_DURATION_MS
        or (
            error_code is not None
            and (
                not isinstance(error_code, str)
                or len(error_code) > _MAX_ERROR_CODE_CHARS
                or _ERROR_CODE.fullmatch(error_code) is None
            )
        )
    ):
        raise ActionProtocolError("invalid_receipt")

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
        or (
            status == "succeeded"
            and selection.capability_class == "native_mutation"
            and endpoint_role is None
        )
    ):
        raise ActionProtocolError("invalid_receipt")

    return ActionReceipt(
        status=status,
        error_code=error_code,
        safe_summary=safe_summary.strip(),
        duration_ms=duration_ms,
        verification_state=verification_state,
    )


def failure_reply(_error_code: str) -> str:
    return "Không thể thực hiện thay đổi âm lượng."


def _validate_volume_arguments(arguments: dict[str, object]) -> None:
    operation = arguments.get("operation")
    if operation in ("get", "mute", "unmute"):
        valid = set(arguments) == {"operation"}
    elif operation == "set":
        level = arguments.get("level")
        valid = (
            set(arguments) == {"operation", "level"}
            and isinstance(level, int)
            and not isinstance(level, bool)
            and 0 <= level <= 100
        )
    elif operation in ("increase", "decrease"):
        amount = arguments.get("amount")
        valid = (
            set(arguments) == {"operation", "amount"}
            and isinstance(amount, int)
            and not isinstance(amount, bool)
            and 1 <= amount <= 50
        )
    else:
        valid = False
    if not valid:
        raise ActionProtocolError("invalid_selection")


def _bounded_text(value: object, maximum: int) -> str | None:
    if not isinstance(value, str) or not value.strip() or len(value) > maximum:
        return None
    return value.strip()


def _contains_term(text: str, term: str) -> bool:
    return re.search(rf"(?<!\w){re.escape(term)}(?!\w)", text) is not None


def _json_size(value: object, code: str) -> int:
    try:
        return len(
            json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        )
    except (TypeError, ValueError, OverflowError, RecursionError) as error:
        raise ActionProtocolError(code) from error
