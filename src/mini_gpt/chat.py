from __future__ import annotations

from dataclasses import dataclass


@dataclass(slots=True)
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
        self.prompt = prompt
        self.response = response
