from __future__ import annotations

import pytest

from ru_runtime.generation.tool_calls import (
    ToolCallParseError,
    parse_tool_call,
)


def test_parse_tool_call_accepts_only_the_structured_json_shape() -> None:
    call = parse_tool_call(
        '{"tool_id":"files.list","tool_version":"1.0.0","arguments":{"path":"src"}}'
    )

    assert call is not None
    assert call.tool_id == "files.list"
    assert call.tool_version == "1.0.0"
    assert call.arguments == {"path": "src"}


@pytest.mark.parametrize(
    "text",
    [
        "Em nghe đây.",
        "[APP_OPEN: notepad]",
        "[VOLUME: up]",
        "[SEARCH: latest news]",
        '["files.list", {"path":"src"}]',
    ],
)
def test_parser_does_not_interpret_markers_or_non_object_text(text: str) -> None:
    assert parse_tool_call(text) is None


def test_parser_rejects_a_partial_structured_call() -> None:
    with pytest.raises(ToolCallParseError, match="tool_version"):
        parse_tool_call('{"tool_id":"files.list","arguments":{}}')
