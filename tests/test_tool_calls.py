from __future__ import annotations

import pytest

from ru_runtime.generation.tool_calls import (
    ToolCall,
    ToolCallParseError,
    parse_legacy_volume_intent,
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


@pytest.mark.parametrize(
    ("marker", "arguments"),
    (
        ("[VOLUME: get]", {"operation": "get"}),
        ("[VOLUME: set 2]", {"operation": "set", "level": 2}),
        ("[VOLUME: increase 5]", {"operation": "increase", "amount": 5}),
        ("[VOLUME: decrease 5]", {"operation": "decrease", "amount": 5}),
        ("[VOLUME: mute]", {"operation": "mute"}),
        ("[VOLUME: unmute]", {"operation": "unmute"}),
        ("[VOLUME: up]", {"operation": "increase", "amount": 4}),
        ("[VOLUME: down]", {"operation": "decrease", "amount": 4}),
    ),
)
def test_legacy_volume_marker_translates_to_intent(
    marker: str,
    arguments: dict[str, object],
) -> None:
    call = parse_legacy_volume_intent(marker)

    assert call == ToolCall("system.volume", "1.0.0", arguments)


def test_legacy_volume_marker_discards_surrounding_success_prose() -> None:
    call = parse_legacy_volume_intent("[VOLUME: set 2] Em đã chỉnh xong.")

    assert call == ToolCall(
        "system.volume",
        "1.0.0",
        {"operation": "set", "level": 2},
    )


@pytest.mark.parametrize(
    "text",
    (
        "[VOLUME: mute] [VOLUME: unmute]",
        "[APP_OPEN: notepad] [VOLUME: set 2]",
        "[SEARCH: news] [VOLUME: set 2]",
        "[[VTS:vts_01]] [VOLUME: set 2]",
        "[VOLUME: unknown]",
        "[VOLUME: set true]",
        "[VOLUME: set 101]",
        "[VOLUME: increase 0]",
        "[VOLUME: decrease 51]",
    ),
)
def test_legacy_volume_marker_rejects_ambiguous_or_invalid_input(text: str) -> None:
    with pytest.raises(ToolCallParseError):
        parse_legacy_volume_intent(text)
