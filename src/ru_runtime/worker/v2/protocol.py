from __future__ import annotations

import json
import math
import re
import unicodedata
from collections.abc import Mapping
from dataclasses import dataclass
from hashlib import sha256
from typing import Any, Literal

PROTOCOL_VERSION = 2
MAX_REQUEST_ID_CHARS = 128
MAX_CORRELATION_ID_CHARS = 128
MAX_PROMPT_CHARS = 16_000
MAX_TEXT_CHARS = 1_000_000
MAX_DELTA_CHARS = 64 * 1024
MAX_TOOL_ID_CHARS = 128
MAX_TOOL_VERSION_CHARS = 32
MAX_PROVIDER_CALL_ID_CHARS = 256
MAX_TOOL_ARGUMENTS_BYTES = 64 * 1024
MAX_TOOL_SCHEMA_BYTES = 32 * 1024
MAX_SETTINGS_BYTES = 128 * 1024
MAX_FRAME_BYTES = 5 * 1024 * 1024
PROMPT_CONTEXT_SCHEMA_VERSION = 1
MAX_PROMPT_CONTEXT_BYTES = 512 * 1024
MAX_PROMPT_INSTRUCTION_BYTES = 64 * 1024
MAX_COMBINED_PROMPT_INSTRUCTION_BYTES = 256 * 1024
MAX_PROMPT_MEMORY_BYTES = 100 * 1024
MAX_PROMPT_REFERENCE_BYTES = 32 * 1024
MAX_PROMPT_COLLECTION_ITEMS = 16
MAX_PROMPT_RECEIPT_BLOCKS = 32
MAX_SEQUENCE = (1 << 63) - 1

HELLO = "hello"
HELLO_OK = "hello.ok"
AGENT_STEP_START = "agent.step.start"
AGENT_STEP_DELTA = "agent.step.delta"
AGENT_STEP_DONE = "agent.step.done"
AGENT_STEP_CANCEL = "agent.step.cancel"
ERROR = "error"

_MESSAGE_TYPES = frozenset(
    {HELLO, HELLO_OK, AGENT_STEP_START, AGENT_STEP_DELTA, AGENT_STEP_DONE, AGENT_STEP_CANCEL, ERROR}
)
_ORDERED_RESPONSE_TYPES = frozenset({HELLO_OK, AGENT_STEP_DELTA, AGENT_STEP_DONE, ERROR})
_FINISH_REASONS = frozenset(("stop", "tool_calls", "length"))
_SEMVER = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")
_SAFE_IDENTIFIER = re.compile(r"^[A-Za-z0-9_.-]+$")
_PROMPT_SOURCE_IDENTIFIER = re.compile(r"^[A-Za-z0-9_.:-]+$")
_ERROR_CODE = re.compile(r"^[a-z][a-z0-9_.-]{0,63}$")
_ENVELOPE_FIELDS = frozenset({"v", "id", "type", "seq", "correlation_id", "payload"})
_ENVELOPE_REQUIRED_FIELDS = frozenset({"v", "id", "type", "payload"})
_START_FIELDS = frozenset({"prompt", "turns", "system_prompt_context", "settings"})
_TURN_FIELDS = frozenset({"prompt", "response"})
_SETTING_FIELDS = frozenset(
    {"max_tokens", "temperature", "top_k", "allow_web_search", "model_tools"}
)
_MODEL_TOOL_FIELDS = frozenset(
    {
        "tool_id",
        "tool_version",
        "scopes",
        "availability",
        "model_enabled",
        "capability_class",
        "verification_required",
    }
)
_MODEL_TOOL_OPTIONAL_FIELDS = frozenset({"hint", "input_schema"})
_PROMPT_CONTEXT_FIELDS = frozenset(
    {
        "schema_version",
        "agent_profile",
        "preset",
        "environment",
        "instruction_blocks",
        "capabilities",
        "runtime_context",
    }
)
_PROMPT_ENVIRONMENT_FIELDS = frozenset(
    {"model", "platform", "locale", "timezone", "date", "workspace_label"}
)
_PROMPT_INSTRUCTION_FIELDS = frozenset(
    {"source_id", "display_name", "content", "content_sha256"}
)
_PROMPT_CAPABILITY_FIELDS = frozenset(
    {"id", "version", "summary", "input_schema_digest", "policy_summary", "availability"}
)
_PROMPT_RUNTIME_FIELDS = frozenset(
    {"memory", "mood", "voice", "vts", "hardware", "references"}
)
_PROMPT_REFERENCE_FIELDS = frozenset({"kind", "label", "content"})
_PROMPT_RECEIPT_FIELDS = frozenset({"composer_version", "prompt_sha256", "block_digests"})
_PROMPT_BLOCK_DIGEST_FIELDS = frozenset({"block_id", "sha256"})


class ProtocolError(ValueError):
    """Stable protocol validation failure; ``code`` is safe to expose."""

    def __init__(self, code: str) -> None:
        self.code = code
        super().__init__(code)


@dataclass(frozen=True, slots=True)
class ModelToolCall:
    provider_call_id: str | None
    tool_id: str
    tool_version: str
    arguments: Mapping[str, Any]

    def __post_init__(self) -> None:
        if self.provider_call_id is not None and (
            not self.provider_call_id.strip()
            or len(self.provider_call_id) > MAX_PROVIDER_CALL_ID_CHARS
        ):
            raise ValueError("provider call id is out of bounds")
        if not self.tool_id.strip() or len(self.tool_id) > MAX_TOOL_ID_CHARS:
            raise ValueError("tool id is out of bounds")
        if (
            len(self.tool_version) > MAX_TOOL_VERSION_CHARS
            or _SEMVER.fullmatch(self.tool_version) is None
        ):
            raise ValueError("tool version is invalid")
        if not isinstance(self.arguments, Mapping):
            raise ValueError("tool arguments must be an object")
        try:
            argument_bytes = len(
                json.dumps(
                    dict(self.arguments),
                    ensure_ascii=False,
                    allow_nan=False,
                    separators=(",", ":"),
                ).encode()
            )
        except (TypeError, ValueError, OverflowError, RecursionError) as error:
            raise ValueError("tool arguments are invalid") from error
        if argument_bytes > MAX_TOOL_ARGUMENTS_BYTES:
            raise ValueError("tool arguments are out of bounds")
        object.__setattr__(self, "arguments", dict(self.arguments))


@dataclass(frozen=True, slots=True)
class AssistantStep:
    text: str | None
    tool_calls: tuple[ModelToolCall, ...]
    finish_reason: Literal["stop", "tool_calls", "length"]

    def __post_init__(self) -> None:
        if self.text is not None and len(self.text) > MAX_TEXT_CHARS:
            raise ValueError("assistant text is out of bounds")
        if self.finish_reason not in _FINISH_REASONS:
            raise ValueError("finish reason is invalid")
        if len(self.tool_calls) > 1:
            raise ValueError("one-step Worker v2 accepts at most one tool call")
        if self.finish_reason == "tool_calls" and not self.tool_calls:
            raise ValueError("tool_calls finish reason requires a call")
        if self.tool_calls and self.finish_reason != "tool_calls":
            raise ValueError("tool call step requires the tool_calls finish reason")
        if self.tool_calls and self.text is not None:
            raise ValueError("tool call step cannot contain visible text")


def encode_envelope(envelope: Mapping[str, Any]) -> str:
    value = validate_envelope(envelope)
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def decode_envelope(raw: str) -> dict[str, Any]:
    if not isinstance(raw, str):
        raise ProtocolError("invalid_json")
    try:
        value = json.loads(
            raw,
            object_pairs_hook=_unique_json_object,
            parse_constant=_reject_json_constant,
        )
    except (json.JSONDecodeError, RecursionError):
        raise ProtocolError("invalid_json") from None
    return validate_envelope(value)


def validate_envelope(value: object) -> dict[str, Any]:
    if not isinstance(value, Mapping):
        raise ProtocolError("invalid_envelope")
    fields = set(value)
    if not _ENVELOPE_REQUIRED_FIELDS.issubset(fields) or not fields.issubset(_ENVELOPE_FIELDS):
        raise ProtocolError("invalid_envelope")
    version = value.get("v")
    if isinstance(version, bool) or not isinstance(version, int):
        raise ProtocolError("invalid_version")
    if version != PROTOCOL_VERSION:
        raise ProtocolError("unsupported_version")
    request_id = value.get("id")
    if not _is_safe_identifier(request_id, MAX_REQUEST_ID_CHARS):
        raise ProtocolError("invalid_id")
    message_type = value.get("type")
    if message_type not in _MESSAGE_TYPES:
        raise ProtocolError("unsupported_message")
    sequence = value.get("seq", 0)
    if message_type in _ORDERED_RESPONSE_TYPES and "seq" not in value:
        raise ProtocolError("missing_sequence")
    if message_type not in _ORDERED_RESPONSE_TYPES and "seq" in value:
        raise ProtocolError("invalid_sequence")
    if (
        isinstance(sequence, bool)
        or not isinstance(sequence, int)
        or sequence < 0
        or sequence > MAX_SEQUENCE
    ):
        raise ProtocolError("invalid_sequence")
    if message_type == HELLO_OK and sequence != 0:
        raise ProtocolError("invalid_sequence")
    if message_type in _ORDERED_RESPONSE_TYPES - {HELLO_OK} and sequence < 1:
        raise ProtocolError("invalid_sequence")
    correlation_id = value.get("correlation_id")
    if correlation_id is not None and not _is_safe_identifier(
        correlation_id, MAX_CORRELATION_ID_CHARS
    ):
        raise ProtocolError("invalid_correlation_id")
    payload = value.get("payload")
    if not isinstance(payload, Mapping):
        raise ProtocolError("invalid_payload")

    if message_type == HELLO:
        _validate_hello(payload)
    elif message_type == HELLO_OK:
        _validate_hello_ok(payload)
    elif message_type == AGENT_STEP_START:
        _validate_start(payload)
    elif message_type == AGENT_STEP_DELTA:
        _validate_delta(payload)
    elif message_type == AGENT_STEP_DONE:
        _validate_step_payload(payload)
    elif message_type == AGENT_STEP_CANCEL:
        if payload:
            raise ProtocolError("invalid_cancel")
    elif message_type == ERROR:
        _validate_error(payload)

    result: dict[str, Any] = {
        "v": version,
        "id": request_id,
        "type": message_type,
    }
    if "seq" in value:
        result["seq"] = sequence
    if correlation_id is not None:
        result["correlation_id"] = correlation_id
    result["payload"] = dict(payload)
    _bounded_json(result, MAX_FRAME_BYTES, "message_too_large")
    return result


def step_to_payload(step: AssistantStep) -> dict[str, Any]:
    return {
        "text": step.text,
        "tool_calls": [
            {
                "provider_call_id": call.provider_call_id,
                "tool_id": call.tool_id,
                "tool_version": call.tool_version,
                "arguments": dict(call.arguments),
            }
            for call in step.tool_calls
        ],
        "finish_reason": step.finish_reason,
    }


def step_from_payload(payload: Mapping[str, Any]) -> AssistantStep:
    _validate_step_payload(payload)
    calls = tuple(
        ModelToolCall(
            item.get("provider_call_id"),
            item["tool_id"],
            item["tool_version"],
            item["arguments"],
        )
        for item in payload["tool_calls"]
    )
    return AssistantStep(payload["text"], calls, payload["finish_reason"])


def validate_start_payload(payload: Mapping[str, Any]) -> None:
    """Validate a model-step request at direct adapter and wire boundaries."""
    _validate_start(payload)


def _validate_hello(payload: Mapping[str, Any]) -> None:
    if set(payload) != {"token"}:
        raise ProtocolError("invalid_hello")
    token = payload.get("token")
    if (
        not isinstance(token, str)
        or not token.strip()
        or token != token.strip()
        or not token.isascii()
        or len(token) > 4096
    ):
        raise ProtocolError("invalid_hello")


def _validate_hello_ok(payload: Mapping[str, Any]) -> None:
    if set(payload) != {"version", "capabilities"}:
        raise ProtocolError("invalid_hello")
    if payload.get("version") != PROTOCOL_VERSION:
        raise ProtocolError("invalid_hello")
    capabilities = payload.get("capabilities")
    if (
        not isinstance(capabilities, list)
        or any(not _is_safe_identifier(item, 128) for item in capabilities)
        or len(capabilities) != len(set(capabilities))
    ):
        raise ProtocolError("invalid_hello")


def _validate_start(payload: Mapping[str, Any]) -> None:
    if "prompt" not in payload or not set(payload).issubset(_START_FIELDS):
        raise ProtocolError("invalid_request")
    prompt = payload.get("prompt")
    if not isinstance(prompt, str) or not prompt.strip() or len(prompt) > MAX_PROMPT_CHARS:
        raise ProtocolError("invalid_request")
    turns = payload.get("turns", [])
    if not isinstance(turns, list) or len(turns) > 8:
        raise ProtocolError("invalid_request")
    for turn in turns:
        if not isinstance(turn, Mapping) or set(turn) != _TURN_FIELDS:
            raise ProtocolError("invalid_request")
        turn_prompt = turn.get("prompt")
        turn_response = turn.get("response")
        if (
            not isinstance(turn_prompt, str)
            or not turn_prompt.strip()
            or len(turn_prompt) > MAX_PROMPT_CHARS
            or not isinstance(turn_response, str)
            or not turn_response.strip()
            or len(turn_response) > 32_000
        ):
            raise ProtocolError("invalid_request")
    prompt_context = payload.get("system_prompt_context")
    if not isinstance(prompt_context, Mapping):
        raise ProtocolError("invalid_request")
    web_search_enabled = _validate_prompt_context(prompt_context)
    settings = payload.get("settings", {})
    if not isinstance(settings, Mapping) or not set(settings).issubset(_SETTING_FIELDS):
        raise ProtocolError("invalid_request")
    _validate_optional_integer(settings, "max_tokens", 1, 1024)
    _validate_optional_number(settings, "temperature", 0.1, 2.0)
    _validate_optional_integer(settings, "top_k", 1, 200)
    allow_web_search = settings.get("allow_web_search", False)
    if not isinstance(allow_web_search, bool):
        raise ProtocolError("invalid_request")
    if allow_web_search and not web_search_enabled:
        raise ProtocolError("invalid_request")
    model_tools = settings.get("model_tools", [])
    if not isinstance(model_tools, list) or len(model_tools) > 16:
        raise ProtocolError("invalid_request")
    tool_ids: set[str] = set()
    for tool in model_tools:
        _validate_model_tool(tool, tool_ids)
    _bounded_json(settings, MAX_SETTINGS_BYTES, "invalid_request")


def _validate_prompt_context(context: Mapping[str, Any]) -> bool:
    if set(context) != _PROMPT_CONTEXT_FIELDS:
        raise ProtocolError("invalid_request")

    if context.get("schema_version") != PROMPT_CONTEXT_SCHEMA_VERSION:
        raise ProtocolError("invalid_request")
    if context.get("agent_profile") != "ru-agent":
        raise ProtocolError("invalid_request")
    if context.get("preset") not in {"standard", "full_access"}:
        raise ProtocolError("invalid_request")

    environment = context.get("environment")
    if not isinstance(environment, Mapping) or set(environment) != _PROMPT_ENVIRONMENT_FIELDS:
        raise ProtocolError("invalid_request")
    for field, maximum in (
        ("model", 256),
        ("platform", 64),
        ("locale", 64),
        ("timezone", 128),
        ("date", 32),
    ):
        _require_prompt_string(environment.get(field), maximum)
    _require_optional_prompt_string(environment.get("workspace_label"), 128)

    instructions = context.get("instruction_blocks")
    if not isinstance(instructions, list) or len(instructions) > MAX_PROMPT_COLLECTION_ITEMS:
        raise ProtocolError("invalid_request")
    instruction_ids: set[str] = set()
    combined_bytes = 0
    for instruction in instructions:
        if not isinstance(instruction, Mapping) or set(instruction) != _PROMPT_INSTRUCTION_FIELDS:
            raise ProtocolError("invalid_request")
        source_id = _require_prompt_identifier(instruction.get("source_id"), 128, allow_colon=True)
        if source_id in instruction_ids:
            raise ProtocolError("invalid_request")
        instruction_ids.add(source_id)
        _require_prompt_string(instruction.get("display_name"), 256)
        content = _require_prompt_text(instruction.get("content"), MAX_PROMPT_INSTRUCTION_BYTES)
        digest = _require_prompt_digest(instruction.get("content_sha256"))
        content_bytes = len(content.encode("utf-8"))
        combined_bytes += content_bytes
        if combined_bytes > MAX_COMBINED_PROMPT_INSTRUCTION_BYTES:
            raise ProtocolError("invalid_request")
        if digest != sha256(content.encode("utf-8")).hexdigest():
            raise ProtocolError("invalid_request")

    capabilities = context.get("capabilities")
    if not isinstance(capabilities, list) or len(capabilities) > MAX_PROMPT_COLLECTION_ITEMS:
        raise ProtocolError("invalid_request")
    capability_ids: set[str] = set()
    web_search_enabled = False
    for capability in capabilities:
        if not isinstance(capability, Mapping) or set(capability) != _PROMPT_CAPABILITY_FIELDS:
            raise ProtocolError("invalid_request")
        capability_id = _require_prompt_identifier(capability.get("id"), 128)
        if capability_id in capability_ids:
            raise ProtocolError("invalid_request")
        capability_ids.add(capability_id)
        version = _require_prompt_string(capability.get("version"), MAX_TOOL_VERSION_CHARS)
        if _SEMVER.fullmatch(version) is None:
            raise ProtocolError("invalid_request")
        _require_prompt_string(capability.get("summary"), 2 * 1024)
        _require_prompt_digest(capability.get("input_schema_digest"))
        _require_prompt_string(capability.get("policy_summary"), 2 * 1024)
        availability = _require_prompt_identifier(capability.get("availability"), 32)
        if capability_id == "web.search" and availability == "enabled":
            web_search_enabled = True

    runtime = context.get("runtime_context")
    if not isinstance(runtime, Mapping) or set(runtime) != _PROMPT_RUNTIME_FIELDS:
        raise ProtocolError("invalid_request")
    memory = _require_optional_prompt_string(runtime.get("memory"), MAX_PROMPT_MEMORY_BYTES)
    if memory is not None and len(memory.encode("utf-8")) > MAX_PROMPT_MEMORY_BYTES:
        raise ProtocolError("invalid_request")
    _require_optional_prompt_string(runtime.get("mood"), 256)
    _require_optional_prompt_string(runtime.get("voice"), 2 * 1024)
    _require_optional_prompt_string(runtime.get("vts"), 2 * 1024)
    _require_optional_prompt_string(runtime.get("hardware"), 2 * 1024)

    references = runtime.get("references")
    if not isinstance(references, list) or len(references) > MAX_PROMPT_COLLECTION_ITEMS:
        raise ProtocolError("invalid_request")
    for reference in references:
        if not isinstance(reference, Mapping) or set(reference) != _PROMPT_REFERENCE_FIELDS:
            raise ProtocolError("invalid_request")
        _require_prompt_identifier(reference.get("kind"), 64)
        _require_prompt_string(reference.get("label"), 256)
        _require_prompt_text(reference.get("content"), MAX_PROMPT_REFERENCE_BYTES)

    _bounded_json(context, MAX_PROMPT_CONTEXT_BYTES, "invalid_request")
    return web_search_enabled


def _validate_prompt_receipt(receipt: Mapping[str, Any]) -> None:
    if set(receipt) != _PROMPT_RECEIPT_FIELDS:
        raise ProtocolError("invalid_step")
    if receipt.get("composer_version") != "1.0.0":
        raise ProtocolError("invalid_step")
    _require_prompt_digest(receipt.get("prompt_sha256"), code="invalid_step")
    blocks = receipt.get("block_digests")
    if not isinstance(blocks, list) or len(blocks) > MAX_PROMPT_RECEIPT_BLOCKS:
        raise ProtocolError("invalid_step")
    block_ids: set[str] = set()
    for block in blocks:
        if not isinstance(block, Mapping) or set(block) != _PROMPT_BLOCK_DIGEST_FIELDS:
            raise ProtocolError("invalid_step")
        block_id = _require_prompt_identifier(block.get("block_id"), 256, allow_colon=True, code="invalid_step")
        if block_id in block_ids:
            raise ProtocolError("invalid_step")
        block_ids.add(block_id)
        _require_prompt_digest(block.get("sha256"), code="invalid_step")


def _normalize_prompt_text(value: str) -> str:
    return unicodedata.normalize("NFC", value).replace("\r\n", "\n").replace("\r", "\n")


def _require_prompt_string(value: object, maximum: int, *, code: str = "invalid_request") -> str:
    if not isinstance(value, str) or not value.strip() or "\x00" in value or len(value) > maximum:
        raise ProtocolError(code)
    return value


def _require_optional_prompt_string(value: object, maximum: int, *, code: str = "invalid_request") -> str | None:
    if value is None:
        return None
    if not isinstance(value, str) or "\x00" in value or len(value) > maximum:
        raise ProtocolError(code)
    return value


def _require_prompt_text(value: object, maximum_bytes: int, *, code: str = "invalid_request") -> str:
    if not isinstance(value, str):
        raise ProtocolError(code)
    normalized = _normalize_prompt_text(value)
    if not normalized.strip() or "\x00" in normalized:
        raise ProtocolError(code)
    try:
        if len(normalized.encode("utf-8")) > maximum_bytes:
            raise ProtocolError(code)
    except UnicodeEncodeError:
        raise ProtocolError(code) from None
    return normalized


def _require_prompt_identifier(
    value: object,
    maximum: int,
    *,
    allow_colon: bool = False,
    code: str = "invalid_request",
) -> str:
    text = _require_prompt_string(value, maximum, code=code)
    pattern = _PROMPT_SOURCE_IDENTIFIER if allow_colon else _SAFE_IDENTIFIER
    if pattern.fullmatch(text) is None:
        raise ProtocolError(code)
    return text


def _require_prompt_digest(value: object, *, code: str = "invalid_request") -> str:
    if not isinstance(value, str) or len(value) != 64 or any(
        character not in "0123456789abcdef" for character in value
    ):
        raise ProtocolError(code)
    return value


def _validate_delta(payload: Mapping[str, Any]) -> None:
    if set(payload) != {"text"}:
        raise ProtocolError("invalid_delta")
    text = payload.get("text")
    if not isinstance(text, str) or not text or len(text) > MAX_DELTA_CHARS:
        raise ProtocolError("invalid_delta")


def _validate_step_payload(payload: Mapping[str, Any]) -> None:
    if set(payload) != {"text", "tool_calls", "finish_reason", "prompt_receipt"}:
        raise ProtocolError("invalid_step")
    receipt = payload.get("prompt_receipt")
    if not isinstance(receipt, Mapping):
        raise ProtocolError("invalid_step")
    _validate_prompt_receipt(receipt)
    text = payload.get("text")
    if text is not None and (not isinstance(text, str) or len(text) > MAX_TEXT_CHARS):
        raise ProtocolError("invalid_step")
    calls = payload.get("tool_calls")
    if not isinstance(calls, list) or len(calls) > 1:
        raise ProtocolError("invalid_step")
    for call in calls:
        if not isinstance(call, Mapping) or set(call) != {
            "provider_call_id",
            "tool_id",
            "tool_version",
            "arguments",
        }:
            raise ProtocolError("invalid_step")
        provider_id = call.get("provider_call_id")
        if provider_id is not None and (
            not isinstance(provider_id, str)
            or not provider_id.strip()
            or len(provider_id) > MAX_PROVIDER_CALL_ID_CHARS
        ):
            raise ProtocolError("invalid_step")
        tool_id = call.get("tool_id")
        version = call.get("tool_version")
        if not isinstance(tool_id, str) or not tool_id.strip() or len(tool_id) > MAX_TOOL_ID_CHARS:
            raise ProtocolError("invalid_step")
        if (
            not isinstance(version, str)
            or len(version) > MAX_TOOL_VERSION_CHARS
            or _SEMVER.fullmatch(version) is None
        ):
            raise ProtocolError("invalid_step")
        arguments = call.get("arguments")
        if not isinstance(arguments, Mapping):
            raise ProtocolError("invalid_step")
        _bounded_json(arguments, MAX_TOOL_ARGUMENTS_BYTES, "invalid_step")
    finish_reason = payload.get("finish_reason")
    if finish_reason not in _FINISH_REASONS:
        raise ProtocolError("invalid_step")
    if finish_reason == "tool_calls" and len(calls) != 1:
        raise ProtocolError("invalid_step")
    if calls and finish_reason != "tool_calls":
        raise ProtocolError("invalid_step")
    if calls and text is not None:
        raise ProtocolError("invalid_step")


def _validate_error(payload: Mapping[str, Any]) -> None:
    if set(payload) != {"code", "retryable"}:
        raise ProtocolError("invalid_error")
    code = payload.get("code")
    retryable = payload.get("retryable")
    if not isinstance(code, str) or _ERROR_CODE.fullmatch(code) is None:
        raise ProtocolError("invalid_error")
    if not isinstance(retryable, bool):
        raise ProtocolError("invalid_error")


def _bounded_json(value: object, maximum: int, code: str) -> None:
    try:
        size = len(
            json.dumps(
                value,
                ensure_ascii=False,
                allow_nan=False,
                separators=(",", ":"),
            ).encode()
        )
    except (TypeError, ValueError, OverflowError, RecursionError) as error:
        raise ProtocolError(code) from error
    if size > maximum:
        raise ProtocolError(code)


def _unique_json_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise ProtocolError("duplicate_field")
        value[key] = item
    return value


def _reject_json_constant(_value: str) -> None:
    raise ProtocolError("invalid_json")


def _is_safe_identifier(value: object, maximum: int) -> bool:
    return (
        isinstance(value, str)
        and 0 < len(value) <= maximum
        and _SAFE_IDENTIFIER.fullmatch(value) is not None
    )


def is_safe_request_id(value: object) -> bool:
    return _is_safe_identifier(value, MAX_REQUEST_ID_CHARS)


def is_safe_error_code(value: object) -> bool:
    return isinstance(value, str) and _ERROR_CODE.fullmatch(value) is not None


def _validate_optional_integer(
    settings: Mapping[str, Any],
    field: str,
    minimum: int,
    maximum: int,
) -> None:
    if field not in settings:
        return
    value = settings[field]
    if isinstance(value, bool) or not isinstance(value, int) or not minimum <= value <= maximum:
        raise ProtocolError("invalid_request")


def _validate_optional_number(
    settings: Mapping[str, Any],
    field: str,
    minimum: float,
    maximum: float,
) -> None:
    if field not in settings:
        return
    value = settings[field]
    if (
        isinstance(value, bool)
        or not isinstance(value, (int, float))
        or not math.isfinite(value)
        or not minimum <= value <= maximum
    ):
        raise ProtocolError("invalid_request")


def _validate_model_tool(value: object, tool_ids: set[str]) -> None:
    if not isinstance(value, Mapping):
        raise ProtocolError("invalid_request")
    fields = set(value)
    if not _MODEL_TOOL_FIELDS.issubset(fields) or not fields.issubset(
        _MODEL_TOOL_FIELDS | _MODEL_TOOL_OPTIONAL_FIELDS
    ):
        raise ProtocolError("invalid_request")
    tool_id = value.get("tool_id")
    tool_version = value.get("tool_version")
    if (
        not isinstance(tool_id, str)
        or not tool_id.strip()
        or len(tool_id) > MAX_TOOL_ID_CHARS
        or tool_id in tool_ids
        or not isinstance(tool_version, str)
        or len(tool_version) > MAX_TOOL_VERSION_CHARS
        or _SEMVER.fullmatch(tool_version) is None
    ):
        raise ProtocolError("invalid_request")
    scopes = value.get("scopes")
    if (
        not isinstance(scopes, list)
        or len(scopes) > 16
        or any(not _is_safe_identifier(scope, 128) for scope in scopes)
        or len(scopes) != len(set(scopes))
        or value.get("availability") not in {"executable", "native_pending"}
        or value.get("model_enabled") is not True
        or value.get("capability_class") not in {"tool", "native_mutation"}
        or not isinstance(value.get("verification_required"), bool)
        or value.get("verification_required")
        != (value.get("capability_class") == "native_mutation")
    ):
        raise ProtocolError("invalid_request")
    hint = value.get("hint")
    if hint is not None and (not isinstance(hint, str) or len(hint) > 1024):
        raise ProtocolError("invalid_request")
    schema = value.get("input_schema")
    if schema is not None:
        if not isinstance(schema, Mapping):
            raise ProtocolError("invalid_request")
        _bounded_json(schema, MAX_TOOL_SCHEMA_BYTES, "invalid_request")
    tool_ids.add(tool_id)
