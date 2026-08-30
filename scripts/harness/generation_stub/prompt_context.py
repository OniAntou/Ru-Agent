from __future__ import annotations

import json
import re
import unicodedata
from collections.abc import Mapping
from dataclasses import dataclass
from hashlib import sha256
from typing import Any

PROMPT_CONTEXT_SCHEMA_VERSION = 1
MAX_PROMPT_CONTEXT_BYTES = 512 * 1024
MAX_PROMPT_INSTRUCTION_BYTES = 64 * 1024
MAX_COMBINED_PROMPT_INSTRUCTION_BYTES = 256 * 1024
MAX_PROMPT_MEMORY_BYTES = 100 * 1024
MAX_PROMPT_REFERENCE_BYTES = 32 * 1024
MAX_PROMPT_COLLECTION_ITEMS = 16

_SAFE_IDENTIFIER = re.compile(r"^[A-Za-z0-9_.-]+$")
_PROMPT_SOURCE_IDENTIFIER = re.compile(r"^[A-Za-z0-9_.:-]+$")
_SEMVER = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")


class PromptContextError(ValueError):
    """A bounded, privacy-safe prompt-context validation error."""

    def __init__(self, code: str = "prompt_contract_invalid") -> None:
        self.code = code
        super().__init__(code)


def sha256_utf8(value: str) -> str:
    if not isinstance(value, str):
        raise PromptContextError()
    try:
        encoded = value.encode("utf-8")
    except UnicodeEncodeError:
        raise PromptContextError() from None
    return sha256(encoded).hexdigest()


def normalize_text(value: str, *, maximum_bytes: int) -> str:
    if not isinstance(value, str) or maximum_bytes < 0 or "\x00" in value:
        raise PromptContextError()
    try:
        normalized = unicodedata.normalize("NFC", value).replace("\r\n", "\n").replace("\r", "\n")
        # Trailing whitespace is a canonical separator artifact, not instruction
        # content. Do not trim leading or interior whitespace.
        normalized = normalized.rstrip()
        encoded = normalized.encode("utf-8")
    except (UnicodeError, ValueError):
        raise PromptContextError() from None
    if len(encoded) > maximum_bytes:
        raise PromptContextError()
    return normalized


@dataclass(frozen=True, slots=True)
class PromptEnvironment:
    model: str
    platform: str
    locale: str
    timezone: str
    date: str
    workspace_label: str | None

    def __post_init__(self) -> None:
        object.__setattr__(self, "model", _require_string(self.model, 256))
        object.__setattr__(self, "platform", _require_string(self.platform, 64))
        object.__setattr__(self, "locale", _require_string(self.locale, 64))
        object.__setattr__(self, "timezone", _require_string(self.timezone, 128))
        object.__setattr__(self, "date", _require_string(self.date, 32))
        object.__setattr__(self, "workspace_label", _optional_string(self.workspace_label, 128))


@dataclass(frozen=True, slots=True)
class PromptReference:
    kind: str
    label: str
    content: str

    def __post_init__(self) -> None:
        object.__setattr__(self, "kind", _require_identifier(self.kind, 64))
        object.__setattr__(self, "label", _require_string(self.label, 256))
        object.__setattr__(self, "content", normalize_text(self.content, maximum_bytes=MAX_PROMPT_REFERENCE_BYTES))
        if not self.content:
            raise PromptContextError()


@dataclass(frozen=True, slots=True)
class InstructionBlock:
    source_id: str
    display_name: str
    content: str
    content_sha256: str

    def __post_init__(self) -> None:
        object.__setattr__(self, "source_id", _require_identifier(self.source_id, 128, allow_colon=True))
        object.__setattr__(self, "display_name", _require_string(self.display_name, 256))
        content = normalize_text(self.content, maximum_bytes=MAX_PROMPT_INSTRUCTION_BYTES)
        if not content:
            raise PromptContextError()
        object.__setattr__(self, "content", content)
        digest = _require_digest(self.content_sha256)
        if digest != sha256_utf8(content):
            raise PromptContextError()
        object.__setattr__(self, "content_sha256", digest)


@dataclass(frozen=True, slots=True)
class Capability:
    id: str
    version: str
    summary: str
    input_schema_digest: str
    policy_summary: str
    availability: str

    def __post_init__(self) -> None:
        object.__setattr__(self, "id", _require_identifier(self.id, 128))
        version = _require_string(self.version, 32)
        if _SEMVER.fullmatch(version) is None:
            raise PromptContextError()
        object.__setattr__(self, "version", version)
        object.__setattr__(self, "summary", _require_string(self.summary, 2 * 1024))
        object.__setattr__(self, "input_schema_digest", _require_digest(self.input_schema_digest))
        object.__setattr__(self, "policy_summary", _require_string(self.policy_summary, 2 * 1024))
        object.__setattr__(self, "availability", _require_identifier(self.availability, 32))


@dataclass(frozen=True, slots=True)
class PromptRuntimeContext:
    memory: str | None
    mood: str | None
    voice: str | None
    vts: str | None
    hardware: str | None
    references: tuple[PromptReference, ...]

    def __post_init__(self) -> None:
        object.__setattr__(
            self,
            "memory",
            _optional_text(self.memory, MAX_PROMPT_MEMORY_BYTES),
        )
        object.__setattr__(self, "mood", _optional_string(self.mood, 256))
        object.__setattr__(self, "voice", _optional_string(self.voice, 2 * 1024))
        object.__setattr__(self, "vts", _optional_string(self.vts, 2 * 1024))
        object.__setattr__(self, "hardware", _optional_string(self.hardware, 2 * 1024))
        if not isinstance(self.references, tuple):
            raise PromptContextError()
        if len(self.references) > MAX_PROMPT_COLLECTION_ITEMS or any(
            not isinstance(reference, PromptReference) for reference in self.references
        ):
            raise PromptContextError()


@dataclass(frozen=True, slots=True)
class SystemPromptContext:
    schema_version: int
    agent_profile: str
    preset: str
    environment: PromptEnvironment
    instruction_blocks: tuple[InstructionBlock, ...]
    capabilities: tuple[Capability, ...]
    runtime_context: PromptRuntimeContext

    def __post_init__(self) -> None:
        if self.schema_version != PROMPT_CONTEXT_SCHEMA_VERSION:
            raise PromptContextError()
        if self.agent_profile != "ru-agent":
            raise PromptContextError()
        if self.preset not in {"standard", "full_access"}:
            raise PromptContextError()
        if not isinstance(self.environment, PromptEnvironment) or not isinstance(
            self.runtime_context, PromptRuntimeContext
        ):
            raise PromptContextError()
        if not isinstance(self.instruction_blocks, tuple) or len(self.instruction_blocks) > MAX_PROMPT_COLLECTION_ITEMS:
            raise PromptContextError()
        if not isinstance(self.capabilities, tuple) or len(self.capabilities) > MAX_PROMPT_COLLECTION_ITEMS:
            raise PromptContextError()
        if any(not isinstance(block, InstructionBlock) for block in self.instruction_blocks):
            raise PromptContextError()
        if any(not isinstance(capability, Capability) for capability in self.capabilities):
            raise PromptContextError()
        instruction_ids = [block.source_id for block in self.instruction_blocks]
        capability_ids = [capability.id for capability in self.capabilities]
        if len(instruction_ids) != len(set(instruction_ids)) or len(capability_ids) != len(set(capability_ids)):
            raise PromptContextError()
        if sum(len(block.content.encode("utf-8")) for block in self.instruction_blocks) > MAX_COMBINED_PROMPT_INSTRUCTION_BYTES:
            raise PromptContextError()
        if len(json.dumps(self.to_payload(), ensure_ascii=False, separators=(",", ":")).encode("utf-8")) > MAX_PROMPT_CONTEXT_BYTES:
            raise PromptContextError()

    def to_payload(self) -> dict[str, Any]:
        return {
            "schema_version": self.schema_version,
            "agent_profile": self.agent_profile,
            "preset": self.preset,
            "environment": {
                "model": self.environment.model,
                "platform": self.environment.platform,
                "locale": self.environment.locale,
                "timezone": self.environment.timezone,
                "date": self.environment.date,
                "workspace_label": self.environment.workspace_label,
            },
            "instruction_blocks": [
                {
                    "source_id": block.source_id,
                    "display_name": block.display_name,
                    "content": block.content,
                    "content_sha256": block.content_sha256,
                }
                for block in self.instruction_blocks
            ],
            "capabilities": [
                {
                    "id": capability.id,
                    "version": capability.version,
                    "summary": capability.summary,
                    "input_schema_digest": capability.input_schema_digest,
                    "policy_summary": capability.policy_summary,
                    "availability": capability.availability,
                }
                for capability in self.capabilities
            ],
            "runtime_context": {
                "memory": self.runtime_context.memory,
                "mood": self.runtime_context.mood,
                "voice": self.runtime_context.voice,
                "vts": self.runtime_context.vts,
                "hardware": self.runtime_context.hardware,
                "references": [
                    {
                        "kind": reference.kind,
                        "label": reference.label,
                        "content": reference.content,
                    }
                    for reference in self.runtime_context.references
                ],
            },
        }


def parse_system_prompt_context(value: Mapping[str, Any]) -> SystemPromptContext:
    try:
        if not isinstance(value, Mapping):
            raise PromptContextError()
        _require_exact_fields(
            value,
            {
                "schema_version",
                "agent_profile",
                "preset",
                "environment",
                "instruction_blocks",
                "capabilities",
                "runtime_context",
            },
        )
        environment_value = value["environment"]
        if not isinstance(environment_value, Mapping):
            raise PromptContextError()
        _require_exact_fields(
            environment_value,
            {"model", "platform", "locale", "timezone", "date", "workspace_label"},
        )
        instruction_values = value["instruction_blocks"]
        capability_values = value["capabilities"]
        if not isinstance(instruction_values, list) or not isinstance(capability_values, list):
            raise PromptContextError()
        instructions = tuple(_parse_instruction(item) for item in instruction_values)
        capabilities = tuple(_parse_capability(item) for item in capability_values)
        runtime_value = value["runtime_context"]
        if not isinstance(runtime_value, Mapping):
            raise PromptContextError()
        _require_exact_fields(runtime_value, {"memory", "mood", "voice", "vts", "hardware", "references"})
        references_value = runtime_value["references"]
        if not isinstance(references_value, list):
            raise PromptContextError()
        references = tuple(_parse_reference(item) for item in references_value)
        return SystemPromptContext(
            schema_version=value["schema_version"],
            agent_profile=value["agent_profile"],
            preset=value["preset"],
            environment=PromptEnvironment(
                model=environment_value["model"],
                platform=environment_value["platform"],
                locale=environment_value["locale"],
                timezone=environment_value["timezone"],
                date=environment_value["date"],
                workspace_label=environment_value["workspace_label"],
            ),
            instruction_blocks=instructions,
            capabilities=capabilities,
            runtime_context=PromptRuntimeContext(
                memory=runtime_value["memory"],
                mood=runtime_value["mood"],
                voice=runtime_value["voice"],
                vts=runtime_value["vts"],
                hardware=runtime_value["hardware"],
                references=references,
            ),
        )
    except PromptContextError:
        raise
    except (KeyError, TypeError, ValueError, UnicodeError, OverflowError):
        raise PromptContextError() from None


def _parse_instruction(value: object) -> InstructionBlock:
    if not isinstance(value, Mapping):
        raise PromptContextError()
    _require_exact_fields(value, {"source_id", "display_name", "content", "content_sha256"})
    return InstructionBlock(
        source_id=value["source_id"],
        display_name=value["display_name"],
        content=value["content"],
        content_sha256=value["content_sha256"],
    )


def _parse_capability(value: object) -> Capability:
    if not isinstance(value, Mapping):
        raise PromptContextError()
    _require_exact_fields(
        value,
        {"id", "version", "summary", "input_schema_digest", "policy_summary", "availability"},
    )
    return Capability(
        id=value["id"],
        version=value["version"],
        summary=value["summary"],
        input_schema_digest=value["input_schema_digest"],
        policy_summary=value["policy_summary"],
        availability=value["availability"],
    )


def _parse_reference(value: object) -> PromptReference:
    if not isinstance(value, Mapping):
        raise PromptContextError()
    _require_exact_fields(value, {"kind", "label", "content"})
    return PromptReference(kind=value["kind"], label=value["label"], content=value["content"])


def _require_exact_fields(value: Mapping[str, Any], fields: set[str]) -> None:
    if set(value) != fields:
        raise PromptContextError()


def _require_string(value: object, maximum: int) -> str:
    if not isinstance(value, str) or not value.strip() or "\x00" in value or len(value) > maximum:
        raise PromptContextError()
    try:
        value.encode("utf-8")
    except UnicodeEncodeError:
        raise PromptContextError() from None
    return value


def _optional_string(value: object, maximum: int) -> str | None:
    if value is None:
        return None
    if not isinstance(value, str) or "\x00" in value or len(value) > maximum:
        raise PromptContextError()
    try:
        value.encode("utf-8")
    except UnicodeEncodeError:
        raise PromptContextError() from None
    return value.strip() or None


def _optional_text(value: object, maximum_bytes: int) -> str | None:
    if value is None:
        return None
    if not isinstance(value, str):
        raise PromptContextError()
    normalized = normalize_text(value, maximum_bytes=maximum_bytes)
    return normalized or None


def _require_identifier(value: object, maximum: int, *, allow_colon: bool = False) -> str:
    text = _require_string(value, maximum)
    pattern = _PROMPT_SOURCE_IDENTIFIER if allow_colon else _SAFE_IDENTIFIER
    if pattern.fullmatch(text) is None:
        raise PromptContextError()
    return text


def _require_digest(value: object) -> str:
    if not isinstance(value, str) or len(value) != 64 or any(
        character not in "0123456789abcdef" for character in value
    ):
        raise PromptContextError()
    return value


__all__ = [
    "Capability",
    "InstructionBlock",
    "PromptContextError",
    "PromptEnvironment",
    "PromptReference",
    "PromptRuntimeContext",
    "SystemPromptContext",
    "MAX_COMBINED_PROMPT_INSTRUCTION_BYTES",
    "MAX_PROMPT_CONTEXT_BYTES",
    "MAX_PROMPT_INSTRUCTION_BYTES",
    "MAX_PROMPT_MEMORY_BYTES",
    "MAX_PROMPT_REFERENCE_BYTES",
    "PROMPT_CONTEXT_SCHEMA_VERSION",
    "normalize_text",
    "parse_system_prompt_context",
    "sha256_utf8",
]
