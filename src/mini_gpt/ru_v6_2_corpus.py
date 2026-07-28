from __future__ import annotations

import json
import os
import re
from collections import Counter
from hashlib import sha256
from pathlib import Path
from random import Random

from ._corpus_quality import casual_rejection_reason
from ._text_utils import normalize_text
from .ru_style import RU_SYSTEM_PROMPT
from .ru_v6_2_dialogues import (
    EXPECTED_INTENSITY_COUNTS,
    EXPECTED_LANGUAGE_COUNTS,
    EXPECTED_MODE_COUNTS,
    EXPECTED_TURN_BAND_COUNTS,
    LICENSE,
    SOURCE,
    V6_2_ROWS,
    V6_2_TOTAL_ROWS,
)


EXPECTED_TOTAL = V6_2_TOTAL_ROWS
EXPECTED_SPLIT_COUNTS = {"train": 1680, "validation": 360, "held_out": 360}
EXPECTED_LANGUAGE_COUNTS = dict(EXPECTED_LANGUAGE_COUNTS)
EXPECTED_MODE_COUNTS = dict(EXPECTED_MODE_COUNTS)
EXPECTED_INTENSITY_COUNTS = dict(EXPECTED_INTENSITY_COUNTS)
EXPECTED_TURN_BAND_COUNTS = dict(EXPECTED_TURN_BAND_COUNTS)

VALID_LANGUAGES = frozenset(EXPECTED_LANGUAGE_COUNTS)
VALID_MODES = frozenset(EXPECTED_MODE_COUNTS)
VALID_INTENSITIES = frozenset(EXPECTED_INTENSITY_COUNTS)
VALID_TURN_BANDS = frozenset(EXPECTED_TURN_BAND_COUNTS)

_SELF_REFERENCE_PATTERN = re.compile(r"(?i)(?<!\w)(?:tôi|tớ|mình)(?!\w)")
_GRAPHIC_PATTERN = re.compile(
    r"(?i)(?<!\w)(?:fuck|dick|cock|pussy|cum|semen|đụ|địt|cặc|lồn|tinh trùng|bú|chịch)(?!\w)"
)
_UNSAFE_PATTERN = re.compile(
    r"(?i)(?:\b(?:rape|kill you|giết anh|giết em|cưỡng ép|cưỡng hiếp)\b|"
    r"\b(?:minor|trẻ em|con nít)\b|chỉ được có em|chỉ thuộc về em)"
)
_AI_DISCLAIMER_PATTERN = re.compile(
    r"(?i)\b(?:as an ai language model|i cannot access|i do not have (?:the ability|a physical body)|"
    r"i exist solely as)\b|(?:tôi|mình)\s+là\s+một\s+(?:mô hình|người mẫu)\s+ngôn ngữ\s+ai"
)
_VI_SIGNAL_PATTERN = re.compile(
    r"(?i)(?<!\w)(?:anh|em|hôm|nay|nghe|một|nghỉ|muốn|đang|vừa|này|đó|rồi|chuyện|giúp|thư|giãn|tin|vui|đầu|óc|cà|phê|nhé|không|vậy|cho|sẽ|nói|làm|được|tối|sáng|ngày|uống)(?!\w)"
)
_EN_SIGNAL_PATTERN = re.compile(
    r"(?i)(?<!\w)(?:i|you|your|the|a|an|and|or|is|are|was|were|what|which|how|would|does|do|want|feel|sounds|today|tonight|with|for|to|from|after|before|quiet|company|coffee|home|help|slow|down|one|just|keep|let|maybe|send|worked|next|could|good|film|weekend|start|write|need|make|it|this|that|please|stay|take|have|can|will|memories|something|different|voice|continue|comes|through|solve|everything|lighter|remember|feeling|joined|tiny|comedy|scene|podcast|dinner|game|my)(?!\w)"
)
_CONTEXT_TOKEN_PATTERN = re.compile(r"(?i)(?<!\w)(?:[^\W\d_]{3,})(?!\w)")
_CONTEXT_STOPWORDS = frozenset(
    {
        "anh",
        "em",
        "đang",
        "vẫn",
        "một",
        "này",
        "đó",
        "rồi",
        "nhé",
        "the",
        "that",
        "with",
        "from",
        "about",
        "want",
        "still",
        "you",
        "your",
        "what",
        "feel",
        "have",
        "this",
        "just",
        "like",
        "when",
        "after",
        "before",
        "more",
        "some",
        "could",
        "would",
        "into",
        "keep",
        "hear",
        "take",
        "one",
        "part",
        "time",
        "small",
        "good",
        "today",
        "tonight",
        "little",
        "quiet",
        "conversation",
        "story",
    }
)


def _canonical_rows(rows: list[dict[str, object]]) -> str:
    serialized = "\n".join(
        json.dumps(row, ensure_ascii=False, sort_keys=True, separators=(",", ":")) for row in rows
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


def _context_tokens(text: str) -> set[str]:
    return {
        token.casefold()
        for token in _CONTEXT_TOKEN_PATTERN.findall(text)
        if token.casefold() not in _CONTEXT_STOPWORDS
    }


def _has_context_signal(row: dict[str, object]) -> bool:
    messages = row["messages"]
    assert isinstance(messages, list)
    history = " ".join(str(message["content"]) for message in messages[1:-1])
    response = str(row["response"])
    return bool(_context_tokens(history) & _context_tokens(response))


def _normalize_row(raw_row: dict[str, object]) -> dict[str, object]:
    expected_fields = {
        "messages",
        "response",
        "language",
        "mode",
        "intensity",
        "turn_band",
        "source",
        "license",
    }
    if set(raw_row) != expected_fields:
        raise ValueError("v6.2 authored record has unexpected fields")

    raw_messages = raw_row["messages"]
    if not isinstance(raw_messages, list) or len(raw_messages) < 2:
        raise ValueError("v6.2 authored record must contain system and conversation messages")

    messages: list[dict[str, str]] = []
    for raw_message in raw_messages:
        if not isinstance(raw_message, dict) or set(raw_message) != {"role", "content"}:
            raise ValueError("v6.2 message must contain exactly role and content")
        role = raw_message["role"]
        content = raw_message["content"]
        if role not in {"system", "user", "assistant"} or not isinstance(content, str):
            raise ValueError("v6.2 message has an unsupported role or content")
        content = normalize_text(content)
        if not content:
            raise ValueError("v6.2 message content cannot be empty")
        messages.append({"role": role, "content": content})

    if messages[0] != {"role": "system", "content": RU_SYSTEM_PROMPT}:
        raise ValueError("v6.2 record must start with the Ru system prompt")
    turn_roles = [message["role"] for message in messages[1:]]
    if (
        turn_roles[0] != "user"
        or turn_roles[-1] != "user"
        or any(left == right for left, right in zip(turn_roles, turn_roles[1:]))
    ):
        raise ValueError("v6.2 conversation roles must alternate and end with user")

    response = raw_row["response"]
    language = raw_row["language"]
    mode = raw_row["mode"]
    intensity = raw_row["intensity"]
    turn_band = raw_row["turn_band"]
    source = raw_row["source"]
    license_name = raw_row["license"]
    values = (response, language, mode, intensity, turn_band, source, license_name)
    if not all(isinstance(value, str) for value in values):
        raise ValueError("v6.2 response metadata must be strings")

    response = normalize_text(response)
    if language not in VALID_LANGUAGES:
        raise ValueError("v6.2 record has an unsupported language")
    if mode not in VALID_MODES:
        raise ValueError("v6.2 record has an unsupported mode")
    if intensity not in VALID_INTENSITIES:
        raise ValueError("v6.2 record has an unsupported intensity")
    if turn_band not in VALID_TURN_BANDS:
        raise ValueError("v6.2 record has an unsupported turn band")
    if source != SOURCE or license_name != LICENSE:
        raise ValueError("v6.2 record provenance is invalid")

    reason = casual_rejection_reason(messages[-1]["content"], response)
    if reason is not None:
        raise ValueError(f"v6.2 authored record failed quality gate: {reason}")
    if _AI_DISCLAIMER_PATTERN.search(response):
        raise ValueError("v6.2 authored record failed quality gate: ai_disclaimer")
    if _SELF_REFERENCE_PATTERN.search(response):
        raise ValueError("v6.2 authored record failed quality gate: vietnamese_voice")
    if _GRAPHIC_PATTERN.search(response) or _UNSAFE_PATTERN.search(response):
        raise ValueError("v6.2 authored record failed quality gate: explicit_or_unsafe_content")

    assistant_messages = [message["content"] for message in messages[1:-1] if message["role"] == "assistant"]
    if any(_SELF_REFERENCE_PATTERN.search(content) for content in assistant_messages):
        raise ValueError("v6.2 authored record failed quality gate: vietnamese_voice_history")
    if any(_GRAPHIC_PATTERN.search(content) or _UNSAFE_PATTERN.search(content) for content in assistant_messages):
        raise ValueError("v6.2 authored record failed quality gate: unsafe_history")

    user_turn_count = sum(message["role"] == "user" for message in messages[1:])
    expected_turn_ranges = {
        "short": range(2, 4),
        "medium": range(4, 9),
        "long": range(9, 15),
    }
    if user_turn_count not in expected_turn_ranges[turn_band]:
        raise ValueError("v6.2 record turn count does not match its turn band")
    if turn_band == "long" and not _has_context_signal(
        {
            "messages": messages,
            "response": response,
        }
    ):
        raise ValueError("v6.2 authored record failed quality gate: long_context_missing")

    if language == "en" and not _EN_SIGNAL_PATTERN.search(response):
        raise ValueError("v6.2 authored record failed quality gate: english_signal_missing")
    if language == "mixed" and (
        not _VI_SIGNAL_PATTERN.search(response) or not _EN_SIGNAL_PATTERN.search(response)
    ):
        raise ValueError("v6.2 authored record failed quality gate: mixed_language")
    if language == "vi" and not _VI_SIGNAL_PATTERN.search(response):
        raise ValueError("v6.2 authored record failed quality gate: vietnamese_signal_missing")

    return {
        "messages": messages,
        "response": response,
        "language": language,
        "mode": mode,
        "intensity": intensity,
        "turn_band": turn_band,
        "source": source,
        "license": license_name,
    }


def _write_jsonl(path: Path, rows: list[dict[str, object]]) -> None:
    path.write_text(
        "\n".join(json.dumps(row, ensure_ascii=False) for row in rows) + "\n",
        encoding="utf-8",
    )


def build_ru_v6_2_corpus(
    output_dir: str | Path,
    *,
    seed: int = 42,
    write: bool = True,
) -> dict[str, object]:
    normalized_rows = [_normalize_row(row) for row in V6_2_ROWS]
    if len(normalized_rows) != EXPECTED_TOTAL:
        raise ValueError(f"v6.2 corpus must contain exactly {EXPECTED_TOTAL} rows")

    pair_keys = [_pair_key(row) for row in normalized_rows]
    if len(pair_keys) != len(set(pair_keys)):
        raise ValueError("v6.2 corpus contains exact duplicate prompt/response pairs")
    prompts = [str(row["messages"][-1]["content"]) for row in normalized_rows]
    if len(prompts) != len(set(prompts)):
        raise ValueError("v6.2 corpus contains duplicate final prompts")

    language_counts = dict(Counter(str(row["language"]) for row in normalized_rows))
    if language_counts != EXPECTED_LANGUAGE_COUNTS:
        raise ValueError(f"v6.2 language counts do not match the approved shape: {language_counts}")
    mode_counts = dict(Counter(str(row["mode"]) for row in normalized_rows))
    if mode_counts != EXPECTED_MODE_COUNTS:
        raise ValueError(f"v6.2 mode counts do not match the approved shape: {mode_counts}")
    intensity_counts = dict(Counter(str(row["intensity"]) for row in normalized_rows))
    if intensity_counts != EXPECTED_INTENSITY_COUNTS:
        raise ValueError(f"v6.2 intensity counts do not match the approved shape: {intensity_counts}")
    turn_band_counts = dict(Counter(str(row["turn_band"]) for row in normalized_rows))
    if turn_band_counts != EXPECTED_TURN_BAND_COUNTS:
        raise ValueError(f"v6.2 turn-band counts do not match the approved shape: {turn_band_counts}")

    contextual_count = sum(_is_contextual(row) for row in normalized_rows)
    if contextual_count < 1200:
        raise ValueError(f"v6.2 corpus needs at least 1200 contextual rows, found {contextual_count}")
    identity_repair_count = sum(str(row["mode"]) == "identity_repair" for row in normalized_rows)
    if identity_repair_count != EXPECTED_MODE_COUNTS["identity_repair"]:
        raise ValueError("v6.2 identity-repair count does not match the approved shape")
    long_context_count = sum(
        str(row["turn_band"]) == "long" and _has_context_signal(row) for row in normalized_rows
    )
    if long_context_count != EXPECTED_TURN_BAND_COUNTS["long"]:
        raise ValueError("v6.2 long rows must all retain an earlier context signal")

    shuffled_indices = list(range(len(normalized_rows)))
    Random(seed).shuffle(shuffled_indices)
    held_out_end = EXPECTED_SPLIT_COUNTS["held_out"]
    validation_end = held_out_end + EXPECTED_SPLIT_COUNTS["validation"]
    held_out = [normalized_rows[index] for index in shuffled_indices[:held_out_end]]
    validation = [normalized_rows[index] for index in shuffled_indices[held_out_end:validation_end]]
    train = [normalized_rows[index] for index in shuffled_indices[validation_end:]]
    splits = {"train": train, "validation": validation, "held_out": held_out}

    split_counts = {name: len(rows) for name, rows in splits.items()}
    if split_counts != EXPECTED_SPLIT_COUNTS:
        raise ValueError(f"v6.2 split counts do not match the approved shape: {split_counts}")
    split_prompt_sets = {
        name: {str(row["messages"][-1]["content"]) for row in rows}
        for name, rows in splits.items()
    }
    if any(
        split_prompt_sets[left] & split_prompt_sets[right]
        for left, right in (("train", "validation"), ("train", "held_out"), ("validation", "held_out"))
    ):
        raise ValueError("v6.2 final prompts overlap between splits")

    report: dict[str, object] = {
        "total_examples": len(normalized_rows),
        "split_counts": split_counts,
        "language_counts": language_counts,
        "mode_counts": mode_counts,
        "intensity_counts": intensity_counts,
        "turn_band_counts": turn_band_counts,
        "contextual_examples": contextual_count,
        "identity_repair_examples": identity_repair_count,
        "long_context_examples": long_context_count,
        "prompt_group_count": len(set(prompts)),
        "source_counts": dict(Counter(str(row["source"]) for row in normalized_rows)),
        "licenses": dict(Counter(str(row["license"]) for row in normalized_rows)),
        "source_sha256": _canonical_rows(normalized_rows),
        "train_sha256": _canonical_rows(train),
        "validation_sha256": _canonical_rows(validation),
        "held_out_sha256": _canonical_rows(held_out),
        "seed": seed,
        "quality_gates": {
            "rejects_links_code_markup_lists_ai_disclaimers": True,
            "rejects_vietnamese_self_reference": ["tôi", "tớ", "mình"],
            "rejects_graphic_or_unsafe_assistant_content": True,
            "requires_alternating_contextual_turns": True,
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
