from __future__ import annotations

import json
from pathlib import Path

import pytest

from mini_gpt.llama3_1_training import (
    validate_v6_1_adapter_dir,
    validate_v6_1_source_adapter_dir,
)


PROJECT_ROOT = Path(__file__).resolve().parents[1]
CONFIG_PATH = PROJECT_ROOT / "configs" / "ru_llama3_1_8b_v6_1.json"


def test_v6_1_config_continues_from_v6_with_style_only_values():
    config = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))

    assert config["source_adapter_dir"].endswith("checkpoints/ru-llama3.1-8b-instruct-v6")
    assert config["dataset_dir"].endswith("data/conversations/ru_v6_1")
    assert config["adapter_dir"].endswith("checkpoints/ru-llama3.1-8b-instruct-v6.1")
    assert config["source_adapter_dir"] != config["adapter_dir"]
    assert config["max_length"] == 512
    assert config["learning_rate"] == 5e-6
    assert config["epochs"] == 3
    assert config["gradient_accumulation_steps"] == 16
    assert config["physical_vram_cap_mib"] == 6144
    assert config["profile_name"] == "ru_llama3_1_v6_1_from_v6"


@pytest.mark.parametrize(
    "path",
    [
        "D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v6",
        "D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v7",
        "D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v7.1",
        "D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v8",
        "D:/LLM/checkpoints/other",
        "D:/LLM/other/ru-llama3.1-8b-instruct-v6.1",
    ],
)
def test_v6_1_output_guard_rejects_reserved_or_outside_paths(path):
    with pytest.raises(ValueError, match="v6.1 output"):
        validate_v6_1_adapter_dir(path)


def test_v6_1_output_guard_accepts_only_the_two_new_destinations():
    assert validate_v6_1_adapter_dir("D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v6.1").name.endswith("-v6.1")
    assert validate_v6_1_adapter_dir("D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v6.1-smoke").name.endswith("-smoke")


def test_v6_1_source_guard_accepts_v6_only():
    assert validate_v6_1_source_adapter_dir("D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v6").name.endswith("-v6")
