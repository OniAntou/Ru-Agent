from __future__ import annotations

import json
import re
import time
from collections.abc import Mapping
from dataclasses import dataclass, field
from typing import Literal, cast

from ru_runtime.worker.v1.action_outcome import (
    ActionOutcomeError,
    ActionOutcomeStatus,
    VolumeReadback,
    parse_volume_readback,
)

_MAX_CAPABILITIES = 16
_MAX_SCOPES = 16
_MAX_TOOL_ID_CHARS = 128
_MAX_TOOL_VERSION_CHARS = 32
_MAX_CAPABILITY_CLASS_CHARS = 64
_MAX_AVAILABILITY_CHARS = 32
_MAX_SCOPE_CHARS = 64
_MAX_HINT_CHARS = 200
_MAX_DURATION_MS = 300_000
_MAX_ERROR_CODE_CHARS = 128
_KNOWN_AVAILABILITY = frozenset(("executable", "native_pending"))
_KNOWN_CAPABILITY_CLASSES = frozenset(("native_mutation", "tool"))
_KNOWN_VERIFICATION_STATES = frozenset(("not_required", "pending", "verified", "failed"))
_CAPABILITY_FIELDS = frozenset(
    (
        "tool_id",
        "tool_version",
        "scopes",
        "availability",
        "model_enabled",
        "capability_class",
        "verification_required",
    )
)
_OPTIONAL_HINT_FIELD = frozenset(("hint",))
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
MAX_TOOL_SUMMARY_CHARS = 4096
MAX_TOOL_ARGUMENTS_BYTES = 64 * 1024
MAX_TOOL_OUTPUT_BYTES = 256 * 1024
TOOL_RESULT_STATUSES = frozenset(
    {"succeeded", "failed", "denied", "approval_required", "cancelled", "timed_out"}
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
    capability_class: Literal["native_mutation", "tool"]
    hint: str = ""


@dataclass(frozen=True, slots=True)
class ActionSelection:
    tool_id: str
    tool_version: str
    capability_class: str
    arguments: dict[str, object]


@dataclass(frozen=True, slots=True)
class ActionReceipt:
    status: ActionOutcomeStatus
    error_code: str | None
    safe_summary: str
    duration_ms: int
    verification_state: str
    readback: VolumeReadback | None = field(default=None, repr=False)


def parse_model_capabilities(settings: Mapping[str, object]) -> tuple[ModelCapability, ...]:
    raw_tools = settings.get("model_tools")
    if not isinstance(raw_tools, list) or len(raw_tools) > _MAX_CAPABILITIES:
        raise ActionProtocolError("invalid_model_tools")

    capabilities: list[ModelCapability] = []
    tool_ids: set[str] = set()
    for raw in raw_tools:
        if not isinstance(raw, Mapping):
            raise ActionProtocolError("invalid_model_tools")
        keys = set(raw)
        if keys != _CAPABILITY_FIELDS and keys != _CAPABILITY_FIELDS | _OPTIONAL_HINT_FIELD:
            raise ActionProtocolError("invalid_model_tools")
        raw_hint = raw.get("hint")
        if raw_hint is not None and not isinstance(raw_hint, str):
            raise ActionProtocolError("invalid_model_tools")
        hint = raw_hint[:_MAX_HINT_CHARS] if raw_hint is not None else ""
        tool_id = _bounded_text(raw.get("tool_id"), _MAX_TOOL_ID_CHARS)
        tool_version = _bounded_text(raw.get("tool_version"), _MAX_TOOL_VERSION_CHARS)
        availability = _bounded_text(raw.get("availability"), _MAX_AVAILABILITY_CHARS)
        capability_class = _bounded_text(
            raw.get("capability_class"),
            _MAX_CAPABILITY_CLASS_CHARS,
        )
        verification_required = raw.get("verification_required")
        raw_scopes = raw.get("scopes")
        if (
            tool_id is None
            or tool_version is None
            or availability not in _KNOWN_AVAILABILITY
            or capability_class not in _KNOWN_CAPABILITY_CLASSES
            or not isinstance(verification_required, bool)
            or verification_required != (capability_class == "native_mutation")
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
                availability=cast(Literal["executable", "native_pending"], availability),
                model_enabled=True,
                capability_class=cast(Literal["native_mutation", "tool"], capability_class),
                hint=hint,
            )
        )
    return tuple(capabilities)


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
        if stripped.startswith(("{", "[")):
            raise ActionProtocolError("invalid_selection") from None
        return None
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

    copied_arguments = dict(arguments)
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
    if (
        payload.get("status") == "succeeded"
        and selection.capability_class == "native_mutation"
        and payload.get("verification_state") != "verified"
    ):
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
    if any(
        expected is not None and payload.get(key) != expected for key, expected in expected_context
    ):
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

    readback = None
    if status == "succeeded" and selection.capability_class == "native_mutation":
        try:
            readback = parse_volume_readback(
                str(selection.arguments["operation"]),
                output,
            )
        except (ActionOutcomeError, KeyError):
            raise ActionProtocolError("invalid_receipt") from None

    return ActionReceipt(
        status=cast(ActionOutcomeStatus, status),
        error_code=error_code,
        safe_summary=safe_summary.strip(),
        duration_ms=duration_ms,
        verification_state=verification_state,
        readback=readback,
    )


def _bounded_text(value: object, maximum: int) -> str | None:
    if not isinstance(value, str) or not value.strip() or len(value) > maximum:
        return None
    return value.strip()


def _json_size(value: object, code: str) -> int:
    try:
        return len(json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8"))
    except (TypeError, ValueError, OverflowError, RecursionError) as error:
        raise ActionProtocolError(code) from error
