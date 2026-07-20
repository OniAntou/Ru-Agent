import json

import torch

from mini_gpt.config import ModelConfig
from mini_gpt.evaluate import evaluate_language_losses
from mini_gpt.model import MiniGPT
from mini_gpt.tokenizer import load_tokenizer, train_tokenizer


def test_validation_loss_is_reported_by_language(tmp_path):
    text = tmp_path / "corpus.txt"
    text.write_text("Xin chào Việt Nam.\n\nHello English world.", encoding="utf-8")
    tokenizer_path = tmp_path / "tokenizer.json"
    train_tokenizer([text], tokenizer_path, vocab_size=300)
    validation = tmp_path / "val.jsonl"
    validation.write_text(
        "\n".join(
            [
                json.dumps({"text": "Xin chào Việt Nam.", "language": "vi"}, ensure_ascii=False),
                json.dumps({"text": "Hello English world.", "language": "en"}, ensure_ascii=False),
            ]
        ),
        encoding="utf-8",
    )
    tokenizer = load_tokenizer(tokenizer_path)
    model = MiniGPT(ModelConfig.tiny(tokenizer.get_vocab_size()))

    losses = evaluate_language_losses(model, tokenizer, validation, torch.device("cpu"))

    assert set(losses) == {"en", "vi"}
    assert all(torch.isfinite(torch.tensor(value)) for value in losses.values())

