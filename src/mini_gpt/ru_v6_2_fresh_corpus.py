from __future__ import annotations

import json
import re
from collections import Counter
from pathlib import Path
from random import Random
from typing import Any

from ._base_corpus import canonical_rows, write_splits_atomic
from ._text_utils import normalize_text
from .ru_style import RU_SYSTEM_PROMPT, ru_system_prompt_for
from .ru_v6_2_fresh_dialogues import (
    EXPECTED_LANGUAGE_COUNTS,
    EXPECTED_MODE_COUNTS,
    EXPECTED_TOTAL,
    EXPECTED_TURN_BAND_COUNTS,
    FRESH_AUTHORED_ROWS,
    LICENSE,
    SOURCE,
)
from .ru_v6_2_fresh_import import append_imports_to_train, repair_mojibake, select_import_rows


_META_PATTERN = re.compile(r"(?i)(?:\bas an ai\b|\blanguage model\b|\bm\u00f4 h\u00ecnh ai\b|\bng\u00f4n ng\u1eef ai\b|\brobot\b)")
_UNSAFE_PATTERN = re.compile(
    r"(?i)(?:\bsex\b|\borgasm\b|\bsexual intercourse\b|\bgenitals\b|\bpenis\b|\bvagina\b|\bquan h\u1ec7 t\u00ecnh d\u1ee5c\b|\bc\u1ef1c kho\u00e1i\b|\bth\u1ee7 d\u00e2m\b|"
    r"\b\u00e2m \u0111\u1ea1o\b|\bd\u01b0\u01a1ng v\u1eadt\b|\bc\u01b0\u1ee1ng hi\u1ebfp\b|\btr\u1ebb em\b|"
    r"\bgi\u1ebft anh\b|\bgi\u1ebft em\b|\bkill you\b|\bshoot you\b|\bgraphic violence\b)"
)
_URL_PATTERN = re.compile(r"(?i)(?:https?://|www\.)")


def _conversation_roles(messages: list[dict[str, str]], language: str) -> bool:
    if not messages or messages[0] != {"role": "system", "content": ru_system_prompt_for(language)}:
        return False
    turns = [message["role"] for message in messages[1:]]
    return bool(turns) and turns[0] == "user" and turns[-1] == "user" and all(
        left != right for left, right in zip(turns, turns[1:])
    )


def _validate_authored(row: dict[str, object]) -> dict[str, object]:
    expected = {"messages", "response", "language", "mode", "intensity", "turn_band", "source", "license"}
    if set(row) != expected:
        raise ValueError("fresh authored row has unexpected fields")
    messages = row["messages"]
    if not isinstance(messages, list) or not all(isinstance(message, dict) for message in messages):
        raise ValueError("fresh authored row messages are malformed")
    normalized_messages: list[dict[str, str]] = []
    for message in messages:
        if set(message) != {"role", "content"} or not isinstance(message["content"], str):
            raise ValueError("fresh authored message is malformed")
        content = message["content"] if message["role"] == "system" else repair_mojibake(message["content"])
        normalized_messages.append({"role": str(message["role"]), "content": normalize_text(content)})
    if not _conversation_roles(normalized_messages, str(row["language"])):
        raise ValueError("fresh authored row roles do not alternate")
    response = normalize_text(repair_mojibake(str(row["response"])))
    if not response or "Nya~" in response or "[[VTS:" in response or _META_PATTERN.search(response) or _UNSAFE_PATTERN.search(response) or _URL_PATTERN.search(response):
        raise ValueError("fresh authored response failed safety gate")
    if row["language"] not in EXPECTED_LANGUAGE_COUNTS or row["mode"] not in EXPECTED_MODE_COUNTS or row["turn_band"] not in EXPECTED_TURN_BAND_COUNTS:
        raise ValueError("fresh authored metadata is invalid")
    if row["source"] != SOURCE or row["license"] != LICENSE:
        raise ValueError("fresh authored provenance is invalid")
    user_turns = sum(message["role"] == "user" for message in normalized_messages)
    expected_turns = {"short": 2, "medium": 3, "long": 4}
    if user_turns != expected_turns[str(row["turn_band"])]:
        raise ValueError("fresh authored turn band is invalid")
    return {**row, "messages": normalized_messages, "response": response}


def _split(rows: list[dict[str, object]], seed: int) -> tuple[list[dict[str, object]], list[dict[str, object]], list[dict[str, object]]]:
    indices = list(range(len(rows)))
    Random(seed).shuffle(indices)
    held_end = len(rows) // 5
    val_end = held_end * 2
    held = [rows[index] for index in indices[:held_end]]
    validation = [rows[index] for index in indices[held_end:val_end]]
    train = [rows[index] for index in indices[val_end:]]
    return train, validation, held


def build_ru_v6_2_fresh_corpus(
    output_dir: str | Path,
    *,
    approved_file: str | Path | None = None,
    memory_file: str | Path | None = None,
    seed: int = 62,
    write: bool = True,
) -> dict[str, object]:
    authored = [_validate_authored(row) for row in FRESH_AUTHORED_ROWS]
    if len(authored) != EXPECTED_TOTAL:
        raise ValueError(f"fresh authored core must contain {EXPECTED_TOTAL} rows")
    if Counter(str(row["language"]) for row in authored) != EXPECTED_LANGUAGE_COUNTS:
        raise ValueError("fresh language quotas are invalid")
    if Counter(str(row["mode"]) for row in authored) != EXPECTED_MODE_COUNTS:
        raise ValueError("fresh mode quotas are invalid")
    if Counter(str(row["turn_band"]) for row in authored) != EXPECTED_TURN_BAND_COUNTS:
        raise ValueError("fresh turn-band quotas are invalid")
    prompts = [str(row["messages"][-1]["content"]).casefold() for row in authored]
    if len(prompts) != len(set(prompts)):
        raise ValueError("fresh authored core contains duplicate final prompts")
    pairs = [(prompt, str(row["response"]).casefold()) for prompt, row in zip(prompts, authored)]
    if len(pairs) != len(set(pairs)):
        raise ValueError("fresh authored core contains duplicate pairs")
    train, validation, held_out = _split(authored, seed)
    report: dict[str, object] = {
        "authored_total_examples": len(authored),
        "total_examples": len(authored),
        "split_counts": {"train": len(train), "validation": len(validation), "held_out": len(held_out)},
        "language_counts": dict(Counter(str(row["language"]) for row in authored)),
        "mode_counts": dict(Counter(str(row["mode"]) for row in authored)),
        "turn_band_counts": dict(Counter(str(row["turn_band"]) for row in authored)),
        "source_counts": dict(Counter(str(row["source"]) for row in authored)),
        "authored_sha256": canonical_rows(authored),
        "seed": seed,
        "quality_gates": {
            "safe_non_explicit": True,
            "alternating_history": True,
            "duplicate_final_prompts": True,
            "language_specific_personality_prompts": True,
            "requires_nya_suffix": False,
            "vts_markers_out_of_text": True,
            "style_reference_only": "roleplay_chat.md",
        },
    }
    if not write:
        return report
    destination = Path(output_dir)
    write_splits_atomic(destination, train, validation, held_out, report)
    if approved_file is not None or memory_file is not None:
        selection = select_import_rows(
            approved_file or destination / "missing-approved.jsonl",
            memory_file or destination / "missing-memory.json",
        )
        append_imports_to_train(destination, selection)
        report = json.loads((destination / "manifest.json").read_text(encoding="utf-8"))
    return report
