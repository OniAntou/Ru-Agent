from __future__ import annotations

import json
from pathlib import Path

import pytest

from mini_gpt.llama3_1_training import validate_v7_1_adapter_dir


CONFIG_PATH = Path(__file__).resolve().parents[1] / "configs" / "ru_llama3_1_8b_v7_1.json"


def test_v7_1_config_is_separate_and_uses_conservative_training_values():
    config = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))

    assert config["dataset_dir"].endswith("data/conversations/ru_v7_1")
    assert config["adapter_dir"].endswith("checkpoints/ru-llama3.1-8b-instruct-v7.1")
    assert config["adapter_dir"] not in {
        "D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v6",
        "D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v7",
    }
    assert config["epochs"] == 2
    assert config["learning_rate"] == 3e-5
    assert config["max_length"] == 384
    assert config["gradient_accumulation_steps"] == 16
    assert config["physical_vram_cap_mib"] == 6144
    assert config["profile_name"] == "ru_llama3_1_v7_1"


@pytest.mark.parametrize(
    "path",
    [
        "D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v6",
        "D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v7",
    ],
)
def test_v7_1_output_guard_rejects_comparison_adapters(path):
    with pytest.raises(ValueError, match="v7.1 output"):
        validate_v7_1_adapter_dir(path)
