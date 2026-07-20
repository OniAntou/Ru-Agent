import json
from types import SimpleNamespace

import torch

from mini_gpt.config import ModelConfig
from mini_gpt.evaluate import evaluate_language_losses, generate_completion
from mini_gpt.model import MiniGPT
from mini_gpt.tokenizer import load_tokenizer, train_tokenizer


class FakeTokenizer:
    def encode(self, _prompt):
        return SimpleNamespace(ids=[2])

    def token_to_id(self, token):
        return {"<bos>": 1}.get(token)

    def decode(self, ids, skip_special_tokens=True):
        return "NEW" if ids == [7] else "PROMPT"


class FakeModel:
    config = SimpleNamespace(context_length=8)

    def eval(self):
        return self

    def __call__(self, context):
        logits = torch.full((1, context.size(1), 8), -100.0)
        logits[:, -1, 7] = 100.0
        return logits, None


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


def test_generate_completion_decodes_only_new_tokens():
    result = generate_completion(
        FakeModel(),
        FakeTokenizer(),
        "prompt",
        torch.device("cpu"),
        max_new_tokens=1,
        temperature=1.0,
        top_k=0,
    )

    assert result == "NEW"
