from __future__ import annotations

from dataclasses import asdict, dataclass
from typing import Any, Literal

import torch

from .data import normalize_text

IGNORE_LABEL = -100


@dataclass(frozen=True)
class QwenMessage:
    role: Literal["system", "user", "assistant"]
    content: str

    def __post_init__(self) -> None:
        if self.role not in {"system", "user", "assistant"}:
            raise ValueError("Qwen message role must be system, user, or assistant")
        if not normalize_text(self.content):
            raise ValueError("Qwen message content cannot be empty")


@dataclass(frozen=True)
class EncodedQwenResponse:
    input_ids: list[int]
    labels: list[int]


class QwenResponseCollator:
    def __init__(self, pad_token_id: int) -> None:
        self.pad_token_id = pad_token_id

    def __call__(self, examples: list[EncodedQwenResponse]) -> dict[str, torch.Tensor]:
        if not examples:
            raise ValueError("Qwen response batch cannot be empty")
        max_length = max(len(example.input_ids) for example in examples)
        input_rows: list[list[int]] = []
        attention_rows: list[list[int]] = []
        label_rows: list[list[int]] = []
        for example in examples:
            if len(example.input_ids) != len(example.labels):
                raise ValueError("Qwen response inputs and labels must have equal lengths")
            padding = max_length - len(example.input_ids)
            input_rows.append([self.pad_token_id] * padding + example.input_ids)
            attention_rows.append([0] * padding + [1] * len(example.input_ids))
            label_rows.append([IGNORE_LABEL] * padding + example.labels)
        return {
            "input_ids": torch.tensor(input_rows, dtype=torch.long),
            "attention_mask": torch.tensor(attention_rows, dtype=torch.long),
            "labels": torch.tensor(label_rows, dtype=torch.long),
        }


def encode_qwen_response(
    tokenizer: Any,
    messages: tuple[QwenMessage, ...],
    response: str,
    max_length: int,
) -> EncodedQwenResponse:
    if max_length <= 0:
        raise ValueError("max_length must be positive")
    if not messages or messages[-1].role != "user":
        raise ValueError("Qwen response context must end with a user message")
    response = normalize_text(response)
    if not response:
        raise ValueError("Qwen response cannot be empty")
    if tokenizer.eos_token_id is None:
        raise ValueError("Qwen tokenizer must define an EOS token")

    prompt_ids = tokenizer.apply_chat_template(
        [asdict(message) for message in messages],
        tokenize=True,
        add_generation_prompt=True,
    )
    response_ids = [*tokenizer(response, add_special_tokens=False)["input_ids"], tokenizer.eos_token_id]
    if len(response_ids) >= max_length:
        raise ValueError("assistant response does not fit within max_length")
    prompt_ids = prompt_ids[-(max_length - len(response_ids)) :]
    return EncodedQwenResponse(
        input_ids=[*prompt_ids, *response_ids],
        labels=[IGNORE_LABEL] * len(prompt_ids) + response_ids,
    )
