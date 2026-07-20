from __future__ import annotations

import json
from collections import defaultdict
from pathlib import Path
from typing import Any

import torch
from tokenizers import Tokenizer

from .model import MiniGPT


@torch.no_grad()
def evaluate_language_losses(
    model: MiniGPT,
    tokenizer: Tokenizer,
    labeled_validation_path: str | Path,
    device: torch.device,
    max_documents: int = 100,
) -> dict[str, float]:
    path = Path(labeled_validation_path)
    if not path.is_file():
        raise FileNotFoundError(f"labeled validation file not found: {path}")
    bos_id = tokenizer.token_to_id("<bos>")
    eos_id = tokenizer.token_to_id("<eos>")
    if bos_id is None or eos_id is None:
        raise ValueError("tokenizer must define <bos> and <eos>")
    totals: dict[str, float] = defaultdict(float)
    counts: dict[str, int] = defaultdict(int)
    model.eval()
    for index, line in enumerate(path.read_text(encoding="utf-8").splitlines()):
        if index >= max_documents:
            break
        record = json.loads(line)
        language = str(record.get("language", "unknown"))
        token_ids = [bos_id, *tokenizer.encode(str(record["text"])).ids, eos_id]
        for start in range(0, len(token_ids) - 1, model.config.context_length):
            window = token_ids[start : start + model.config.context_length + 1]
            if len(window) < 2:
                continue
            inputs = torch.tensor([window[:-1]], dtype=torch.long, device=device)
            targets = torch.tensor([window[1:]], dtype=torch.long, device=device)
            _, loss = model(inputs, targets)
            if loss is not None:
                totals[language] += float(loss.item())
                counts[language] += 1
    return {language: totals[language] / counts[language] for language in sorted(counts)}


@torch.no_grad()
def _sample_tokens(
    model: MiniGPT,
    tokenizer: Tokenizer,
    prompt: str,
    device: torch.device,
    max_new_tokens: int = 50,
    temperature: float = 0.8,
    top_k: int = 50,
) -> tuple[torch.Tensor, int]:
    if temperature <= 0:
        raise ValueError("temperature must be positive")
    input_ids = tokenizer.encode(prompt).ids
    bos_id = tokenizer.token_to_id("<bos>")
    eos_id = tokenizer.token_to_id("<eos>")
    if bos_id is not None:
        input_ids = [bos_id, *input_ids]
    generated = torch.tensor([input_ids], dtype=torch.long, device=device)
    input_length = generated.size(1)
    model.eval()
    for _ in range(max_new_tokens):
        context = generated[:, -model.config.context_length :]
        logits, _ = model(context)
        next_logits = logits[:, -1, :] / temperature
        if top_k > 0:
            values, indices = torch.topk(next_logits, min(top_k, next_logits.size(-1)))
            filtered = torch.full_like(next_logits, float("-inf"))
            filtered.scatter_(1, indices, values)
            next_logits = filtered
        probabilities = torch.softmax(next_logits, dim=-1)
        next_token = torch.multinomial(probabilities, num_samples=1)
        generated = torch.cat([generated, next_token], dim=1)
        if eos_id is not None and int(next_token.item()) == eos_id:
            break
    return generated, input_length


@torch.no_grad()
def generate(
    model: MiniGPT,
    tokenizer: Tokenizer,
    prompt: str,
    device: torch.device,
    max_new_tokens: int = 50,
    temperature: float = 0.8,
    top_k: int = 50,
) -> str:
    generated, _ = _sample_tokens(
        model,
        tokenizer,
        prompt,
        device,
        max_new_tokens=max_new_tokens,
        temperature=temperature,
        top_k=top_k,
    )
    return tokenizer.decode(generated[0].tolist(), skip_special_tokens=True)


@torch.no_grad()
def generate_completion(
    model: MiniGPT,
    tokenizer: Tokenizer,
    prompt: str,
    device: torch.device,
    max_new_tokens: int = 50,
    temperature: float = 0.8,
    top_k: int = 50,
) -> str:
    generated, input_length = _sample_tokens(
        model,
        tokenizer,
        prompt,
        device,
        max_new_tokens=max_new_tokens,
        temperature=temperature,
        top_k=top_k,
    )
    return tokenizer.decode(generated[0, input_length:].tolist(), skip_special_tokens=True)


def checkpoint_model_config(payload: dict[str, Any]) -> dict[str, Any]:
    if "model_config" not in payload:
        raise ValueError("checkpoint does not contain model_config")
    return payload["model_config"]
