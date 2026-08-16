from __future__ import annotations

from ru_runtime.models.llama3_1_training import LlamaMessage, encode_llama_response


class FakeTokenizer:
    unk_token_id = 0
    eos_token_id = 99

    def __init__(self, token_ids: dict[str, int]) -> None:
        self._token_ids = token_ids

    def apply_chat_template(self, *_args, **_kwargs) -> list[int]:
        return [10, 11]

    def convert_tokens_to_ids(self, token: str) -> int:
        return self._token_ids.get(token, self.unk_token_id)

    def __call__(self, _text: str, *, add_special_tokens: bool) -> dict[str, list[int]]:
        assert add_special_tokens is False
        return {"input_ids": [20, 21]}


def test_response_encoding_prefers_the_lfm25_qwen_turn_terminator() -> None:
    tokenizer = FakeTokenizer({"<|im_end|>": 7, "<|eot_id|>": 8})

    encoded = encode_llama_response(
        tokenizer,
        (LlamaMessage("user", "Chào em."),),
        "Em chào anh.",
        max_length=16,
    )

    assert encoded.input_ids[-1] == 7
    assert encoded.labels[-1] == 7


def test_response_encoding_falls_back_to_eos_for_unknown_templates() -> None:
    tokenizer = FakeTokenizer({})

    encoded = encode_llama_response(
        tokenizer,
        (LlamaMessage("user", "Hello."),),
        "Hi.",
        max_length=16,
    )

    assert encoded.input_ids[-1] == tokenizer.eos_token_id
