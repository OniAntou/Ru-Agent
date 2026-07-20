from __future__ import annotations

import hashlib
import json
import unicodedata
from dataclasses import asdict, dataclass
from pathlib import Path
from random import Random

import numpy as np
import torch
from torch.utils.data import Dataset

from .tokenizer import load_tokenizer


@dataclass
class CorpusReport:
    source_files: int
    documents_before_deduplication: int
    documents_after_deduplication: int
    train_documents: int
    validation_documents: int
    language_characters: dict[str, int]


def normalize_text(text: str) -> str:
    return unicodedata.normalize("NFC", text.replace("\r\n", "\n").replace("\r", "\n")).strip()


def _documents_from_file(path: Path) -> list[tuple[str, str]]:
    content = normalize_text(path.read_text(encoding="utf-8"))
    documents: list[tuple[str, str]] = []
    for block in content.split("\n\n"):
        block = normalize_text(block)
        if not block:
            continue
        language = "unknown"
        if block.startswith("VI:"):
            language, block = "vi", normalize_text(block[3:])
        elif block.startswith("EN:"):
            language, block = "en", normalize_text(block[3:])
        if block:
            documents.append((block, language))
    return documents


def _write_documents(path: Path, documents: list[tuple[str, str]]) -> None:
    path.write_text("\n\n".join(text for text, _ in documents) + "\n", encoding="utf-8")


def _write_labeled_documents(path: Path, documents: list[tuple[str, str]]) -> None:
    records = [{"text": text, "language": language} for text, language in documents]
    path.write_text(
        "\n".join(json.dumps(record, ensure_ascii=False) for record in records) + "\n",
        encoding="utf-8",
    )


def prepare_corpus(
    raw_dir: str | Path,
    processed_dir: str | Path,
    validation_ratio: float = 0.1,
    seed: int = 42,
) -> CorpusReport:
    if not 0 < validation_ratio < 1:
        raise ValueError("validation_ratio must be between 0 and 1")
    raw_path = Path(raw_dir)
    output_path = Path(processed_dir)
    files = sorted(raw_path.glob("*.txt"))
    if not files:
        raise ValueError(f"no .txt files found in {raw_path}")

    documents: list[tuple[str, str]] = []
    for file_path in files:
        documents.extend(_documents_from_file(file_path))
    before = len(documents)
    unique: list[tuple[str, str]] = []
    seen: set[str] = set()
    for text, language in documents:
        digest = hashlib.sha256(text.encode("utf-8")).hexdigest()
        if digest not in seen:
            seen.add(digest)
            unique.append((text, language))
    if len(unique) < 2:
        raise ValueError("at least two unique documents are required")

    Random(seed).shuffle(unique)
    validation_count = max(1, int(round(len(unique) * validation_ratio)))
    validation_count = min(validation_count, len(unique) - 1)
    remaining = list(unique)
    languages = sorted({language for _, language in unique if language != "unknown"})
    validation: list[tuple[str, str]] = []
    if len(languages) <= validation_count and len(unique) - len(languages) >= 1:
        for language in languages:
            selected = next(index for index, (_, item_language) in enumerate(remaining) if item_language == language)
            validation.append(remaining.pop(selected))
    additional_validation = validation_count - len(validation)
    validation.extend(remaining[:additional_validation])
    train = remaining[additional_validation:]
    output_path.mkdir(parents=True, exist_ok=True)
    _write_documents(output_path / "train.txt", train)
    _write_documents(output_path / "val.txt", validation)
    _write_labeled_documents(output_path / "train.jsonl", train)
    _write_labeled_documents(output_path / "val.jsonl", validation)
    language_characters: dict[str, int] = {}
    for text, language in unique:
        language_characters[language] = language_characters.get(language, 0) + len(text)
    report = CorpusReport(
        source_files=len(files),
        documents_before_deduplication=before,
        documents_after_deduplication=len(unique),
        train_documents=len(train),
        validation_documents=len(validation),
        language_characters=language_characters,
    )
    (output_path / "report.json").write_text(json.dumps(asdict(report), ensure_ascii=False, indent=2), encoding="utf-8")
    return report


def encode_split(text_path: str | Path, tokenizer_path: str | Path, output_path: str | Path) -> int:
    tokenizer = load_tokenizer(tokenizer_path)
    text = Path(text_path).read_text(encoding="utf-8")
    token_ids: list[int] = []
    bos_id = tokenizer.token_to_id("<bos>")
    eos_id = tokenizer.token_to_id("<eos>")
    if bos_id is None or eos_id is None:
        raise ValueError("tokenizer must define <bos> and <eos>")
    for document in text.split("\n\n"):
        document = normalize_text(document)
        if document:
            token_ids.extend([bos_id, *tokenizer.encode(document).ids, eos_id])
    if len(token_ids) < 2:
        raise ValueError("encoded split contains fewer than two tokens")
    destination = Path(output_path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    np.asarray(token_ids, dtype=np.int32).tofile(destination)
    return len(token_ids)


class TokenWindowDataset(Dataset[tuple[torch.Tensor, torch.Tensor]]):
    def __init__(self, token_path: str | Path, context_length: int) -> None:
        if context_length <= 0:
            raise ValueError("context_length must be positive")
        self.tokens = np.memmap(token_path, dtype=np.int32, mode="r")
        self.context_length = context_length
        if len(self.tokens) <= context_length:
            raise ValueError("token file must contain more tokens than context_length")

    def __len__(self) -> int:
        return len(self.tokens) - self.context_length

    def __getitem__(self, index: int) -> tuple[torch.Tensor, torch.Tensor]:
        window = np.asarray(self.tokens[index : index + self.context_length + 1], dtype=np.int64)
        return torch.from_numpy(window[:-1]), torch.from_numpy(window[1:])
