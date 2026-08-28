from __future__ import annotations

import pytest

from ru_runtime.generation.turns import MAX_TURN_LENGTH, ConversationTurn


def test_conversation_turn_normalizes_and_bounds_each_side() -> None:
    turn = ConversationTurn("  e\u0301  ", "  " + ("x" * (MAX_TURN_LENGTH + 10)) + "  ")

    assert turn.prompt == "é"
    assert len(turn.response) == MAX_TURN_LENGTH


@pytest.mark.parametrize("prompt,response", [("", "reply"), ("prompt", "")])
def test_conversation_turn_rejects_empty_sides(prompt: str, response: str) -> None:
    with pytest.raises(ValueError):
        ConversationTurn(prompt, response)
