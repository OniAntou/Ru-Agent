from __future__ import annotations

from collections import Counter
from pathlib import Path
from random import Random

from ._base_corpus import (
    canonical_rows,
    is_contextual,
    pair_key,
    validate_alternating_roles,
    write_jsonl,
    write_splits_atomic,
)
from ._safety_patterns import (
    AI_DISCLAIMER as _AI_DISCLAIMER_PATTERN,
    CONTEXT_STOPWORDS as _CONTEXT_STOPWORDS,
    CONTEXT_TOKEN as _CONTEXT_TOKEN_PATTERN,
    EN_SIGNAL as _EN_SIGNAL_PATTERN,
    GRAPHIC_CONTENT as _GRAPHIC_PATTERN,
    SELF_REFERENCE_VOICE as _SELF_REFERENCE_PATTERN,
    UNSAFE_CONTENT as _UNSAFE_PATTERN,
    VI_SIGNAL as _VI_SIGNAL_PATTERN,
)
from ._text_utils import normalize_text
from .ru_qwen_corpus import casual_rejection_reason
from .ru_style import RU_SYSTEM_PROMPT
from .ru_v6_1_dialogues import (
    EXPECTED_INTENSITY_COUNTS,
    EXPECTED_LANGUAGE_COUNTS,
    EXPECTED_MODE_COUNTS,
    EXPECTED_TURN_BAND_COUNTS,
    LICENSE,
    SOURCE,
    V6_1_ROWS,
    V6_1_TOTAL_ROWS,
)

VERSION = "v6.1"
EXPECTED_TOTAL = V6_1_TOTAL_ROWS
EXPECTED_SPLIT_COUNTS = {"train": 1680, "validation": 360, "held_out": 360}
EXPECTED_LANGUAGE_COUNTS = dict(EXPECTED_LANGUAGE_COUNTS)
EXPECTED_MODE_COUNTS = dict(EXPECTED_MODE_COUNTS)
EXPECTED_INTENSITY_COUNTS = dict(EXPECTED_INTENSITY_COUNTS)
EXPECTED_TURN_BAND_COUNTS = dict(EXPECTED_TURN_BAND_COUNTS)

VALID_LANGUAGES = frozenset(EXPECTED_LANGUAGE_COUNTS)
VALID_MODES = frozenset(EXPECTED_MODE_COUNTS)
VALID_INTENSITIES = frozenset(EXPECTED_INTENSITY_COUNTS)
VALID_TURN_BANDS = frozenset(EXPECTED_TURN_BAND_COUNTS)


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
        raise ValueError(f"{VERSION} authored record has unexpected fields")

    raw_messages = raw_row["messages"]
    if not isinstance(raw_messages, list) or len(raw_messages) < 2:
        raise ValueError(f"{VERSION} authored record must contain system and conversation messages")

    messages: list[dict[str, str]] = []
    for raw_message in raw_messages:
        if not isinstance(raw_message, dict) or set(raw_message) != {"role", "content"}:
            raise ValueError(f"{VERSION} message must contain exactly role and content")
        role = raw_message["role"]
        content = raw_message["content"]
        if role not in {"system", "user", "assistant"} or not isinstance(content, str):
            raise ValueError(f"{VERSION} message has an unsupported role or content")
        content = normalize_text(content)
        if not content:
            raise ValueError(f"{VERSION} message content cannot be empty")
        messages.append({"role": role, "content": content})

    if messages[0] != {"role": "system", "content": RU_SYSTEM_PROMPT}:
        raise ValueError(f"{VERSION} record must start with the Ru system prompt")
    validate_alternating_roles(messages, VERSION)

    response = raw_row["response"]
    language = raw_row["language"]
    mode = raw_row["mode"]
    intensity = raw_row["intensity"]
    turn_band = raw_row["turn_band"]
    source = raw_row["source"]
    license_name = raw_row["license"]
    values = (response, language, mode, intensity, turn_band, source, license_name)
    if not all(isinstance(value, str) for value in values):
        raise ValueError(f"{VERSION} response metadata must be strings")

    response = normalize_text(response)
    if language not in VALID_LANGUAGES:
        raise ValueError(f"{VERSION} record has an unsupported language")
    if mode not in VALID_MODES:
        raise ValueError(f"{VERSION} record has an unsupported mode")
    if intensity not in VALID_INTENSITIES:
        raise ValueError(f"{VERSION} record has an unsupported intensity")
    if turn_band not in VALID_TURN_BANDS:
        raise ValueError(f"{VERSION} record has an unsupported turn band")
    if source != SOURCE or license_name != LICENSE:
        raise ValueError(f"{VERSION} record provenance is invalid")

    reason = casual_rejection_reason(messages[-1]["content"], response)
    if reason is not None:
        raise ValueError(f"{VERSION} authored record failed quality gate: {reason}")
    if _AI_DISCLAIMER_PATTERN.search(response):
        raise ValueError(f"{VERSION} authored record failed quality gate: ai_disclaimer")
    if _SELF_REFERENCE_PATTERN.search(response):
        raise ValueError(f"{VERSION} authored record failed quality gate: vietnamese_voice")
    if _GRAPHIC_PATTERN.search(response) or _UNSAFE_PATTERN.search(response):
        raise ValueError(f"{VERSION} authored record failed quality gate: explicit_or_unsafe_content")

    assistant_messages = [message["content"] for message in messages[1:-1] if message["role"] == "assistant"]
    if any(_SELF_REFERENCE_PATTERN.search(content) for content in assistant_messages):
        raise ValueError(f"{VERSION} authored record failed quality gate: vietnamese_voice_history")
    if any(_GRAPHIC_PATTERN.search(content) or _UNSAFE_PATTERN.search(content) for content in assistant_messages):
        raise ValueError(f"{VERSION} authored record failed quality gate: unsafe_history")

    user_turn_count = sum(message["role"] == "user" for message in messages[1:])
    expected_turn_ranges = {
        "short": range(2, 4),
        "medium": range(4, 9),
        "long": range(9, 15),
    }
    if user_turn_count not in expected_turn_ranges[turn_band]:
        raise ValueError(f"{VERSION} record turn count does not match its turn band")
    if turn_band == "long" and not _has_context_signal({"messages": messages, "response": response}):
        raise ValueError(f"{VERSION} authored record failed quality gate: long_context_missing")

    if language == "en" and not _EN_SIGNAL_PATTERN.search(response):
        raise ValueError(f"{VERSION} authored record failed quality gate: english_signal_missing")
    if language == "mixed" and (
        not _VI_SIGNAL_PATTERN.search(response) or not _EN_SIGNAL_PATTERN.search(response)
    ):
        raise ValueError(f"{VERSION} authored record failed quality gate: mixed_language")
    if language == "vi" and not _VI_SIGNAL_PATTERN.search(response):
        raise ValueError(f"{VERSION} authored record failed quality gate: vietnamese_signal_missing")

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


def build_ru_v6_1_corpus(
    output_dir: str | Path,
    *,
    seed: int = 42,
    write: bool = True,
) -> dict[str, object]:
    normalized_rows = [_normalize_row(row) for row in V6_1_ROWS]
    if len(normalized_rows) != EXPECTED_TOTAL:
        raise ValueError(f"{VERSION} corpus must contain exactly {EXPECTED_TOTAL} rows")

    pair_keys = [pair_key(row) for row in normalized_rows]
    if len(pair_keys) != len(set(pair_keys)):
        raise ValueError(f"{VERSION} corpus contains exact duplicate prompt/response pairs")
    prompts = [str(row["messages"][-1]["content"]) for row in normalized_rows]
    if len(prompts) != len(set(prompts)):
        raise ValueError(f"{VERSION} corpus contains duplicate final prompts")

    language_counts = dict(Counter(str(row["language"]) for row in normalized_rows))
    if language_counts != EXPECTED_LANGUAGE_COUNTS:
        raise ValueError(f"{VERSION} language counts do not match the approved shape: {language_counts}")
    mode_counts = dict(Counter(str(row["mode"]) for row in normalized_rows))
    if mode_counts != EXPECTED_MODE_COUNTS:
        raise ValueError(f"{VERSION} mode counts do not match the approved shape: {mode_counts}")
    intensity_counts = dict(Counter(str(row["intensity"]) for row in normalized_rows))
    if intensity_counts != EXPECTED_INTENSITY_COUNTS:
        raise ValueError(f"{VERSION} intensity counts do not match the approved shape: {intensity_counts}")
    turn_band_counts = dict(Counter(str(row["turn_band"]) for row in normalized_rows))
    if turn_band_counts != EXPECTED_TURN_BAND_COUNTS:
        raise ValueError(f"{VERSION} turn-band counts do not match the approved shape: {turn_band_counts}")

    contextual_count = sum(is_contextual(row, check_history_only=True) for row in normalized_rows)
    if contextual_count < 1200:
        raise ValueError(f"{VERSION} corpus needs at least 1200 contextual rows, found {contextual_count}")
    identity_repair_count = sum(str(row["mode"]) == "identity_repair" for row in normalized_rows)
    if identity_repair_count != EXPECTED_MODE_COUNTS["identity_repair"]:
        raise ValueError(f"{VERSION} identity-repair count does not match the approved shape")
    long_context_count = sum(
        str(row["turn_band"]) == "long" and _has_context_signal(row) for row in normalized_rows
    )
    if long_context_count != EXPECTED_TURN_BAND_COUNTS["long"]:
        raise ValueError(f"{VERSION} long rows must all retain an earlier context signal")

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
        raise ValueError(f"{VERSION} split counts do not match the approved shape: {split_counts}")
    split_prompt_sets = {
        name: {str(row["messages"][-1]["content"]) for row in rows}
        for name, rows in splits.items()
    }
    if any(
        split_prompt_sets[left] & split_prompt_sets[right]
        for left, right in (("train", "validation"), ("train", "held_out"), ("validation", "held_out"))
    ):
        raise ValueError(f"{VERSION} final prompts overlap between splits")

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
        "source_sha256": canonical_rows(normalized_rows),
        "train_sha256": canonical_rows(train),
        "validation_sha256": canonical_rows(validation),
        "held_out_sha256": canonical_rows(held_out),
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

    write_splits_atomic(Path(output_dir), train, validation, held_out, report)
    return report
