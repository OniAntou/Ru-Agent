from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class ChatTurn:
    prompt: str
    response: str

    def __post_init__(self) -> None:
        prompt = self.prompt.strip()
        response = self.response.strip()
        if not prompt:
            raise ValueError("prompt cannot be empty")
        if not response:
            raise ValueError("response cannot be empty")
        object.__setattr__(self, "prompt", prompt)
        object.__setattr__(self, "response", response)


def format_chat_prompt(
    turns: Sequence[ChatTurn],
    latest_prompt: str,
    max_turns: int = 4,
) -> str:
    latest = latest_prompt.strip()
    if not latest:
        raise ValueError("latest prompt cannot be empty")
    if max_turns <= 0:
        raise ValueError("max_turns must be positive")

    blocks = [
        f"User: {turn.prompt}\nAssistant: {turn.response}"
        for turn in turns[-max_turns:]
    ]
    blocks.append(f"User: {latest}\nAssistant:")
    return "\n".join(blocks)
