from __future__ import annotations

import json
from dataclasses import asdict, dataclass
from pathlib import Path
from random import Random

from .data import normalize_text


@dataclass(frozen=True)
class ChatCorpusReport:
    examples_before_deduplication: int
    examples_after_deduplication: int
    train_examples: int
    validation_examples: int


def format_chat_example(prompt: str, response: str) -> str:
    return f"User: {prompt}\nAssistant: {response}"


def _read_examples(source_path: Path) -> list[tuple[str, str]]:
    if not source_path.is_file():
        raise FileNotFoundError(f"approved conversation file not found: {source_path}")
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
            clean_prompt = normalize_text(prompt)
            clean_response = normalize_text(response)
            if not clean_prompt:
                raise ValueError(f"line {line_number} prompt cannot be empty")
            if not clean_response:
                raise ValueError(f"line {line_number} response cannot be empty")
            examples.append((clean_prompt, clean_response))
    return examples


def _write_examples(path: Path, examples: list[tuple[str, str]]) -> None:
    path.write_text(
        "\n\n".join(format_chat_example(prompt, response) for prompt, response in examples) + "\n",
        encoding="utf-8",
    )


def prepare_chat_corpus(
    source_path: str | Path,
    output_dir: str | Path,
    validation_ratio: float = 0.1,
    seed: int = 42,
) -> ChatCorpusReport:
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
        raise ValueError("at least two unique approved chat examples are required")

    Random(seed).shuffle(unique_examples)
    validation_count = max(1, int(round(len(unique_examples) * validation_ratio)))
    validation_count = min(validation_count, len(unique_examples) - 1)
    validation = unique_examples[:validation_count]
    train = unique_examples[validation_count:]

    destination = Path(output_dir)
    destination.mkdir(parents=True, exist_ok=True)
    _write_examples(destination / "train.txt", train)
    _write_examples(destination / "val.txt", validation)
    report = ChatCorpusReport(
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
