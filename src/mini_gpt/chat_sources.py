from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterator

from .data import normalize_text

ULTRACHAT_SOURCE = "HuggingFaceH4/ultrachat_200k"
ULTRACHAT_LICENSE = "MIT"
OASST2_SOURCE = "OpenAssistant/oasst2"
OASST2_LICENSE = "Apache-2.0"
VIETNAMESE_SOURCE = "hoanghai2110/vietnamese-dataset"
VIETNAMESE_LICENSE = "Apache-2.0"
MULTI_TURN_SOURCE = "5CD-AI/Vietnamese-Multi-turn-Chat-Alpaca"
MULTI_TURN_LICENSE = "Apache-2.0"


@dataclass(frozen=True)
class ChatSeedRecord:
    prompt: str
    response: str
    language: str
    source: str
    license: str


def _message_list(record: dict[str, Any]) -> list[dict[str, Any]]:
    messages = record.get("messages")
    if not isinstance(messages, list):
        raise ValueError("source record field 'messages' must be a list")
    if not all(isinstance(message, dict) for message in messages):
        raise ValueError("source record field 'messages' must contain objects")
    return messages


def _message_text(message: dict[str, Any], field: str) -> str:
    value = message.get(field)
    if not isinstance(value, str):
        raise ValueError(f"source message field '{field}' must be a string")
    return normalize_text(value)


def quality_rejection_reason(prompt: str, response: str, max_characters: int = 800) -> str | None:
    combined = f"{prompt}\n{response}"
    if len(prompt) < 1 or len(response) < 2:
        return "too_short"
    if len(prompt) + len(response) > max_characters:
        return "too_long"
    if "```" in combined or re.search(r"<[^>]{1,80}>", combined):
        return "code_or_markup"
    if re.search(r"\b(roleplay|as an ai language model)\b|nhập vai|đóng vai", combined, re.IGNORECASE):
        return "roleplay"
    if re.search(r"https?://|www\.", combined, re.IGNORECASE):
        return "link"
    return None


def extract_ultrachat_pairs(record: dict[str, Any]) -> Iterator[ChatSeedRecord]:
    messages = _message_list(record)
    for user, assistant in zip(messages, messages[1:]):
        if user["role"] != "user" or assistant["role"] != "assistant":
            continue
        yield ChatSeedRecord(
            prompt=_message_text(user, "content"),
            response=_message_text(assistant, "content"),
            language="en",
            source=ULTRACHAT_SOURCE,
            license=ULTRACHAT_LICENSE,
        )


def extract_vietnamese_pairs(record: dict[str, Any]) -> Iterator[ChatSeedRecord]:
    messages = _message_list(record)
    for user, assistant in zip(messages, messages[1:]):
        if user["role"] != "user" or assistant["role"] != "assistant":
            continue
        yield ChatSeedRecord(
            prompt=_message_text(user, "content"),
            response=_message_text(assistant, "content"),
            language="vi",
            source=VIETNAMESE_SOURCE,
            license=VIETNAMESE_LICENSE,
        )


def extract_5cd_pairs(record: dict[str, Any]) -> Iterator[ChatSeedRecord]:
    """Yield only adjacent human-to-gpt turns from the 5CD format."""
    conversations = record.get("conversations")
    if not isinstance(conversations, list):
        raise ValueError("5CD record is missing a conversations list")

    for user, assistant in zip(conversations, conversations[1:]):
        if not isinstance(user, dict) or not isinstance(assistant, dict):
            continue
        if user.get("from") != "human" or assistant.get("from") != "gpt":
            continue

        prompt = _message_text(user, "value")
        response = _message_text(assistant, "value")
        combined = f"{prompt}{response}"
        language = "vi" if any(char in combined for char in "ăâđêôơưĂÂĐÊÔƠƯ") else "en"
        yield ChatSeedRecord(
            prompt=prompt,
            response=response,
            language=language,
            source=MULTI_TURN_SOURCE,
            license=MULTI_TURN_LICENSE,
        )


def extract_oasst2_pairs(records: Iterator[dict[str, Any]] | list[dict[str, Any]]) -> Iterator[ChatSeedRecord]:
    prompts: dict[str, dict[str, Any]] = {}
    pending_assistants: dict[str, list[dict[str, Any]]] = {}

    def pair(prompt: dict[str, Any], assistant: dict[str, Any]) -> ChatSeedRecord | None:
        if (
            prompt.get("lang") not in {"en", "vi"}
            or prompt.get("lang") != assistant.get("lang")
            or assistant.get("deleted") is not False
            or assistant.get("review_result") is not True
            or assistant.get("synthetic") is not False
        ):
            return None
        return ChatSeedRecord(
            prompt=_message_text(prompt, "text"),
            response=_message_text(assistant, "text"),
            language=prompt["lang"],
            source=OASST2_SOURCE,
            license=OASST2_LICENSE,
        )

    for record in records:
        if record.get("role") == "prompter":
            message_id = record["message_id"]
            prompts[message_id] = record
            for assistant in pending_assistants.pop(message_id, []):
                candidate = pair(record, assistant)
                if candidate is not None:
                    yield candidate
        elif record.get("role") == "assistant":
            parent_id = record.get("parent_id")
            prompt = prompts.get(parent_id)
            if prompt is None:
                if isinstance(parent_id, str):
                    pending_assistants.setdefault(parent_id, []).append(record)
                continue
            candidate = pair(prompt, record)
            if candidate is not None:
                yield candidate


def download_open_chat_seed(
    output_path: str | Path,
    manifest_path: str | Path,
    cache_dir: str | Path,
    max_ultrachat_examples: int = 2_000,
    max_oasst_examples: int = 400,
    max_vietnamese_examples: int = 1_500,
) -> dict[str, Any]:
    quotas = {
        ULTRACHAT_SOURCE: max_ultrachat_examples,
        OASST2_SOURCE: max_oasst_examples,
        VIETNAMESE_SOURCE: max_vietnamese_examples,
    }
    if any(quota <= 0 for quota in quotas.values()):
        raise ValueError("all source quotas must be positive")

    output = Path(output_path)
    manifest_file = Path(manifest_path)
    cache = Path(cache_dir)
    output.parent.mkdir(parents=True, exist_ok=True)
    manifest_file.parent.mkdir(parents=True, exist_ok=True)
    cache.mkdir(parents=True, exist_ok=True)
    os.environ["HF_HOME"] = str(cache)
    os.environ["HF_DATASETS_CACHE"] = str(cache / "datasets")
    os.environ["HF_HUB_CACHE"] = str(cache / "hub")

    try:
        from datasets import load_dataset
    except ImportError as error:
        raise RuntimeError("install the data extra with: python -m pip install datasets") from error

    output_partial = output.with_suffix(f"{output.suffix}.partial")
    manifest_partial = manifest_file.with_suffix(f"{manifest_file.suffix}.partial")
    source_specs = (
        (ULTRACHAT_SOURCE, "train_sft", extract_ultrachat_pairs),
        (OASST2_SOURCE, "train", extract_oasst2_pairs),
        (VIETNAMESE_SOURCE, "train", extract_vietnamese_pairs),
    )
    source_report: dict[str, dict[str, Any]] = {}
    seen: set[tuple[str, str]] = set()
    total_examples = 0

    try:
        with output_partial.open("w", encoding="utf-8", newline="\n") as handle:
            for source, split, extractor in source_specs:
                stream = load_dataset(source, split=split, streaming=True)
                scanned = 0

                def counted_stream():
                    nonlocal scanned
                    for item in stream:
                        scanned += 1
                        yield item

                if source == OASST2_SOURCE:
                    candidates = extractor(counted_stream())
                else:
                    candidates = (
                        pair
                        for item in counted_stream()
                        for pair in extractor(item)
                    )

                accepted = 0
                rejected: dict[str, int] = {}
                quota = quotas[source]
                for candidate in candidates:
                    reason = quality_rejection_reason(candidate.prompt, candidate.response)
                    if reason is not None:
                        rejected[reason] = rejected.get(reason, 0) + 1
                        continue
                    key = (candidate.prompt, candidate.response)
                    if key in seen:
                        rejected["duplicate"] = rejected.get("duplicate", 0) + 1
                        continue
                    seen.add(key)
                    handle.write(json.dumps(candidate.__dict__, ensure_ascii=False) + "\n")
                    accepted += 1
                    total_examples += 1
                    if accepted == quota:
                        break
                if accepted != quota:
                    raise ValueError(f"{source} yielded {accepted} acceptable examples; expected {quota}")
                source_report[source] = {
                    "split": split,
                    "license": ULTRACHAT_LICENSE if source == ULTRACHAT_SOURCE else OASST2_LICENSE if source == OASST2_SOURCE else VIETNAMESE_LICENSE,
                    "scanned": scanned,
                    "accepted": accepted,
                    "rejected": rejected,
                }

        manifest = {
            "generated_at_utc": datetime.now(timezone.utc).isoformat(),
            "cache_dir": str(cache),
            "limits": {
                "max_ultrachat_examples": max_ultrachat_examples,
                "max_oasst_examples": max_oasst_examples,
                "max_vietnamese_examples": max_vietnamese_examples,
            },
            "total_examples": total_examples,
            "sources": source_report,
        }
        manifest_partial.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        os.replace(output_partial, output)
        os.replace(manifest_partial, manifest_file)
        return manifest
    except Exception:
        output_partial.unlink(missing_ok=True)
        manifest_partial.unlink(missing_ok=True)
        raise


def download_response_chat_seed(
    output_path: str | Path,
    manifest_path: str | Path,
    cache_dir: str | Path,
    max_5cd_examples: int = 18_000,
    max_vietnamese_examples: int = 1_797,
    max_oasst_examples: int = 2_000,
    max_ultrachat_examples: int = 6_000,
) -> dict[str, Any]:
    """Create the larger V2 response-only chat seed without modifying V1 data."""
    quotas = {
        MULTI_TURN_SOURCE: max_5cd_examples,
        VIETNAMESE_SOURCE: max_vietnamese_examples,
        OASST2_SOURCE: max_oasst_examples,
        ULTRACHAT_SOURCE: max_ultrachat_examples,
    }
    if any(quota <= 0 for quota in quotas.values()):
        raise ValueError("all source quotas must be positive")

    output = Path(output_path)
    manifest_file = Path(manifest_path)
    cache = Path(cache_dir)
    output.parent.mkdir(parents=True, exist_ok=True)
    manifest_file.parent.mkdir(parents=True, exist_ok=True)
    cache.mkdir(parents=True, exist_ok=True)
    os.environ["HF_HOME"] = str(cache)
    os.environ["HF_DATASETS_CACHE"] = str(cache / "datasets")
    os.environ["HF_HUB_CACHE"] = str(cache / "hub")

    try:
        from datasets import load_dataset
    except ImportError as error:
        raise RuntimeError("install the data extra with: python -m pip install datasets") from error

    output_partial = output.with_suffix(f"{output.suffix}.partial")
    manifest_partial = manifest_file.with_suffix(f"{manifest_file.suffix}.partial")
    source_specs = (
        (MULTI_TURN_SOURCE, "train", extract_5cd_pairs),
        (VIETNAMESE_SOURCE, "train", extract_vietnamese_pairs),
        (OASST2_SOURCE, "train", extract_oasst2_pairs),
        (ULTRACHAT_SOURCE, "train_sft", extract_ultrachat_pairs),
    )
    source_report: dict[str, dict[str, Any]] = {}
    seen: set[tuple[str, str]] = set()
    total_examples = 0

    try:
        with output_partial.open("w", encoding="utf-8", newline="\n") as handle:
            for source, split, extractor in source_specs:
                stream = load_dataset(source, split=split, streaming=True)
                scanned = 0

                def counted_stream() -> Iterator[dict[str, Any]]:
                    nonlocal scanned
                    for item in stream:
                        scanned += 1
                        yield item

                if source == OASST2_SOURCE:
                    candidates = extractor(counted_stream())
                else:
                    candidates = (pair for item in counted_stream() for pair in extractor(item))

                accepted = 0
                rejected: dict[str, int] = {}
                for candidate in candidates:
                    reason = quality_rejection_reason(candidate.prompt, candidate.response)
                    if reason is not None:
                        rejected[reason] = rejected.get(reason, 0) + 1
                        continue
                    key = (candidate.prompt, candidate.response)
                    if key in seen:
                        rejected["duplicate"] = rejected.get("duplicate", 0) + 1
                        continue
                    seen.add(key)
                    handle.write(json.dumps(candidate.__dict__, ensure_ascii=False) + "\n")
                    accepted += 1
                    total_examples += 1
                    if accepted == quotas[source]:
                        break

                if accepted != quotas[source]:
                    raise ValueError(f"{source} yielded {accepted} acceptable examples; expected {quotas[source]}")
                source_report[source] = {
                    "split": split,
                    "license": {
                        MULTI_TURN_SOURCE: MULTI_TURN_LICENSE,
                        VIETNAMESE_SOURCE: VIETNAMESE_LICENSE,
                        OASST2_SOURCE: OASST2_LICENSE,
                        ULTRACHAT_SOURCE: ULTRACHAT_LICENSE,
                    }[source],
                    "scanned": scanned,
                    "accepted": accepted,
                    "rejected": rejected,
                }

        manifest = {
            "generated_at_utc": datetime.now(timezone.utc).isoformat(),
            "cache_dir": str(cache),
            "limits": {
                "max_5cd_examples": max_5cd_examples,
                "max_vietnamese_examples": max_vietnamese_examples,
                "max_oasst_examples": max_oasst_examples,
                "max_ultrachat_examples": max_ultrachat_examples,
            },
            "total_examples": total_examples,
            "sources": source_report,
        }
        manifest_partial.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        os.replace(output_partial, output)
        os.replace(manifest_partial, manifest_file)
        return manifest
    except Exception:
        output_partial.unlink(missing_ok=True)
        manifest_partial.unlink(missing_ok=True)
        raise
