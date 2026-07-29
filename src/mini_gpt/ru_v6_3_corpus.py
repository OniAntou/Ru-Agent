from __future__ import annotations

import re
from collections import Counter
from pathlib import Path

from ._base_corpus import canonical_rows, write_splits_atomic
from .ru_style import ru_system_prompt_for
from .ru_v6_3_dialogues import (
    AUTHORED_ROWS,
    EXPECTED_LANGUAGE_COUNTS,
    EXPECTED_MODE_COUNTS,
    EXPECTED_TOTAL,
    EXPECTED_TURN_BAND_COUNTS,
    LICENSE,
    SOURCE,
)


_META_PATTERN = re.compile(r"(?i)(?:\bas an ai\b|\blanguage model\b|\bmô hình ai\b|\bngôn ngữ ai\b|\brobot\b)")
_UNSAFE_PATTERN = re.compile(
    r"(?i)(?:\bsex\b|\bsexting\b|\borgasm\b|\bsexual intercourse\b|\bgenitals\b|\bpenis\b|\bvagina\b|"
    r"\bquan hệ tình dục\b|\bcực khoái\b|\bthủ dâm\b|\bâm đạo\b|\bdương vật\b|\bcưỡng hiếp\b|"
    r"\bgiết anh\b|\bgiết em\b|\bkill you\b|\bshoot you\b|\bgraphic violence\b|\btrẻ em\b|\bminor\b)"
)
_VTS_PATTERN = re.compile(r"\[\[\s*vts\s*:", re.IGNORECASE)
_URL_PATTERN = re.compile(r"(?i)(?:https?://|www\.)")
_VI_WRONG_VOICE = re.compile(r"(?i)\b(?:tôi|mình|bạn|cậu)\b")
_EN_VI_MARKER = re.compile(r"(?i)\b(?:anh|em|nhé|ừ|không)\b")
_PROFANITY_PATTERN = re.compile(r"(?i)(?:\b(?:damn|hell)\b|\u0111\u1ec7t|v\u00e3i|m\u1eb9 ki\u1ebfp)")


def _validate_row(row: dict[str, object]) -> dict[str, object]:
    expected = {"messages", "response", "language", "mode", "intensity", "turn_band", "source", "license", "target", "profanity"}
    if set(row) != expected:
        raise ValueError("v6.3 authored row has unexpected fields")
    language = row["language"]
    if language not in EXPECTED_LANGUAGE_COUNTS or row["mode"] not in EXPECTED_MODE_COUNTS or row["turn_band"] not in EXPECTED_TURN_BAND_COUNTS or row["profanity"] not in {"none", "mild", "medium"}:
        raise ValueError("v6.3 authored metadata is invalid")
    messages = row["messages"]
    if not isinstance(messages, list) or len(messages) < 4 or not all(isinstance(message, dict) for message in messages):
        raise ValueError("v6.3 messages are malformed")
    if messages[0] != {"role": "system", "content": ru_system_prompt_for(str(language))}:
        raise ValueError("v6.3 row has the wrong system prompt")
    roles = [str(message.get("role")) for message in messages[1:]]
    if roles[0] != "user" or roles[-1] != "user" or any(left == right for left, right in zip(roles, roles[1:])):
        raise ValueError("v6.3 conversation roles do not alternate")
    if any(not isinstance(message.get("content"), str) or not str(message["content"]).strip() for message in messages):
        raise ValueError("v6.3 message content cannot be empty")
    response = str(row["response"]).strip()
    combined = "\n".join(str(message["content"]) for message in messages[1:]) + "\n" + response
    detected_profanity = bool(_PROFANITY_PATTERN.search(response))
    if "Nya~" in response or (row["profanity"] == "none" and detected_profanity) or (row["profanity"] != "none" and not detected_profanity) or _META_PATTERN.search(combined) or _UNSAFE_PATTERN.search(combined) or _VTS_PATTERN.search(combined) or _URL_PATTERN.search(combined):
        raise ValueError("v6.3 row failed safety/metadata gate")
    if language == "vi" and ("anh" not in response.casefold() or "em" not in response.casefold() or _VI_WRONG_VOICE.search(response)):
        raise ValueError("v6.3 Vietnamese response failed anh/em voice gate")
    if language == "mixed" and ("anh" not in response.casefold() or "em" not in response.casefold() or not re.search(r"\b(?:okay|not|my|got|follow|simple|english|dramatic|stay|fair|keep|can)\b", response, re.IGNORECASE)):
        raise ValueError("v6.3 mixed response failed bilingual gate")
    if language == "en" and _EN_VI_MARKER.search(response):
        raise ValueError("v6.3 English response contains Vietnamese voice markers")
    if row["source"] != SOURCE or row["license"] != LICENSE:
        raise ValueError("v6.3 row provenance is invalid")
    return row


def _split(rows: list[dict[str, object]], seed: int) -> tuple[list[dict[str, object]], list[dict[str, object]], list[dict[str, object]]]:
    import random

    indices = list(range(len(rows)))
    random.Random(seed).shuffle(indices)
    held_size = len(rows) // 5
    validation_size = held_size
    held = [rows[index] for index in indices[:held_size]]
    validation = [rows[index] for index in indices[held_size:held_size + validation_size]]
    train = [rows[index] for index in indices[held_size + validation_size:]]
    return train, validation, held


def build_ru_v6_3_corpus(output_dir: str | Path, *, seed: int = 63, write: bool = True) -> dict[str, object]:
    rows = [_validate_row(row) for row in AUTHORED_ROWS]
    if len(rows) != EXPECTED_TOTAL:
        raise ValueError(f"v6.3 authored core must contain {EXPECTED_TOTAL} rows")
    if Counter(str(row["language"]) for row in rows) != EXPECTED_LANGUAGE_COUNTS:
        raise ValueError("v6.3 language quotas are invalid")
    if Counter(str(row["mode"]) for row in rows) != EXPECTED_MODE_COUNTS:
        raise ValueError("v6.3 mode quotas are invalid")
    if Counter(str(row["turn_band"]) for row in rows) != EXPECTED_TURN_BAND_COUNTS:
        raise ValueError("v6.3 turn-band quotas are invalid")
    prompts = [str(row["messages"][-1]["content"]).casefold() for row in rows]
    if len(prompts) != len(set(prompts)):
        raise ValueError("v6.3 authored core contains duplicate final prompts")
    pairs = [(prompt, str(row["response"]).casefold()) for prompt, row in zip(prompts, rows)]
    if len(pairs) != len(set(pairs)):
        raise ValueError("v6.3 authored core contains duplicate prompt/response pairs")
    train, validation, held_out = _split(rows, seed)
    report: dict[str, object] = {
        "dataset_id": "ru_v6_3_targeted",
        "dataset_version": "v6.3",
        "lineage": "new authored rows targeted from ru_hard_v1 findings; no v6.2 rows copied",
        "base_adapter": "D:/LLM/checkpoints/ru-llama3.1-8b-instruct-v6.2-fresh",
        "authored_total_examples": len(rows),
        "total_examples": len(rows),
        "split_counts": {"train": len(train), "validation": len(validation), "held_out": len(held_out)},
        "language_counts": dict(Counter(str(row["language"]) for row in rows)),
        "mode_counts": dict(Counter(str(row["mode"]) for row in rows)),
        "turn_band_counts": dict(Counter(str(row["turn_band"]) for row in rows)),
        "source_counts": dict(Counter(str(row["source"]) for row in rows)),
        "profanity_counts": dict(Counter(str(row["profanity"]) for row in rows)),
        "authored_sha256": canonical_rows(rows),
        "seed": seed,
        "quality_gates": {
            "safe_non_explicit": True,
            "anh_em_voice": True,
            "mixed_language_bridge": True,
            "repair_history": True,
            "unique_final_prompts": True,
            "vts_markers_out_of_text": True,
            "bounded_contextual_profanity": True,
            "training_started": False,
        },
    }
    if write:
        write_splits_atomic(Path(output_dir), train, validation, held_out, report)
    return report
