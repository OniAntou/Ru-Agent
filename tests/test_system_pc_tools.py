from __future__ import annotations

import datetime

from ru_runtime.generation.context import build_conversation_messages
from ru_runtime.system.pc_tools import (
    describe_pc_hardware_for_prompt,
    get_pc_hardware_status,
    get_realtime_clock_context,
)


def test_get_realtime_clock_context_fixed_date() -> None:
    fixed_time = datetime.datetime(2026, 8, 19, 21, 30, 45)
    context = get_realtime_clock_context(fixed_time)

    assert "21:30:45" in context
    assert "Thứ Tư" in context
    assert "19/08/2026" in context
    assert "Thời gian thực tế trên máy tính" in context


def test_get_pc_hardware_status_is_read_only_telemetry() -> None:
    status = get_pc_hardware_status()

    assert "os" in status
    assert isinstance(status["os"], str)


def test_describe_pc_hardware_for_prompt() -> None:
    description = describe_pc_hardware_for_prompt()

    assert "Trạng thái máy tính của anh:" in description
    assert "Hệ điều hành:" in description


def test_build_conversation_messages_injects_clock_and_hardware() -> None:
    messages = build_conversation_messages(
        system_prompt="Ru system prompt",
        prompt="Mấy giờ rồi em?",
        turns=(),
        system_clock_context="Thời gian: 21:30",
        system_hardware_context="Pin: 80%",
    )

    assert any(
        "Thời gian: 21:30" in content
        for role, content in messages
        if role == "system"
    )
    assert any("Pin: 80%" in content for role, content in messages if role == "system")
