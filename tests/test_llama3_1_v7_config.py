from __future__ import annotations

import json
from pathlib import Path


CONFIG_PATH = Path(__file__).resolve().parents[1] / "configs" / "ru_llama3_1_8b_v7.json"


def test_v7_training_config_is_separate_and_conservative():
    config = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))

    assert config["dataset_dir"].endswith("data/conversations/ru_v7")
    assert config["adapter_dir"].endswith("checkpoints/ru-llama3.1-8b-instruct-v7")
    assert config["adapter_dir"] != "D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v6"
    assert config["epochs"] == 3
    assert config["learning_rate"] == 5e-5
    assert config["max_length"] == 384
    assert config["gradient_accumulation_steps"] == 16
    assert config["profile_name"] == "ru_llama3_1_v7"
