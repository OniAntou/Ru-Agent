import json
from pathlib import Path

import pytest

from mini_gpt.config import ModelConfig, TrainingConfig


def test_tiny_model_config_is_valid():
    config = ModelConfig.tiny(vocab_size=300)
    assert config.d_model % config.n_heads == 0


def test_model_config_rejects_invalid_dimensions():
    with pytest.raises(ValueError, match="divisible"):
        ModelConfig(vocab_size=300, d_model=127, n_heads=4)
    with pytest.raises(ValueError, match="positive"):
        ModelConfig(vocab_size=0)


def test_training_config_rejects_unknown_device():
    with pytest.raises(ValueError, match="device"):
        TrainingConfig(device="metal")


def test_response_only_chat_v2_config_uses_conservative_fine_tuning_settings():
    config_path = Path(__file__).resolve().parents[1] / "configs" / "chat_response_v2.json"
    values = json.loads(config_path.read_text(encoding="utf-8"))

    assert values["training"]["learning_rate"] == 0.00002
    assert values["training"]["total_steps"] == 1200
    assert values["training"]["gradient_accumulation_steps"] == 16
    assert values["training"]["validation_interval"] == 50
    assert values["training"]["checkpoint_interval"] == 50
