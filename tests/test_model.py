import torch

from mini_gpt.config import ModelConfig
from mini_gpt.model import MiniGPT


def test_model_shapes_and_loss():
    config = ModelConfig.tiny(vocab_size=300)
    model = MiniGPT(config)
    inputs = torch.randint(0, config.vocab_size, (2, config.context_length))
    logits, loss = model(inputs, inputs)
    assert logits.shape == (2, config.context_length, config.vocab_size)
    assert loss is not None and torch.isfinite(loss)
    assert model.parameter_count() > 0


def test_model_rejects_sequence_longer_than_context():
    config = ModelConfig.tiny(vocab_size=300)
    model = MiniGPT(config)
    inputs = torch.zeros((1, config.context_length + 1), dtype=torch.long)
    try:
        model(inputs)
    except ValueError as error:
        assert "context_length" in str(error)
    else:
        raise AssertionError("expected context-length validation error")

