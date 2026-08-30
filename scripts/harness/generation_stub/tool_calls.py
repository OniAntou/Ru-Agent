from __future__ import annotations

import json
import re
from dataclasses import dataclass
from typing import Any

_SEMVER = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")
_MAX_TOOL_ID_CHARS = 128
_MAX_TOOL_ARGUMENT_BYTES = 64 * 1024


class ToolCallParseError(ValueError):
    pass


@dataclass(frozen=True, slots=True)
class ToolCall:
    tool_id: str
    tool_version: str
    arguments: dict[str, Any]


def parse_tool_call(text: str) -> ToolCall | None:
    """Parse a model tool call without interpreting legacy marker syntax.

    Ordinary prose and JSON that is not a tool-call object are not tool calls.
    Once a JSON object identifies itself as a tool call, malformed fields fail
    closed so callers cannot silently execute a partial request.
    """
    if not isinstance(text, str):
        return None
    try:
        value = json.loads(text.strip())
    except (json.JSONDecodeError, TypeError):
        return None
    if not isinstance(value, dict):
        return None
    if not ({"tool_id", "tool_version", "arguments"} & value.keys()):
        return None
    missing = {"tool_id", "tool_version", "arguments"} - value.keys()
    if missing:
        raise ToolCallParseError(next(iter(sorted(missing))))
    if set(value) != {"tool_id", "tool_version", "arguments"}:
        raise ToolCallParseError("unexpected tool call fields")

    tool_id = value["tool_id"]
    tool_version = value["tool_version"]
    arguments = value["arguments"]
    if not isinstance(tool_id, str) or not tool_id.strip() or len(tool_id) > _MAX_TOOL_ID_CHARS:
        raise ToolCallParseError("tool_id")
    if not isinstance(tool_version, str) or not _SEMVER.fullmatch(tool_version):
        raise ToolCallParseError("tool_version")
    if not isinstance(arguments, dict):
        raise ToolCallParseError("arguments")
    encoded_arguments = json.dumps(arguments, ensure_ascii=False, separators=(",", ":"))
    if len(encoded_arguments.encode("utf-8")) > _MAX_TOOL_ARGUMENT_BYTES:
        raise ToolCallParseError("arguments")
    return ToolCall(tool_id.strip(), tool_version, arguments)
