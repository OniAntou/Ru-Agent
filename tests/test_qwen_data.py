from mini_gpt.qwen_data import EncodedQwenResponse, QwenMessage, QwenResponseCollator, encode_qwen_response


class FakeQwenTokenizer:
    eos_token_id = 99

    def apply_chat_template(self, messages, *, tokenize, add_generation_prompt):
        assert tokenize is True
        assert add_generation_prompt is True
        assert messages[-1]["role"] == "user"
        return [10, 11, 12]

    def __call__(self, text, *, add_special_tokens):
        assert text == "Chào anh!"
        assert add_special_tokens is False
        return {"input_ids": [21, 22]}


def test_encode_qwen_response_masks_chat_prefix_and_scores_response_end_marker():
    encoded = encode_qwen_response(
        FakeQwenTokenizer(),
        messages=(QwenMessage("system", "You are Ru."), QwenMessage("user", "Xin chào")),
        response="Chào anh!",
        max_length=16,
    )

    assert encoded.input_ids == [10, 11, 12, 21, 22, 99]
    assert encoded.labels == [-100, -100, -100, 21, 22, 99]


def test_collator_left_pads_inputs_and_keeps_label_padding_ignored():
    batch = QwenResponseCollator(pad_token_id=0)(
        [
            EncodedQwenResponse([10, 21, 99], [-100, 21, 99]),
            EncodedQwenResponse([10, 11, 22, 99], [-100, -100, 22, 99]),
        ]
    )

    assert batch["input_ids"].tolist() == [[0, 10, 21, 99], [10, 11, 22, 99]]
    assert batch["attention_mask"].tolist() == [[0, 1, 1, 1], [1, 1, 1, 1]]
    assert batch["labels"].tolist() == [[-100, -100, 21, 99], [-100, -100, 22, 99]]
