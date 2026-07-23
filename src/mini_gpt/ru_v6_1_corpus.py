from __future__ import annotations

import json
import os
import re
from collections import Counter
from hashlib import sha256
from pathlib import Path
from random import Random

from .data import normalize_text
from .ru_qwen_corpus import casual_rejection_reason
from .ru_style import RU_SYSTEM_PROMPT
from .ru_v6_1_dialogues import LICENSE, SOURCE, V6_1_ROWS

EXPECTED_TOTAL = 120
EXPECTED_SPLIT_COUNTS = {"train": 84, "validation": 18, "held_out": 18}
EXPECTED_LANGUAGE_COUNTS = {"vi": 60, "mixed": 36, "en": 24}
EXPECTED_IDENTITY_CORRECTION_EXAMPLES = 18
EXPECTED_MODE_COUNTS = {
    "acknowledge": 24,
    "follow_up": 30,
    "everyday_help": 20,
    "identity_repair": 18,
    "emotion": 16,
    "light_humour": 12,
}
MAX_LIGHT_FLIRT_EXAMPLES = 18
VALID_LANGUAGES = frozenset(EXPECTED_LANGUAGE_COUNTS)
VALID_MODES = frozenset(EXPECTED_MODE_COUNTS)
VALID_FLIRT_LEVELS = frozenset({"none", "light"})

_FORBIDDEN_VI_SELF_REFERENCE_PATTERN = re.compile(r"(?i)(?<!\w)(?:tớ|tôi|mình)(?!\w)")
_VI_SIGNAL_PATTERN = re.compile(
    r"(?i)\b(?:anh|em|hôm|nay|nghe|mệt|nghỉ|muốn|đang|vừa|này|đó|rồi|chuyện|giúp|thư|giãn|tin|vui|đầu|óc|cà|phê|nhé|không|vậy|cho|sẽ|nói|làm|được|tối|sáng|ngày|uống)\b"
)
_EN_SIGNAL_PATTERN = re.compile(
    r"(?i)\b(?:i|you|your|the|a|an|and|or|is|are|was|were|what|which|how|would|does|do|want|feel|sounds|today|tonight|with|for|to|from|after|before|quiet|company|coffee|home|help|slow|down|one|just|keep|let|maybe|send|worked|next|could|good|film|weekend|start|write|need|make|it|this|that|please|stay|take|have|can|will|memories|something|different)\b"
)


def _canonical_rows(rows: list[dict[str, object]]) -> str:
    serialized = "\n".join(
        json.dumps(row, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
        for row in rows
    )
    return sha256(serialized.encode("utf-8")).hexdigest()


def _pair_key(row: dict[str, object]) -> str:
    return json.dumps(
        {"messages": row["messages"], "response": row["response"]},
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    )


def _is_contextual(row: dict[str, object]) -> bool:
    messages = row["messages"]
    assert isinstance(messages, list)
    return any(message["role"] == "assistant" for message in messages[1:-1])


def _normalize_row(raw_row: dict[str, object]) -> dict[str, object]:
    expected_fields = {"messages", "response", "language", "mode", "source", "license", "flirt_level"}
    if set(raw_row) != expected_fields:
        raise ValueError("v6.1 authored record has unexpected fields")

    raw_messages = raw_row["messages"]
    if not isinstance(raw_messages, list) or len(raw_messages) < 2:
        raise ValueError("v6.1 authored record must contain system and conversation messages")

    messages: list[dict[str, str]] = []
    for raw_message in raw_messages:
        if not isinstance(raw_message, dict) or set(raw_message) != {"role", "content"}:
            raise ValueError("v6.1 message must contain exactly role and content")
        role = raw_message["role"]
        content = raw_message["content"]
        if role not in {"system", "user", "assistant"} or not isinstance(content, str):
            raise ValueError("v6.1 message has an unsupported role or content")
        content = normalize_text(content)
        if not content:
            raise ValueError("v6.1 message content cannot be empty")
        messages.append({"role": role, "content": content})

    if messages[0] != {"role": "system", "content": RU_SYSTEM_PROMPT}:
        raise ValueError("v6.1 record must start with the Ru system prompt")
    turn_roles = [message["role"] for message in messages[1:]]
    if (
        turn_roles[0] != "user"
        or turn_roles[-1] != "user"
        or any(left == right for left, right in zip(turn_roles, turn_roles[1:]))
    ):
        raise ValueError("v6.1 conversation roles must alternate and end with user")

    response = raw_row["response"]
    language = raw_row["language"]
    mode = raw_row["mode"]
    source = raw_row["source"]
    license_name = raw_row["license"]
    flirt_level = raw_row["flirt_level"]
    values = (response, language, mode, source, license_name, flirt_level)
    if not all(isinstance(value, str) for value in values):
        raise ValueError("v6.1 response metadata must be strings")

    response = normalize_text(response)
    if language not in VALID_LANGUAGES:
        raise ValueError("v6.1 record has an unsupported language")
    if mode not in VALID_MODES:
        raise ValueError("v6.1 record has an unsupported mode")
    if source != SOURCE or license_name != LICENSE:
        raise ValueError("v6.1 record provenance is invalid")
    if flirt_level not in VALID_FLIRT_LEVELS:
        raise ValueError("v6.1 record has an unsupported flirt level")

    reason = casual_rejection_reason(messages[-1]["content"], response)
    if reason is not None:
        raise ValueError(f"v6.1 authored record failed quality gate: {reason}")
    if language != "en" and _FORBIDDEN_VI_SELF_REFERENCE_PATTERN.search(response):
        raise ValueError("v6.1 authored record failed quality gate: vietnamese_voice")
    if language == "en" and not _EN_SIGNAL_PATTERN.search(response):
        raise ValueError("v6.1 authored record failed quality gate: english_signal_missing")
    if language == "mixed" and (
        not _VI_SIGNAL_PATTERN.search(response) or not _EN_SIGNAL_PATTERN.search(response)
    ):
        raise ValueError("v6.1 authored record failed quality gate: mixed_language")

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


def build_ru_v6_1_corpus(
    output_dir: str | Path,
    *,
    seed: int = 42,
    write: bool = True,
) -> dict[str, object]:
    normalized_rows = [_normalize_row(row) for row in V6_1_ROWS]
    if len(normalized_rows) != EXPECTED_TOTAL:
        raise ValueError(f"v6.1 corpus must contain exactly {EXPECTED_TOTAL} rows")

    pair_keys = [_pair_key(row) for row in normalized_rows]
    if len(pair_keys) != len(set(pair_keys)):
        raise ValueError("v6.1 corpus contains exact duplicate prompt/response pairs")
    prompts = [str(row["messages"][-1]["content"]) for row in normalized_rows]
    if len(prompts) != len(set(prompts)):
        raise ValueError("v6.1 corpus contains duplicate final prompts")

    language_counts = dict(Counter(str(row["language"]) for row in normalized_rows))
    if language_counts != EXPECTED_LANGUAGE_COUNTS:
        raise ValueError(f"v6.1 language counts do not match the approved shape: {language_counts}")
    mode_counts = dict(Counter(str(row["mode"]) for row in normalized_rows))
    if mode_counts != EXPECTED_MODE_COUNTS:
        raise ValueError(f"v6.1 mode counts do not match the approved shape: {mode_counts}")
    identity_count = sum(str(row["mode"]) == "identity_repair" for row in normalized_rows)
    if identity_count != EXPECTED_IDENTITY_CORRECTION_EXAMPLES:
        raise ValueError("v6.1 identity-correction count does not match the approved shape")
    light_flirt_count = sum(str(row["flirt_level"]) == "light" for row in normalized_rows)
    if light_flirt_count > MAX_LIGHT_FLIRT_EXAMPLES:
        raise ValueError("v6.1 corpus contains too many light-flirt rows")
    contextual_count = sum(_is_contextual(row) for row in normalized_rows)
    if contextual_count < 48:
        raise ValueError(f"v6.1 corpus needs at least 48 contextual rows, found {contextual_count}")

    shuffled = [*normalized_rows]
    Random(seed).shuffle(shuffled)
    held_out_end = EXPECTED_SPLIT_COUNTS["held_out"]
    validation_end = held_out_end + EXPECTED_SPLIT_COUNTS["validation"]
    held_out = shuffled[:held_out_end]
    validation = shuffled[held_out_end:validation_end]
    train = shuffled[validation_end:]
    splits = {"train": train, "validation": validation, "held_out": held_out}

    split_counts = {name: len(rows) for name, rows in splits.items()}
    if split_counts != EXPECTED_SPLIT_COUNTS:
        raise ValueError(f"v6.1 split counts do not match the approved shape: {split_counts}")
    split_prompt_sets = {
        name: {str(row["messages"][-1]["content"]) for row in rows}
        for name, rows in splits.items()
    }
    if any(split_prompt_sets[left] & split_prompt_sets[right] for left, right in (("train", "validation"), ("train", "held_out"), ("validation", "held_out"))):
        raise ValueError("v6.1 final prompts overlap between splits")

    report: dict[str, object] = {
        "total_examples": len(normalized_rows),
        "split_counts": split_counts,
        "language_counts": language_counts,
        "mode_counts": mode_counts,
        "contextual_examples": contextual_count,
        "identity_correction_examples": identity_count,
        "light_flirt_examples": light_flirt_count,
        "flirt_counts": dict(Counter(str(row["flirt_level"]) for row in normalized_rows)),
        "source_counts": dict(Counter(str(row["source"]) for row in normalized_rows)),
        "licenses": dict(Counter(str(row["license"]) for row in normalized_rows)),
        "source_sha256": _canonical_rows(normalized_rows),
        "train_sha256": _canonical_rows(train),
        "validation_sha256": _canonical_rows(validation),
        "held_out_sha256": _canonical_rows(held_out),
        "seed": seed,
        "quality_gates": {
            "max_light_flirt_examples": MAX_LIGHT_FLIRT_EXAMPLES,
            "rejects_links_code_markup_lists_ai_disclaimers": True,
            "rejects_vietnamese_self_reference": ["tớ", "tôi", "mình"],
        },
    }

    if not write:
        return report

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
