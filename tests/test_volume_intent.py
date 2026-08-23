from __future__ import annotations

import pytest

from ru_runtime.generation.volume_intent import resolve_volume_intent


@pytest.mark.parametrize(
    ("prompt", "arguments"),
    (
        ("Giảm âm lượng của anh xuống còn 2", {"operation": "set", "level": 2}),
        ("Đặt âm lượng về 0%", {"operation": "set", "level": 0}),
        ("Set the volume to 100 percent", {"operation": "set", "level": 100}),
        ("Tăng âm lượng lên 10", {"operation": "set", "level": 10}),
        ("Tăng âm lượng lên mức 10", {"operation": "set", "level": 10}),
        ("Tăng âm lượng thêm 10", {"operation": "increase", "amount": 10}),
        ("Tăng âm lượng thêm 5", {"operation": "increase", "amount": 5}),
        ("Giảm âm lượng đi 1%", {"operation": "decrease", "amount": 1}),
        ("Decrease the volume by 50 percent", {"operation": "decrease", "amount": 50}),
        ("Tắt tiếng máy giúp anh", {"operation": "mute"}),
        ("Bật lại tiếng máy", {"operation": "unmute"}),
        ("Âm lượng hiện tại là bao nhiêu?", {"operation": "get"}),
        ("What is the current volume?", {"operation": "get"}),
    ),
)
def test_explicit_commands_resolve_to_one_exact_tool_call(
    prompt: str,
    arguments: dict[str, object],
) -> None:
    decision = resolve_volume_intent(prompt)

    assert decision.kind == "execute"
    assert decision.reason_code is None
    assert decision.tool_call is not None
    assert decision.tool_call.tool_id == "system.volume"
    assert decision.tool_call.tool_version == "1.0.0"
    assert decision.tool_call.arguments == arguments


@pytest.mark.parametrize(
    ("prompt", "reason_code"),
    (
        ("Giảm âm lượng 2%", "volume_command_ambiguous"),
        ("Tăng âm lượng", "volume_command_ambiguous"),
        ("Đặt âm lượng về 101", "volume_value_out_of_range"),
        ("Đặt âm lượng về -1", "volume_value_out_of_range"),
        ("Tăng âm lượng thêm 0", "volume_value_out_of_range"),
        ("Giảm âm lượng đi 51", "volume_value_out_of_range"),
        ("Tăng âm lượng lên 101", "volume_value_out_of_range"),
        ("Đặt âm lượng về 2.5", "volume_command_ambiguous"),
        ("Tắt tiếng rồi bật tiếng", "volume_command_ambiguous"),
        ("Em chỉnh âm lượng được không?", "volume_capability_question"),
    ),
)
def test_unclear_or_invalid_volume_requests_never_execute(
    prompt: str,
    reason_code: str,
) -> None:
    decision = resolve_volume_intent(prompt)

    assert decision.kind == "clarification_required"
    assert decision.reason_code == reason_code
    assert decision.tool_call is None


@pytest.mark.parametrize(
    "prompt",
    (
        "Đừng giảm âm lượng",
        "Nếu giảm âm lượng xuống 2 thì sao?",
        "Giải thích câu 'giảm âm lượng xuống còn 2'",
        "Anh đang nói về volume trong Docker",
        "Bài hát này có âm lượng cảm xúc lớn",
        "Tăng lên 10",
        "Hôm nay em thế nào?",
    ),
)
def test_negated_quoted_hypothetical_and_ordinary_prose_stays_non_action(
    prompt: str,
) -> None:
    decision = resolve_volume_intent(prompt)

    assert decision.kind == "not_action"
    assert decision.reason_code is None
    assert decision.tool_call is None


@pytest.mark.parametrize(
    "prompt",
    (
        "Đặt âm lượng về 20 rồi đặt âm lượng về 30",
        "Đặt âm lượng về 20, đặt âm lượng về 30",
        "Set the volume to 20 and decrease the volume by 5 percent",
    ),
)
def test_repeated_or_conflicting_commands_require_clarification(prompt: str) -> None:
    decision = resolve_volume_intent(prompt)

    assert decision.kind == "clarification_required"
    assert decision.reason_code == "volume_command_ambiguous"
    assert decision.tool_call is None


@pytest.mark.parametrize(
    "prompt",
    (
        "Đặt âm lượng về true",
        "Set the volume to false",
        "Chỉnh âm lượng thành mức dễ nghe",
    ),
)
def test_command_shaped_unknown_values_require_clarification(prompt: str) -> None:
    decision = resolve_volume_intent(prompt)

    assert decision.kind == "clarification_required"
    assert decision.reason_code == "volume_command_ambiguous"
    assert decision.tool_call is None


@pytest.mark.parametrize(
    ("prompt", "arguments"),
    (
        ("  ĐẶT   ÂM LƯỢNG VỀ 25%!!!  ", {"operation": "set", "level": 25}),
        (
            "Ｓｅｔ ｔｈｅ ｖｏｌｕｍｅ ｔｏ ３０ ｐｅｒｃｅｎｔ",
            {"operation": "set", "level": 30},
        ),
        ("Tăng âm lượng thêm 1", {"operation": "increase", "amount": 1}),
        ("Tăng âm lượng thêm 50", {"operation": "increase", "amount": 50}),
        ("Đặt âm lượng về 0", {"operation": "set", "level": 0}),
        ("Đặt âm lượng về 100", {"operation": "set", "level": 100}),
    ),
)
def test_normalization_and_inclusive_boundaries_are_stable(
    prompt: str,
    arguments: dict[str, object],
) -> None:
    decision = resolve_volume_intent(prompt)

    assert decision.kind == "execute"
    assert decision.tool_call is not None
    assert decision.tool_call.arguments == arguments


@pytest.mark.parametrize(
    "prompt",
    (
        "Em có thể đặt âm lượng về 2 không?",
        "Can you set the volume to 20 percent?",
    ),
)
def test_capability_questions_with_values_never_execute(prompt: str) -> None:
    decision = resolve_volume_intent(prompt)

    assert decision.kind == "clarification_required"
    assert decision.reason_code == "volume_capability_question"
    assert decision.tool_call is None


@pytest.mark.parametrize(
    "prompt",
    (
        "Anh đừng giảm âm lượng xuống còn 2",
        "Please don't set the volume to 2",
        "Anh muốn thảo luận về cách giảm âm lượng trong Windows",
        "This article explains how to lower the volume",
    ),
)
def test_embedded_negation_and_discussion_context_stays_non_action(prompt: str) -> None:
    decision = resolve_volume_intent(prompt)

    assert decision.kind == "not_action"
    assert decision.reason_code is None
    assert decision.tool_call is None
