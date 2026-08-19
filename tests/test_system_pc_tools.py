from __future__ import annotations

import datetime
from unittest.mock import patch

from ru_runtime.system.pc_tools import (
    get_realtime_clock_context,
    get_pc_hardware_status,
    describe_pc_hardware_for_prompt,
    open_application_or_url,
    adjust_system_volume,
)
from ru_runtime.generation.context import build_conversation_messages
from ru_runtime.generation.orchestration import execute_reply


def test_get_realtime_clock_context_fixed_date():
    fixed_time = datetime.datetime(2026, 8, 19, 21, 30, 45)
    ctx = get_realtime_clock_context(fixed_time)
    assert "21:30:45" in ctx
    assert "Thứ Tư" in ctx
    assert "19/08/2026" in ctx
    assert "Thời gian thực tế trên máy tính" in ctx


def test_get_pc_hardware_status_keys():
    status = get_pc_hardware_status()
    assert "os" in status
    assert isinstance(status["os"], str)


def test_describe_pc_hardware_for_prompt():
    desc = describe_pc_hardware_for_prompt()
    assert "Trạng thái máy tính của anh:" in desc
    assert "Hệ điều hành:" in desc


def test_open_application_or_url_validation():
    # Invalid app with traversal characters should be rejected safely
    ok, msg = open_application_or_url("../../evil.exe")
    assert ok is False
    assert "Không tìm thấy ứng dụng hoặc không được phép mở" in msg

    # Web URL
    with patch("webbrowser.open", return_value=True) as mock_open:
        ok, msg = open_application_or_url("https://youtube.com")
        assert ok is True
        assert "Đã mở liên kết" in msg
        mock_open.assert_called_once_with("https://youtube.com")


def test_adjust_system_volume_invalid_action():
    ok, msg = adjust_system_volume("invalid_action_xyz")
    assert ok is False
    assert "không hợp lệ" in msg


def test_build_conversation_messages_injects_clock_and_hardware():
    messages = build_conversation_messages(
        system_prompt="Ru system prompt",
        prompt="Mấy giờ rồi em?",
        turns=(),
        system_clock_context="Thời gian: 21:30",
        system_hardware_context="Pin: 80%",
    )
    # The second system message should contain clock & hardware
    assert any("Thời gian: 21:30" in content for role, content in messages if role == "system")
    assert any("Pin: 80%" in content for role, content in messages if role == "system")


def test_execute_reply_executes_pc_tool_markers():
    executed_apps = []

    def mock_open(target):
        executed_apps.append(target)
        return True, "ok"

    with patch("ru_runtime.system.pc_tools.open_application_or_url", side_effect=mock_open):
        def generate(_messages, *_settings, **_kwargs):
            return "[APP_OPEN: notepad] Em đã mở Notepad cho anh rồi nè!"

        result = execute_reply(
            generate,
            prompt="Mở notepad hộ anh",
            system_instruction="Ru",
            max_new_tokens=32,
            temperature=0.7,
            top_k=20,
            allow_web_search=False,
        )

        assert executed_apps == ["notepad"]
        assert "[APP_OPEN:" not in result
        assert "Em đã mở Notepad cho anh rồi nè!" in result
