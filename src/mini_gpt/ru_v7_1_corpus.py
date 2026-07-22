from __future__ import annotations

import json
import os
import re
from collections import Counter
from hashlib import sha256
from pathlib import Path
from random import Random

from .data import normalize_text
from .ru_style import RU_SYSTEM_PROMPT
from .ru_v7_1_dialogues import (
    LICENSE,
    REPAIR_LANGUAGE_COUNTS,
    REPAIR_MAX_LIGHT_FLIRT_ROWS,
    REPAIR_MODE_COUNTS,
    REPAIR_TOTAL_ROWS,
    SOURCE,
    v7_1_repair_records,
)
from .ru_v7_corpus import (
    _AI_DISCLAIMER_PATTERN,
    _GENERIC_REFUSAL_PATTERN,
    _GRAPHIC_PATTERN,
    _HARMFUL_INSTRUCTION_PATTERN,
    _LIST_PATTERN,
    _MINOR_PATTERN,
    _POSSESSIVE_PATTERN,
    _STAGE_DIRECTION_PATTERN,
    _VIOLENCE_PATTERN,
    _normalize_row as _normalize_v7_row,
)
from .ru_v7_dialogues import v7_dialogue_records

SPLIT_COUNTS = {"train": 228, "validation": 36, "held_out": 36}
VALID_FLIRT_LEVELS = frozenset({"none", "light"})
MAX_PROMPT_CHARACTERS = 420
MAX_RESPONSE_CHARACTERS = 420
ALLOWED_CROSS_SOURCE_PROMPTS = frozenset(
    {
        "I feel a little tired today.",
        "I just got home.",
        "Ru, hôm nay I feel better.",
    }
)

_VI_SIGNAL_PATTERN = re.compile(
    r"(?i)\b(?:anh|em|hôm|nay|nghe|mệt|nghỉ|muốn|đang|vừa|này|đó|rồi|chuyện|giúp|thư giãn|tin vui|đầu óc|cà phê)\b"
)
_EN_SIGNAL_PATTERN = re.compile(
    r"(?i)\b(?:i|you|your|the|a|an|and|or|is|are|was|were|what|which|how|would|does|do|want|feel|sounds|today|with|for|to|from|after|before|quiet|company|coffee|home|help|slow|down)\b"
)
_WRONG_VI_VOICE_PATTERN = re.compile(r"(?i)\b(?:tôi|mình|bạn)\b")


def _canonical_rows(rows: list[dict[str, object]]) -> str:
    serialized = "\n".join(
        json.dumps(row, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
        for row in rows
    )
    return sha256(serialized.encode("utf-8")).hexdigest()


def _is_contextual(row: dict[str, object]) -> bool:
    messages = row["messages"]
    assert isinstance(messages, list)
    return any(message["role"] == "assistant" for message in messages[1:-1])


def _conversation_text(messages: list[dict[str, str]]) -> str:
    return "\n".join(message["content"] for message in messages[1:])


def _repair_quality_reason(
    messages: list[dict[str, str]],
    response: str,
    language: str,
    mode: str,
    flirt_level: str,
) -> str | None:
    prompt = messages[-1]["content"]
    combined = f"{_conversation_text(messages)}\n{response}"
    if len(prompt) > MAX_PROMPT_CHARACTERS or len(response) > MAX_RESPONSE_CHARACTERS:
        return "too_long"
    if len(response) < 24:
        return "too_short"
    if _LIST_PATTERN.search(response):
        return "list_like"
    if _AI_DISCLAIMER_PATTERN.search(response) or _GENERIC_REFUSAL_PATTERN.search(response):
        return "generic_refusal"
    for pattern, name in (
        (_MINOR_PATTERN, "minor_content"),
        (_GRAPHIC_PATTERN, "graphic_content"),
        (_VIOLENCE_PATTERN, "violent_content"),
        (_POSSESSIVE_PATTERN, "possessive_content"),
        (_HARMFUL_INSTRUCTION_PATTERN, "harmful_content"),
        (_STAGE_DIRECTION_PATTERN, "stage_direction"),
    ):
        if pattern.search(combined):
            return name
    if response.count("?") > 1:
        return "too_many_questions"
    if flirt_level not in VALID_FLIRT_LEVELS:
        return "invalid_flirt_level"
    if language == "vi" and _WRONG_VI_VOICE_PATTERN.search(response):
        return "vietnamese_voice"
    if language == "en" and not _EN_SIGNAL_PATTERN.search(response):
        return "english_signal_missing"
    if language == "mixed":
        if _WRONG_VI_VOICE_PATTERN.search(response):
            return "mixed_voice"
        if not _VI_SIGNAL_PATTERN.search(response) or not _EN_SIGNAL_PATTERN.search(response):
            return "mixed_language"
    if mode == "mixed_code_switch" and language != "mixed":
        if not _VI_SIGNAL_PATTERN.search(response):
            return "repair_code_switch_signal_missing"
    return None


def _normalize_repair_row(raw_row: dict[str, object]) -> dict[str, object]:
    expected_fields = {"messages", "response", "language", "mode", "source", "license", "flirt_level"}
    if set(raw_row) != expected_fields:
        raise ValueError("V7.1 repair record has unexpected fields")
    raw_messages = raw_row["messages"]
    if not isinstance(raw_messages, list) or len(raw_messages) < 2:
        raise ValueError("V7.1 repair record must contain system and conversation messages")

    messages: list[dict[str, str]] = []
    for raw_message in raw_messages:
        if not isinstance(raw_message, dict) or set(raw_message) != {"role", "content"}:
            raise ValueError("V7.1 repair message must contain role and content")
        role, content = raw_message["role"], raw_message["content"]
        if role not in {"system", "user", "assistant"} or not isinstance(content, str):
            raise ValueError("V7.1 repair message has an unsupported role or content")
        content = normalize_text(content)
        if not content:
            raise ValueError("V7.1 repair message content cannot be empty")
        messages.append({"role": role, "content": content})

    if messages[0] != {"role": "system", "content": RU_SYSTEM_PROMPT}:
        raise ValueError("V7.1 repair record must start with the Ru system prompt")
    turn_roles = [message["role"] for message in messages[1:]]
    if (
        turn_roles[0] != "user"
        or turn_roles[-1] != "user"
        or any(left == right for left, right in zip(turn_roles, turn_roles[1:]))
    ):
        raise ValueError("V7.1 repair conversation roles must alternate and end with user")

    response = raw_row["response"]
    language = raw_row["language"]
    mode = raw_row["mode"]
    source = raw_row["source"]
    license_name = raw_row["license"]
    flirt_level = raw_row["flirt_level"]
    if not all(isinstance(value, str) for value in (response, language, mode, source, license_name, flirt_level)):
        raise ValueError("V7.1 repair response metadata must be strings")
    response = normalize_text(response)
    if language not in REPAIR_LANGUAGE_COUNTS:
        raise ValueError("V7.1 repair record has an unsupported language")
    if mode not in REPAIR_MODE_COUNTS:
        raise ValueError("V7.1 repair record has an unsupported mode")
    if source != SOURCE or license_name != LICENSE:
        raise ValueError("V7.1 repair record provenance is invalid")
    reason = _repair_quality_reason(messages, response, language, mode, flirt_level)
    if reason is not None:
        raise ValueError(f"V7.1 repair record failed quality gate: {reason}")
    return {
        "messages": messages,
        "response": response,
        "language": language,
        "mode": mode,
        "source": source,
        "license": license_name,
        "flirt_level": flirt_level,
    }


def _write_jsonl(path: Path, rows: list[dict[str, object]]) -> None:
    path.write_text(
        "\n".join(json.dumps(row, ensure_ascii=False) for row in rows) + "\n",
        encoding="utf-8",
    )


def build_ru_v7_1_corpus(
    repair_rows: list[dict[str, object]],
    output_dir: str | Path,
    seed: int = 42,
) -> dict[str, object]:
    base_rows = [_normalize_v7_row(row) for row in v7_dialogue_records()]
    normalized_repair = [_normalize_repair_row(row) for row in repair_rows]
    if len(base_rows) != 180:
        raise ValueError(f"V7 base corpus must contain 180 rows, found {len(base_rows)}")
    if len(normalized_repair) != REPAIR_TOTAL_ROWS:
        raise ValueError(f"V7.1 repair corpus must contain {REPAIR_TOTAL_ROWS} rows")
    if Counter(str(row["language"]) for row in normalized_repair) != Counter(REPAIR_LANGUAGE_COUNTS):
        raise ValueError("V7.1 repair language counts do not match the approved shape")
    if Counter(str(row["mode"]) for row in normalized_repair) != Counter(REPAIR_MODE_COUNTS):
        raise ValueError("V7.1 repair mode counts do not match the approved shape")
    if sum(str(row["flirt_level"]) == "light" for row in normalized_repair) > REPAIR_MAX_LIGHT_FLIRT_ROWS:
        raise ValueError("V7.1 repair corpus contains too many light-flirt rows")

    repair_prompts = [str(row["messages"][-1]["content"]) for row in normalized_repair]
    if len(repair_prompts) != len(set(repair_prompts)):
        raise ValueError("V7.1 repair corpus contains duplicate final prompts")
    base_prompts = [str(row["messages"][-1]["content"]) for row in base_rows]
    cross_source_overlaps = sorted(set(base_prompts) & set(repair_prompts))
    if set(cross_source_overlaps) != set(ALLOWED_CROSS_SOURCE_PROMPTS):
        raise ValueError("V7.1 repair corpus has an unexpected cross-source prompt overlap")

    base_pairs = {
        json.dumps({"messages": row["messages"], "response": row["response"]}, ensure_ascii=False, sort_keys=True)
        for row in base_rows
    }
    repair_pairs = {
        json.dumps({"messages": row["messages"], "response": row["response"]}, ensure_ascii=False, sort_keys=True)
        for row in normalized_repair
    }
    if base_pairs & repair_pairs:
        raise ValueError("V7.1 combined corpus contains exact duplicate prompt/response pairs")

    combined = [*base_rows, *normalized_repair]
    if len(combined) != 300:
        raise ValueError(f"V7.1 combined corpus must contain 300 rows, found {len(combined)}")
    shuffled = [*combined]
    Random(seed).shuffle(shuffled)
    held_out = shuffled[: SPLIT_COUNTS["held_out"]]
    validation_end = SPLIT_COUNTS["held_out"] + SPLIT_COUNTS["validation"]
    validation = shuffled[SPLIT_COUNTS["held_out"] : validation_end]
    train = shuffled[validation_end:]

    report: dict[str, object] = {
        "total_examples": len(combined),
        "base_examples": len(base_rows),
        "repair_examples": len(normalized_repair),
        "split_counts": {"train": len(train), "validation": len(validation), "held_out": len(held_out)},
        "language_counts": dict(Counter(str(row["language"]) for row in combined)),
        "mode_counts": dict(Counter(str(row["mode"]) for row in combined)),
        "repair_language_counts": dict(Counter(str(row["language"]) for row in normalized_repair)),
        "repair_mode_counts": dict(Counter(str(row["mode"]) for row in normalized_repair)),
        "flirt_counts": dict(Counter(str(row["flirt_level"]) for row in combined)),
        "contextual_examples": sum(_is_contextual(row) for row in combined),
        "source_counts": dict(Counter(str(row["source"]) for row in combined)),
        "licenses": dict(Counter(str(row["license"]) for row in combined)),
        "base_source_sha256": _canonical_rows(base_rows),
        "repair_source_sha256": _canonical_rows(normalized_repair),
        "combined_source_sha256": _canonical_rows(combined),
        "cross_source_prompt_overlap_count": len(cross_source_overlaps),
        "cross_source_prompt_overlaps": cross_source_overlaps,
        "seed": seed,
    }

    destination = Path(output_dir)
    destination.mkdir(parents=True, exist_ok=True)
    paths = {
        "train": destination / "train.jsonl",
        "validation": destination / "val.jsonl",
        "held_out": destination / "held_out.jsonl",
    }
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
