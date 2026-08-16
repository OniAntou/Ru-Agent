from __future__ import annotations

import json
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]

from tools.training.lfm25.config import Lfm25TrainingConfig
from tools.training.lfm25.data import (
    directory_fingerprint,
    training_dataset_fingerprint,
)
from tools.training.lfm25.encode import LfmMessage, encode_lfm_response


def test_approved_lfm25_config_loads_and_is_marked_approved() -> None:
    config = Lfm25TrainingConfig.from_json(ROOT / "configs/ru_lfm2_5_v1.json")

    assert config.base_model_id == "LiquidAI/LFM2.5-2.6B"
    assert config.backend == "lfm2.5-qlora"
    assert config.profile_name == "ru_lfm2_5_v1"
    assert config.training_status == "approved"


def test_lfm25_config_uses_qwen_style_lora_target_modules() -> None:
    config = Lfm25TrainingConfig.from_json(ROOT / "configs/ru_lfm2_5_v1.json")

    assert config.target_modules == (
        "self_attn.q_proj",
        "self_attn.k_proj",
        "self_attn.v_proj",
        "self_attn.out_proj",
        "feed_forward.w1",
        "feed_forward.w3",
    )


def test_lfm25_config_defaults_target_active_assets() -> None:
    config = Lfm25TrainingConfig()

    assert config.base_model_path == "models/lfm2.5-2.6b-hf"
    assert config.dataset_dir == "data/conversations/ru_v1"
    assert config.adapter_dir == "checkpoints/ru-lfm2.5-2.6b-v1"


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


def test_response_encoding_prefers_the_qwen_turn_terminator() -> None:
    tokenizer = FakeTokenizer({"<|im_end|>": 7, "<|eot_id|>": 8})

    encoded = encode_lfm_response(
        tokenizer,
        (LfmMessage("user", "Chào em."),),
        "Em chào anh.",
        max_length=16,
    )

    assert encoded.input_ids[-1] == 7
    assert encoded.labels[-1] == 7


def test_response_encoding_falls_back_to_eos_for_unknown_templates() -> None:
    tokenizer = FakeTokenizer({})

    encoded = encode_lfm_response(
        tokenizer,
        (LfmMessage("user", "Hello."),),
        "Hi.",
        max_length=16,
    )

    assert encoded.input_ids[-1] == tokenizer.eos_token_id


def test_training_dataset_fingerprint_is_stable_and_content_aware(tmp_path: Path) -> None:
    dataset = tmp_path / "dataset"
    dataset.mkdir()
    (dataset / "train.jsonl").write_text("row", encoding="utf-8")
    (dataset / "val.jsonl").write_text("row", encoding="utf-8")
    (dataset / "manifest.json").write_text("{}", encoding="utf-8")

    first = training_dataset_fingerprint(dataset)
    second = training_dataset_fingerprint(dataset)
    assert first == second
    assert set(first["files"]) == {"train.jsonl", "val.jsonl", "manifest.json"}

    (dataset / "train.jsonl").write_text("row-changed", encoding="utf-8")
    assert training_dataset_fingerprint(dataset) != first


def test_directory_fingerprint_covers_all_files(tmp_path: Path) -> None:
    root = tmp_path / "model"
    (root / "nested").mkdir(parents=True)
    (root / "config.json").write_text("a", encoding="utf-8")
    (root / "nested" / "weights.bin").write_text("b", encoding="utf-8")

    digest = directory_fingerprint(root)
    assert len(digest) == 64


def test_lfm25_training_module_exposes_the_bounded_cli() -> None:
    cli = ROOT / "tools/training/lfm25/__main__.py"
    assert cli.is_file()
    source = cli.read_text(encoding="utf-8")
    assert "--config" in source
    assert "--max-steps" in source


def test_no_legacy_llama_training_seam_remains() -> None:
    assert not (ROOT / "src/ru_runtime/models/llama3_1_training.py").exists()
    assert not (ROOT / "tests/test_training_compatibility.py").exists()