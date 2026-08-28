from __future__ import annotations

import unicodedata
from dataclasses import dataclass

MAX_TURN_LENGTH = 50_000


@dataclass(slots=True)
class ConversationTurn:
    """A bounded user/assistant pair retained for prompt history."""

    prompt: str
    response: str

    def __post_init__(self) -> None:
        # NFC normalization keeps Vietnamese composed characters consistent so
        # identical turns from different inputs hash and compare the same.
        prompt = unicodedata.normalize("NFC", self.prompt).strip()
        response = unicodedata.normalize("NFC", self.response).strip()
        prompt = prompt[:MAX_TURN_LENGTH]
        response = response[:MAX_TURN_LENGTH]
        if not prompt:
            raise ValueError("prompt cannot be empty")
        if not response:
            raise ValueError("response cannot be empty")
        self.prompt = prompt
        self.response = response
