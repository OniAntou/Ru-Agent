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

