from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Literal

from ru_runtime.policy.text import normalize_text

IGNORE_LABEL = -100


@dataclass(frozen=True)
class LfmMessage:
    role: Literal["system", "user", "assistant"]
    content: str

    def __post_init__(self) -> None:
        if self.role not in {"system", "user", "assistant"}:
            raise ValueError("Role must be system, user, or assistant")
        if not normalize_text(self.content):
            raise ValueError("Message content cannot be empty")


@dataclass(frozen=True)
class EncodedResponse:
    input_ids: list[int]
    labels: list[int]


def encode_lfm_response(
    tokenizer: Any,
    messages: tuple[LfmMessage, ...],
    response: str,
    max_length: int,
) -> EncodedResponse:
    if max_length <= 0:
        raise ValueError("max_length must be positive")
    if not messages or messages[-1].role != "user":
        raise ValueError("Context must end with a user message")
    response = normalize_text(response)
    if not response:
        raise ValueError("Response cannot be empty")

    prompt_ids = tokenizer.apply_chat_template(
        [{"role": message.role, "content": message.content} for message in messages],
        tokenize=True,
        add_generation_prompt=True,
    )
    if hasattr(prompt_ids, "__contains__") and "input_ids" in prompt_ids:
        prompt_ids = prompt_ids["input_ids"]
    elif hasattr(prompt_ids, "ids"):
        prompt_ids = prompt_ids.ids

    # LFM2 (Qwen-style) chat templates end turns with <|im_end|>; older
    # templates may expose <|eot_id|>. Prefer the first known terminator.
    terminator_token_id = None
    for candidate in ("<|im_end|>", "<|eot_id|>"):
        candidate_id = tokenizer.convert_tokens_to_ids(candidate)
        if candidate_id is not None and candidate_id != tokenizer.unk_token_id:
            terminator_token_id = candidate_id
            break
    if terminator_token_id is None:
        terminator_token_id = tokenizer.eos_token_id

    response_ids = [
        *tokenizer(response, add_special_tokens=False)["input_ids"],
        terminator_token_id,
    ]
    if len(response_ids) >= max_length:
        raise ValueError("assistant response does not fit within max_length")
    prompt_ids = prompt_ids[-(max_length - len(response_ids)) :]
    return EncodedResponse(
        input_ids=[*prompt_ids, *response_ids],
        labels=[IGNORE_LABEL] * len(prompt_ids) + response_ids,
    )