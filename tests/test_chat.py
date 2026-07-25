import pytest

from mini_gpt.chat import ChatTurn


def test_chat_turn_rejects_empty_messages():
    with pytest.raises(ValueError, match="prompt"):
        ChatTurn("", "response")
    with pytest.raises(ValueError, match="response"):
        ChatTurn("prompt", "")
