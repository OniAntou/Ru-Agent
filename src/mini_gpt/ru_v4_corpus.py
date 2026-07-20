from __future__ import annotations

import json
import os
import re
from collections import Counter
from hashlib import sha256
from pathlib import Path
from random import Random
from typing import Any

from .data import normalize_text
from .ru_qwen_corpus import casual_rejection_reason
from .ru_style import RU_SYSTEM_PROMPT
from .ru_v4_dialogues import LICENSE, SOURCE

EXPECTED_EXAMPLES = 400
SPLIT_COUNTS = {"train": 320, "validation": 40, "held_out": 40}
LANGUAGE_COUNTS = {"vi": 160, "en": 160, "mixed": 80}
MINIMUM_CONTEXTUAL_EXAMPLES = 120
MINIMUM_DIRECT_ACKNOWLEDGEMENTS = 100
MINIMUM_EVERYDAY_HELP = 60
VALID_MODES = {"acknowledge", "follow_up", "everyday_help", "story", "light_humour", "correction"}
_VI_DEFAULT_VOICE = re.compile(r"\b(?:tôi|mình|bạn)\b", re.IGNORECASE)


def _canonical_rows(rows: list[dict[str, object]]) -> str:
    serialized = "\n".join(json.dumps(row, ensure_ascii=False, sort_keys=True, separators=(",", ":")) for row in rows)
    return sha256(serialized.encode("utf-8")).hexdigest()


def _is_contextual(row: dict[str, object]) -> bool:
    messages = row["messages"]
    assert isinstance(messages, list)
    return any(message["role"] == "assistant" for message in messages[:-1])


def _normalize_row(raw_row: dict[str, object]) -> dict[str, object]:
    expected_fields = {"messages", "response", "language", "mode", "source", "license"}
    if set(raw_row) != expected_fields:
        raise ValueError("V4 record has unexpected fields")
    raw_messages = raw_row["messages"]
    if not isinstance(raw_messages, list) or len(raw_messages) < 2:
        raise ValueError("V4 record must contain system and conversation messages")
    messages: list[dict[str, str]] = []
    for raw_message in raw_messages:
        if not isinstance(raw_message, dict) or set(raw_message) != {"role", "content"}:
            raise ValueError("V4 message must contain role and content")
        role, content = raw_message["role"], raw_message["content"]
        if role not in {"system", "user", "assistant"} or not isinstance(content, str):
            raise ValueError("V4 message has an unsupported role or content")
        content = normalize_text(content)
        if not content:
            raise ValueError("V4 message content cannot be empty")
        messages.append({"role": role, "content": content})
    if messages[0] != {"role": "system", "content": RU_SYSTEM_PROMPT}:
        raise ValueError("V4 record must start with the Ru system prompt")
    turn_roles = [message["role"] for message in messages[1:]]
    if turn_roles[0] != "user" or turn_roles[-1] != "user" or any(left == right for left, right in zip(turn_roles, turn_roles[1:])):
        raise ValueError("V4 conversation roles must alternate and end with user")
    response = raw_row["response"]
    language = raw_row["language"]
    mode = raw_row["mode"]
    source = raw_row["source"]
    license_name = raw_row["license"]
    if not all(isinstance(value, str) for value in (response, language, mode, source, license_name)):
        raise ValueError("V4 response metadata must be strings")
    response = normalize_text(response)
    if language not in LANGUAGE_COUNTS:
        raise ValueError("V4 record has an unsupported language")
    if mode not in VALID_MODES:
        raise ValueError("V4 record has an unsupported response mode")
    if source != SOURCE or license_name != LICENSE:
        raise ValueError("V4 record provenance must be teacher-curated project-owned")
    if language == "vi" and _VI_DEFAULT_VOICE.search(response):
        raise ValueError("Vietnamese Ru reply must use the default em/anh voice")
    reason = casual_rejection_reason(messages[-1]["content"], response)
    if reason is not None:
        raise ValueError(f"V4 record failed quality gate: {reason}")
    return {
        "messages": messages,
        "response": response,
        "language": language,
        "mode": mode,
        "source": source,
        "license": license_name,
    }


def _write_jsonl(path: Path, rows: list[dict[str, object]]) -> None:
    path.write_text("\n".join(json.dumps(row, ensure_ascii=False) for row in rows) + "\n", encoding="utf-8")


def build_ru_v4_corpus(rows: list[dict[str, object]], output_dir: str | Path, seed: int = 42) -> dict[str, object]:
    normalized = [_normalize_row(row) for row in rows]
    if len(normalized) != EXPECTED_EXAMPLES:
        raise ValueError(f"V4 corpus must contain exactly {EXPECTED_EXAMPLES} rows")
    if Counter(str(row["language"]) for row in normalized) != Counter(LANGUAGE_COUNTS):
        raise ValueError("V4 corpus language counts do not match the approved shape")
    mode_counts = Counter(str(row["mode"]) for row in normalized)
    if mode_counts["acknowledge"] < MINIMUM_DIRECT_ACKNOWLEDGEMENTS or mode_counts["everyday_help"] < MINIMUM_EVERYDAY_HELP:
        raise ValueError("V4 corpus does not contain enough direct acknowledgements or everyday help")
    contextual_examples = sum(_is_contextual(row) for row in normalized)
    if contextual_examples < MINIMUM_CONTEXTUAL_EXAMPLES:
        raise ValueError("V4 corpus does not contain enough contextual conversations")
    keys = [json.dumps({"messages": row["messages"], "response": row["response"]}, ensure_ascii=False, sort_keys=True) for row in normalized]
    if len(set(keys)) != len(keys):
        raise ValueError("V4 corpus contains duplicate conversations")
    shuffled = [*normalized]
    Random(seed).shuffle(shuffled)
    held_out = shuffled[: SPLIT_COUNTS["held_out"]]
    validation_end = SPLIT_COUNTS["held_out"] + SPLIT_COUNTS["validation"]
    validation = shuffled[SPLIT_COUNTS["held_out"] : validation_end]
    train = shuffled[validation_end:]
    report: dict[str, object] = {
        "total_examples": len(normalized),
        "split_counts": {"train": len(train), "validation": len(validation), "held_out": len(held_out)},
        "language_counts": dict(Counter(str(row["language"]) for row in normalized)),
        "mode_counts": dict(mode_counts),
        "contextual_examples": contextual_examples,
        "direct_acknowledgements": mode_counts["acknowledge"],
        "everyday_help_examples": mode_counts["everyday_help"],
        "source_counts": dict(Counter(str(row["source"]) for row in normalized)),
        "licenses": dict(Counter(str(row["license"]) for row in normalized)),
        "source_sha256": _canonical_rows(normalized),
        "seed": seed,
    }
    destination = Path(output_dir)
    destination.mkdir(parents=True, exist_ok=True)
    paths = {"train": destination / "train.jsonl", "validation": destination / "val.jsonl", "held_out": destination / "held_out.jsonl"}
    partial_paths = {name: path.with_suffix(path.suffix + ".partial") for name, path in paths.items()}
    manifest_path = destination / "manifest.json"
    manifest_partial = manifest_path.with_suffix(".json.partial")
    try:
        _write_jsonl(partial_paths["train"], train)
        _write_jsonl(partial_paths["validation"], validation)
        _write_jsonl(partial_paths["held_out"], held_out)
        manifest_partial.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        for name, path in paths.items():
            os.replace(partial_paths[name], path)
        os.replace(manifest_partial, manifest_path)
    except Exception:
        for path in (*partial_paths.values(), manifest_partial):
            path.unlink(missing_ok=True)
        raise
    return report
