from __future__ import annotations

import json
from dataclasses import asdict, dataclass
from pathlib import Path
from random import Random

import numpy as np
import torch
from torch.utils.data import Dataset

from .data import normalize_text
from .tokenizer import load_tokenizer

IGNORE_LABEL = -100


@dataclass(frozen=True)
class ResponseChatCorpusReport:
    examples_before_deduplication: int
    examples_after_deduplication: int
    train_examples: int
    validation_examples: int


def _read_examples(source_path: Path) -> list[tuple[str, str]]:
    if not source_path.is_file():
        raise FileNotFoundError(f"conversation file not found: {source_path}")

    examples: list[tuple[str, str]] = []
    with source_path.open("r", encoding="utf-8") as handle:
        for line_number, line in enumerate(handle, start=1):
            if not line.strip():
                continue
            try:
                record = json.loads(line)
            except json.JSONDecodeError as error:
                raise ValueError(f"invalid JSON on line {line_number}") from error
            if not isinstance(record, dict):
                raise ValueError(f"line {line_number} must contain a JSON object")
            prompt = record.get("prompt")
            response = record.get("response")
            if not isinstance(prompt, str):
                raise ValueError(f"line {line_number} prompt must be a string")
            if not isinstance(response, str):
                raise ValueError(f"line {line_number} response must be a string")
            prompt = normalize_text(prompt)
            response = normalize_text(response)
            if not prompt:
                raise ValueError(f"line {line_number} prompt cannot be empty")
            if not response:
                raise ValueError(f"line {line_number} response cannot be empty")
            examples.append((prompt, response))
    return examples


def _write_examples(path: Path, examples: list[tuple[str, str]]) -> None:
    path.write_text(
        "\n".join(
            json.dumps({"prompt": prompt, "response": response}, ensure_ascii=False)
            for prompt, response in examples
        )
        + "\n",
        encoding="utf-8",
    )


def prepare_response_chat_corpus(
    source_path: str | Path,
    output_dir: str | Path,
    validation_ratio: float = 0.1,
    seed: int = 42,
) -> ResponseChatCorpusReport:
    if not 0 < validation_ratio < 1:
        raise ValueError("validation_ratio must be between 0 and 1")

    raw_examples = _read_examples(Path(source_path))
    unique_examples: list[tuple[str, str]] = []
    seen: set[tuple[str, str]] = set()
    for example in raw_examples:
        if example not in seen:
            seen.add(example)
            unique_examples.append(example)
    if len(unique_examples) < 2:
        raise ValueError("at least two unique chat examples are required")

    Random(seed).shuffle(unique_examples)
    validation_count = max(1, int(round(len(unique_examples) * validation_ratio)))
    validation_count = min(validation_count, len(unique_examples) - 1)
    validation = unique_examples[:validation_count]
    train = unique_examples[validation_count:]

    destination = Path(output_dir)
    destination.mkdir(parents=True, exist_ok=True)
    _write_examples(destination / "train.jsonl", train)
    _write_examples(destination / "val.jsonl", validation)
    report = ResponseChatCorpusReport(
        examples_before_deduplication=len(raw_examples),
        examples_after_deduplication=len(unique_examples),
        train_examples=len(train),
        validation_examples=len(validation),
    )
    (destination / "report.json").write_text(
        json.dumps(asdict(report), ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    return report


def encode_response_chat_split(
    examples_path: str | Path,
    tokenizer_path: str | Path,
    token_output_path: str | Path,
    label_output_path: str | Path,
) -> int:
    tokenizer = load_tokenizer(tokenizer_path)
    bos_id = tokenizer.token_to_id("<bos>")
    eos_id = tokenizer.token_to_id("<eos>")
    if bos_id is None or eos_id is None:
        raise ValueError("tokenizer must define <bos> and <eos>")

    token_ids: list[int] = []
    labels: list[int] = []
    for prompt, response in _read_examples(Path(examples_path)):
        prefix = [bos_id, *tokenizer.encode(f"User: {prompt}\nAssistant:").ids]
        completion = [*tokenizer.encode(response).ids, eos_id]
        token_ids.extend([*prefix, *completion])
        labels.extend([IGNORE_LABEL] * len(prefix))
        labels.extend(completion)

    if len(token_ids) < 2:
        raise ValueError("encoded split contains fewer than two tokens")
    if len(token_ids) != len(labels):
        raise RuntimeError("response supervision tokens and labels are misaligned")

    token_destination = Path(token_output_path)
    label_destination = Path(label_output_path)
    token_destination.parent.mkdir(parents=True, exist_ok=True)
    label_destination.parent.mkdir(parents=True, exist_ok=True)
    np.asarray(token_ids, dtype=np.int32).tofile(token_destination)
    np.asarray(labels, dtype=np.int32).tofile(label_destination)
    return len(token_ids)


class SupervisedTokenWindowDataset(Dataset[tuple[torch.Tensor, torch.Tensor]]):
    """Autoregressive windows with at least one assistant token to score."""

    def __init__(self, token_path: str | Path, label_path: str | Path, context_length: int) -> None:
        if context_length <= 0:
            raise ValueError("context_length must be positive")
        self.tokens = np.memmap(token_path, dtype=np.int32, mode="r")
        self.labels = np.memmap(label_path, dtype=np.int32, mode="r")
        if len(self.tokens) != len(self.labels):
            raise ValueError("token and label files must have the same length")
        if len(self.tokens) <= context_length:
            raise ValueError("token file must contain more tokens than context_length")

        starts = np.arange(len(self.tokens) - context_length)
        supervised = np.asarray(self.labels != IGNORE_LABEL, dtype=np.int64)
        cumulative = np.concatenate((np.zeros(1, dtype=np.int64), np.cumsum(supervised)))
        counts = cumulative[starts + context_length + 1] - cumulative[starts + 1]
        self.starts = starts[counts > 0]
        if len(self.starts) == 0:
            raise ValueError("no token windows contain response labels")
        self.context_length = context_length

    def __len__(self) -> int:
        return len(self.starts)

    def __getitem__(self, index: int) -> tuple[torch.Tensor, torch.Tensor]:
        start = int(self.starts[index])
        tokens = np.asarray(self.tokens[start : start + self.context_length], dtype=np.int64)
        labels = np.asarray(self.labels[start + 1 : start + self.context_length + 1], dtype=np.int64)
        return torch.from_numpy(tokens), torch.from_numpy(labels)
