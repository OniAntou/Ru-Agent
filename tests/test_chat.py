import pytest

from mini_gpt.chat import ChatTurn, format_chat_prompt


def test_format_chat_prompt_includes_recent_turns_and_latest_prompt():
    turns = [ChatTurn("Xin chào", "Chào anh")]

    assert format_chat_prompt(turns, "Hôm nay thế nào?") == (
        "User: Xin chào\nAssistant: Chào anh\n"
        "User: Hôm nay thế nào?\nAssistant:"
    )


def test_format_chat_prompt_limits_old_turns():
    turns = [ChatTurn(f"q{i}", f"a{i}") for i in range(5)]

    result = format_chat_prompt(turns, "latest", max_turns=2)

    assert "q0" not in result
    assert "q3" in result and "q4" in result
    assert result.endswith("User: latest\nAssistant:")


def test_chat_turn_rejects_empty_messages():
    with pytest.raises(ValueError, match="prompt"):
        ChatTurn("", "response")
    with pytest.raises(ValueError, match="response"):
        ChatTurn("prompt", "")


def test_format_chat_prompt_rejects_invalid_arguments():
    with pytest.raises(ValueError, match="latest prompt"):
        format_chat_prompt([], "")
    with pytest.raises(ValueError, match="max_turns"):
        format_chat_prompt([], "prompt", max_turns=0)
