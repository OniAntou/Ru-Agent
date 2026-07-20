from __future__ import annotations

import json
import os
import unicodedata
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable

WIKIMEDIA_DATASET = "wikimedia/wikipedia"
WIKIMEDIA_LICENSE_URL = "https://dumps.wikimedia.org/legal.html"


def _normalize(value: str) -> str:
    return unicodedata.normalize("NFC", value.replace("\r\n", "\n").replace("\r", "\n")).strip()


def normalize_wikipedia_record(
    record: dict[str, Any],
    language: str,
    min_characters: int = 200,
    max_characters: int = 100_000,
) -> tuple[str, str, str] | None:
    title = _normalize(str(record.get("title", "")))
    text = _normalize(str(record.get("text", "")))
    if not title or not text or len(text) < min_characters or len(text) > max_characters:
        return None
    return language, title, text


def _write_document(handle: Any, language: str, title: str, text: str) -> None:
    normalized_text = "\n".join(
        line.strip()
        for line in text.replace("\r\n", "\n").replace("\r", "\n").splitlines()
        if line.strip()
    )
    handle.write(f"{language.upper()}: {title}\n{normalized_text}\n\n")


def download_wikipedia_sample(
    output_dir: str | Path,
    languages: Iterable[str] = ("en", "vi"),
    snapshot: str = "20231101",
    max_documents_per_language: int = 10_000,
    min_characters: int = 200,
    max_characters: int = 100_000,
    cache_dir: str | Path | None = None,
) -> dict[str, Any]:
    language_list = [language.strip().lower() for language in languages if language.strip()]
    if not language_list:
        raise ValueError("at least one language is required")
    if max_documents_per_language <= 0:
        raise ValueError("max_documents_per_language must be positive")
    if min_characters <= 0 or max_characters < min_characters:
        raise ValueError("character limits are invalid")

    destination = Path(output_dir)
    destination.mkdir(parents=True, exist_ok=True)
    resolved_cache = Path(cache_dir) if cache_dir is not None else destination.parent / ".hf"
    resolved_cache.mkdir(parents=True, exist_ok=True)
    os.environ["HF_HOME"] = str(resolved_cache)
    os.environ["HF_DATASETS_CACHE"] = str(resolved_cache / "datasets")
    os.environ["HF_HUB_CACHE"] = str(resolved_cache / "hub")

    try:
        from datasets import load_dataset
    except ImportError as error:
        raise RuntimeError("install the data extra with: python -m pip install datasets") from error

    manifest: dict[str, Any] = {
        "dataset": WIKIMEDIA_DATASET,
        "snapshot": snapshot,
        "license_reference": WIKIMEDIA_LICENSE_URL,
        "generated_at_utc": datetime.now(timezone.utc).isoformat(),
        "cache_dir": str(resolved_cache),
        "limits": {
            "max_documents_per_language": max_documents_per_language,
            "min_characters": min_characters,
            "max_characters": max_characters,
        },
        "languages": {},
    }

    for language in language_list:
        config_name = f"{snapshot}.{language}"
        stream = load_dataset(WIKIMEDIA_DATASET, config_name, split="train", streaming=True)
        final_path = destination / f"wikipedia_{language}.txt"
        temporary_path = final_path.with_suffix(".txt.partial")
        accepted = 0
        scanned = 0
        characters = 0
        with temporary_path.open("w", encoding="utf-8", newline="\n") as handle:
            for record in stream:
                scanned += 1
                normalized = normalize_wikipedia_record(record, language, min_characters, max_characters)
                if normalized is None:
                    continue
                _, title, text = normalized
                _write_document(handle, language, title, text)
                accepted += 1
                characters += len(title) + len(text)
                if accepted >= max_documents_per_language:
                    break
        os.replace(temporary_path, final_path)
        manifest["languages"][language] = {
            "config": config_name,
            "scanned": scanned,
            "accepted": accepted,
            "characters": characters,
            "output": str(final_path),
        }

    manifest_path = destination / "wikipedia_manifest.json"
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    return manifest
