from __future__ import annotations

import json
import os
import re
from collections import Counter
from datetime import datetime, timezone
from hashlib import sha256
from pathlib import Path
from random import Random
from typing import Any

from ._text_utils import normalize_text
from .ru_style import RU_SYSTEM_PROMPT

EXPECTED_EXAMPLES = 120
MINIMUM_CONTEXTUAL_EXAMPLES = 30
SOURCE = "ru_project_seed"
LICENSE = "project-owned"
MAX_PROMPT_CHARACTERS = 420
MAX_RESPONSE_CHARACTERS = 420
LIST_PATTERN = re.compile(r"(?m)^\s*(?:\d+\.|[-*])\s+")
AI_DISCLAIMER_PATTERN = re.compile(
    r"(?i)\b(as an ai language model|i cannot access|i do not have (?:the ability|a physical body)|i exist solely as)\b"
    r"|(?:tôi|mình) là một (?:mô hình|người mẫu) ngôn ngữ ai|với tư cách là",
)


def casual_rejection_reason(prompt: str, response: str) -> str | None:
    prompt = normalize_text(prompt)
    response = normalize_text(response)
    combined = f"{prompt}\n{response}"
    if not prompt or len(response) < 2:
        return "too_short"
    if len(prompt) > MAX_PROMPT_CHARACTERS or len(response) > MAX_RESPONSE_CHARACTERS:
        return "too_long"
    if "```" in combined or re.search(r"<[^>]{1,80}>", combined):
        return "code_or_markup"
    if re.search(r"https?://|www\.", combined, re.IGNORECASE):
        return "link"
    if LIST_PATTERN.search(response):
        return "list_heavy"
    if AI_DISCLAIMER_PATTERN.search(response):
        return "ai_disclaimer"
    return None


def _normalize_record(record: dict[str, object]) -> dict[str, object]:
    expected_fields = {"messages", "response", "language", "source", "license"}
    if set(record) != expected_fields:
        raise ValueError("authored record must contain exactly messages, response, language, source, and license")
    raw_messages = record["messages"]
    if not isinstance(raw_messages, list) or len(raw_messages) < 2:
        raise ValueError("authored record messages must include system and user turns")

    messages: list[dict[str, str]] = []
    for message in raw_messages:
        if not isinstance(message, dict) or set(message) != {"role", "content"}:
            raise ValueError("authored record messages must contain exactly role and content")
        role = message["role"]
        content = message["content"]
        if role not in {"system", "user", "assistant"} or not isinstance(content, str):
            raise ValueError("authored record messages must use supported roles and string content")
        content = normalize_text(content)
        if not content:
            raise ValueError("authored record messages cannot be empty")
        messages.append({"role": role, "content": content})
    if messages[0] != {"role": "system", "content": RU_SYSTEM_PROMPT}:
        raise ValueError("authored record must start with the Ru system prompt")
    turn_roles = [message["role"] for message in messages[1:]]
    if turn_roles[-1] != "user" or turn_roles[0] != "user":
        raise ValueError("authored record turns must start and end with user")
    if any(left == right for left, right in zip(turn_roles, turn_roles[1:])):
        raise ValueError("authored record user and assistant turns must alternate")

    response = record["response"]
    language = record["language"]
    source = record["source"]
    license_name = record["license"]
    if not all(isinstance(value, str) for value in (response, language, source, license_name)):
        raise ValueError("authored record response and provenance fields must be strings")
    response = normalize_text(response)
    if language not in {"vi", "en"}:
        raise ValueError("authored record language must be vi or en")
    if source != SOURCE or license_name != LICENSE:
        raise ValueError("authored record provenance must be project-owned")
    reason = casual_rejection_reason(messages[-1]["content"], response)
    if reason is not None:
        raise ValueError(f"authored record failed quality gate: {reason}")
    return {
        "messages": messages,
        "response": response,
        "language": language,
        "source": source,
        "license": license_name,
    }


def _canonical_source_sha256(records: list[dict[str, object]]) -> str:
    serialized = "\n".join(
        json.dumps(record, ensure_ascii=False, sort_keys=True, separators=(",", ":")) for record in records
    )
    return sha256(serialized.encode("utf-8")).hexdigest()


def _is_contextual(record: dict[str, object]) -> bool:
    messages = record["messages"]
    assert isinstance(messages, list)
    return any(message["role"] == "assistant" for message in messages[:-1])


def _write_jsonl(path: Path, records: list[dict[str, object]]) -> None:
    path.write_text("\n".join(json.dumps(record, ensure_ascii=False) for record in records) + "\n", encoding="utf-8")


def build_ru_qwen_corpus(
    records: list[dict[str, object]],
    output_dir: str | Path,
    validation_ratio: float = 0.1,
    seed: int = 42,
    minimum_examples: int = EXPECTED_EXAMPLES,
    minimum_contextual_examples: int = MINIMUM_CONTEXTUAL_EXAMPLES,
) -> dict[str, object]:
    if not 0 < validation_ratio < 1:
        raise ValueError("validation_ratio must be between 0 and 1")
    if minimum_examples != EXPECTED_EXAMPLES:
        raise ValueError(f"minimum_examples must be exactly {EXPECTED_EXAMPLES}")
    if minimum_contextual_examples < MINIMUM_CONTEXTUAL_EXAMPLES:
        raise ValueError(f"minimum_contextual_examples must be at least {MINIMUM_CONTEXTUAL_EXAMPLES}")

    normalized_records = [_normalize_record(record) for record in records]
    source_sha256 = _canonical_source_sha256(normalized_records)
    accepted: list[dict[str, object]] = []
    seen: set[str] = set()
    for record in normalized_records:
        key = json.dumps(
            {"messages": record["messages"], "response": record["response"]},
            ensure_ascii=False,
            sort_keys=True,
            separators=(",", ":"),
        )
        if key not in seen:
            seen.add(key)
            accepted.append(record)
    if len(accepted) != minimum_examples:
        raise ValueError(f"expected exactly {minimum_examples} accepted authored records, found {len(accepted)}")
    if {record["language"] for record in accepted} != {"vi", "en"}:
        raise ValueError("authored corpus must contain both vi and en records")
    contextual_examples = sum(_is_contextual(record) for record in accepted)
    if contextual_examples < minimum_contextual_examples:
        raise ValueError(
            f"expected at least {minimum_contextual_examples} contextual authored records, found {contextual_examples}"
        )

    shuffled = [*accepted]
    Random(seed).shuffle(shuffled)
    validation_count = max(1, round(len(shuffled) * validation_ratio))
    validation_count = min(validation_count, len(shuffled) - 1)
    validation = shuffled[:validation_count]
    train = shuffled[validation_count:]
    report: dict[str, object] = {
        "generated_at_utc": datetime.now(timezone.utc).isoformat(),
        "total_examples": len(accepted),
        "train_examples": len(train),
        "validation_examples": len(validation),
        "language_counts": dict(Counter(str(record["language"]) for record in accepted)),
        "source_counts": dict(Counter(str(record["source"]) for record in accepted)),
        "contextual_examples": contextual_examples,
        "licenses": dict(Counter(str(record["license"]) for record in accepted)),
        "source_sha256": source_sha256,
        "filtering": {
            "max_prompt_characters": MAX_PROMPT_CHARACTERS,
            "max_response_characters": MAX_RESPONSE_CHARACTERS,
            "rejects_links": True,
            "rejects_code_or_markup": True,
            "rejects_lists": True,
            "rejects_ai_disclaimers": True,
        },
        "rejected": {"duplicate": len(normalized_records) - len(accepted)},
        "seed": seed,
        "validation_ratio": validation_ratio,
    }

    destination = Path(output_dir)
    destination.mkdir(parents=True, exist_ok=True)
    train_path = destination / "train.jsonl"
    validation_path = destination / "val.jsonl"
    manifest_path = destination / "manifest.json"
    train_partial = train_path.with_suffix(".jsonl.partial")
    validation_partial = validation_path.with_suffix(".jsonl.partial")
    manifest_partial = manifest_path.with_suffix(".json.partial")
    try:
        _write_jsonl(train_partial, train)
        _write_jsonl(validation_partial, validation)
        manifest_partial.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        os.replace(train_partial, train_path)
        os.replace(validation_partial, validation_path)
        os.replace(manifest_partial, manifest_path)
    except Exception:
        train_partial.unlink(missing_ok=True)
        validation_partial.unlink(missing_ok=True)
        manifest_partial.unlink(missing_ok=True)
        raise
    return report
