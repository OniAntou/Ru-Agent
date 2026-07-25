from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

from mini_gpt.gemma4_training import (
    Gemma4TrainingConfig,
    GemmaMessage,
    encode_gemma_response,
    validate_gemma4_adapter_dir,
)


class _FakeTokenizer:
    eos_token_id = 2
    pad_token_id = 0

    def apply_chat_template(self, messages, **kwargs):
        assert kwargs["add_generation_prompt"] is True
        assert kwargs["enable_thinking"] is False
        assert messages[-1]["role"] == "user"
        return [101, 102, 103]

    def __call__(self, text, *, add_special_tokens):
        assert add_special_tokens is False
        return {"input_ids": [7, 8]}


def test_light_config_matches_the_bounded_gemma_profile():
    config_path = Path(__file__).parents[1] / "configs" / "gemma4_e4b_ru_light.json"
    config = Gemma4TrainingConfig.from_json(config_path)

    assert config.base_model_path == Path(r"D:\LLM\models\gemma-4-e4b-it-hf")
    assert config.dataset_dir == Path(r"D:\LLM\data\conversations\ru_v7_1")
    assert config.max_length == 512
    assert config.max_steps == 64
    assert config.gradient_accumulation_steps == 8
    assert config.lora_rank == 4
    assert config.target_modules == ("q_proj", "k_proj", "v_proj", "o_proj")
    assert config.quantization_bits == 4
    assert config.physical_vram_cap_mib == 6144


def test_encode_gemma_response_masks_prompt_and_keeps_response_tokens():
    encoded = encode_gemma_response(
        _FakeTokenizer(),
        [
            GemmaMessage(role="system", content="Be Ru."),
            GemmaMessage(role="user", content="Chào em."),
        ],
        "Em chào anh.",
        max_length=8,
    )

    assert encoded.input_ids == [101, 102, 103, 7, 8, 2]
    assert encoded.labels == [-100, -100, -100, 7, 8, 2]
    assert encoded.response_token_count == 3


def test_encode_gemma_response_accepts_batch_encoding_chat_template_output():
    class BatchEncodingTokenizer(_FakeTokenizer):
        def apply_chat_template(self, messages, **kwargs):
            return {"input_ids": [201, 202]}

    encoded = encode_gemma_response(
        BatchEncodingTokenizer(),
        [GemmaMessage(role="user", content="Chào em.")],
        "Em chào anh.",
        max_length=8,
    )

    assert encoded.input_ids == [201, 202, 7, 8, 2]


def test_encode_gemma_response_left_truncates_only_the_prompt():
    class LongPromptTokenizer(_FakeTokenizer):
        def apply_chat_template(self, messages, **kwargs):
            return list(range(10, 16))

    encoded = encode_gemma_response(
        LongPromptTokenizer(),
        [GemmaMessage(role="user", content="Một câu hỏi dài.")],
        "Được ạ.",
        max_length=7,
    )

    assert encoded.input_ids == [12, 13, 14, 15, 7, 8, 2]
    assert encoded.labels == [-100, -100, -100, -100, 7, 8, 2]


def test_encode_gemma_response_rejects_context_without_user_turn():
    with pytest.raises(ValueError, match="last message must be a user"):
        encode_gemma_response(
            _FakeTokenizer(),
            [GemmaMessage(role="assistant", content="Không hợp lệ")],
            "Phản hồi.",
            max_length=8,
        )


def test_adapter_output_guard_does_not_touch_llama_checkpoints(tmp_path):
    with pytest.raises(ValueError, match="reserved"):
        validate_gemma4_adapter_dir(tmp_path / "llama3.1-8b-ru-v7.1")

    output_dir = tmp_path / "gemma4-e4b-ru-light-smoke"
    assert validate_gemma4_adapter_dir(output_dir) == output_dir


def test_train_script_exposes_smoke_overrides():
    script = Path(__file__).parents[1] / "scripts" / "train_gemma4_e4b_ru.py"
    completed = subprocess.run(
        [sys.executable, str(script), "--help"],
        check=True,
        capture_output=True,
        text=True,
    )

    assert "--max-steps" in completed.stdout
    assert "--output-dir" in completed.stdout
