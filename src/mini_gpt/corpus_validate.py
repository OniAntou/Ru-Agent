from __future__ import annotations

from collections import Counter
from pathlib import Path
from random import Random

from ._base_corpus import (
    canonical_rows,
    is_contextual,
    pair_key,
    validate_alternating_roles,
    write_splits_atomic,
)
from ._corpus_quality import casual_rejection_reason
from ._safety_patterns import (
    AI_DISCLAIMER as _AI_DISCLAIMER_PATTERN,
    CONTEXT_STOPWORDS as _CONTEXT_STOPWORDS,
    CONTEXT_TOKEN as _CONTEXT_TOKEN_PATTERN,
    EN_SIGNAL as _EN_SIGNAL_PATTERN,
    GRAPHIC_CONTENT as _GRAPHIC_PATTERN,
    VI_WRONG_SELF as _SELF_REFERENCE_PATTERN,
    UNSAFE_CONTENT as _UNSAFE_PATTERN,
    VI_SIGNAL as _VI_SIGNAL_PATTERN,
)
from ._text_utils import normalize_text
from .ru_style import ru_system_prompt_for
from .ru_v6_1_naturalness import (
    assert_authored_rows_are_natural,
    audit_rows,
    normalized_final_prompt,
)


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


def _expected_turn_ranges() -> dict[str, range]:
    return {
        "short": range(2, 4),
        "medium": range(4, 9),
        "long": range(9, 15),
    }


def normalize_row(
    raw_row: dict[str, object],
    *,
    version: str,
    expected_fields: frozenset[str],
    valid_languages: frozenset[str],
    valid_modes: frozenset[str],
    valid_intensities: frozenset[str],
    valid_turn_bands: frozenset[str],
    source: str,
    license_name: str,
    system_prompt: str | None = None,
    allow_approved: bool = False,
    approved_source: str | None = None,
) -> dict[str, object]:
    is_approved = "approval_id" in raw_row
    if is_approved and not allow_approved:
        raise ValueError(f"{version} authored record has unexpected fields")
    if set(raw_row) != (expected_fields | ({"approval_id"} if is_approved else set())):
        raise ValueError(f"{version} authored record has unexpected fields")

    raw_messages = raw_row["messages"]
    if not isinstance(raw_messages, list) or len(raw_messages) < 2:
        raise ValueError(f"{version} authored record must contain system and conversation messages")

    messages: list[dict[str, str]] = []
    for raw_message in raw_messages:
        if not isinstance(raw_message, dict) or set(raw_message) != {"role", "content"}:
            raise ValueError(f"{version} message must contain exactly role and content")
        role = raw_message["role"]
        content = raw_message["content"]
        if role not in {"system", "user", "assistant"} or not isinstance(content, str):
            raise ValueError(f"{version} message has an unsupported role or content")
        content = normalize_text(content)
        if not content:
            raise ValueError(f"{version} message content cannot be empty")
        messages.append({"role": role, "content": content})

    response = raw_row["response"]
    language = raw_row["language"]
    mode = raw_row["mode"]
    intensity = raw_row["intensity"]
    turn_band = raw_row["turn_band"]
    source_value = raw_row["source"]
    row_license = raw_row["license"]
    values = (response, language, mode, intensity, turn_band, source_value, row_license)
    if not all(isinstance(value, str) for value in values):
        raise ValueError(f"{version} response metadata must be strings")

    response = normalize_text(response)
    if language not in valid_languages:
        raise ValueError(f"{version} record has an unsupported language")
    if messages[0] != {"role": "system", "content": system_prompt or ru_system_prompt_for(language)}:
        raise ValueError(f"{version} record must start with the Ru system prompt")
    validate_alternating_roles(messages, version)
    if mode not in valid_modes:
        raise ValueError(f"{version} record has an unsupported mode")
    if intensity not in valid_intensities:
        raise ValueError(f"{version} record has an unsupported intensity")
    if turn_band not in valid_turn_bands:
        raise ValueError(f"{version} record has an unsupported turn band")
    if not (source_value == source or (is_approved and source_value == approved_source)) or row_license != license_name:
        raise ValueError(f"{version} record provenance is invalid")

    if is_approved:
        approval_id = raw_row["approval_id"]
        if not isinstance(approval_id, str) or not approval_id:
            raise ValueError(f"{version} approved record has an invalid approval id")
        return {
            "messages": messages,
            "response": response,
            "language": language,
            "mode": mode,
            "intensity": intensity,
            "turn_band": turn_band,
            "source": source_value,
            "license": row_license,
            "approval_id": approval_id,
        }

    reason = casual_rejection_reason(messages[-1]["content"], response)
    if reason is not None:
        raise ValueError(f"{version} authored record failed quality gate: {reason}")
    if _AI_DISCLAIMER_PATTERN.search(response):
        raise ValueError(f"{version} authored record failed quality gate: ai_disclaimer")
    if _SELF_REFERENCE_PATTERN.search(response):
        raise ValueError(f"{version} authored record failed quality gate: vietnamese_voice")
    if _GRAPHIC_PATTERN.search(response) or _UNSAFE_PATTERN.search(response):
        raise ValueError(f"{version} authored record failed quality gate: explicit_or_unsafe_content")

    assistant_messages = [message["content"] for message in messages[1:-1] if message["role"] == "assistant"]
    if any(_SELF_REFERENCE_PATTERN.search(content) for content in assistant_messages):
        raise ValueError(f"{version} authored record failed quality gate: vietnamese_voice_history")
    if any(_GRAPHIC_PATTERN.search(content) or _UNSAFE_PATTERN.search(content) for content in assistant_messages):
        raise ValueError(f"{version} authored record failed quality gate: unsafe_history")

    user_turn_count = sum(message["role"] == "user" for message in messages[1:])
    if user_turn_count not in _expected_turn_ranges()[turn_band]:
        raise ValueError(f"{version} record turn count does not match its turn band")
    if turn_band == "long" and not _has_context_signal({"messages": messages, "response": response}):
        raise ValueError(f"{version} authored record failed quality gate: long_context_missing")

    if language == "en" and not _EN_SIGNAL_PATTERN.search(response):
        raise ValueError(f"{version} authored record failed quality gate: english_signal_missing")
    if language == "mixed" and (
        not _VI_SIGNAL_PATTERN.search(response) or not _EN_SIGNAL_PATTERN.search(response)
    ):
        raise ValueError(f"{version} authored record failed quality gate: mixed_language")
    if language == "vi" and not _VI_SIGNAL_PATTERN.search(response):
        raise ValueError(f"{version} authored record failed quality gate: vietnamese_signal_missing")

    return {
        "messages": messages,
        "response": response,
        "language": language,
        "mode": mode,
        "intensity": intensity,
        "turn_band": turn_band,
        "source": source_value,
        "license": row_license,
    }


def build_corpus(
    rows: list[dict[str, object]],
    *,
    output_dir: str | Path,
    version: str,
    expected_total: int,
    expected_split_counts: dict[str, int],
    expected_language_counts: dict[str, int],
    expected_mode_counts: dict[str, int],
    expected_intensity_counts: dict[str, int],
    expected_turn_band_counts: dict[str, int],
    valid_languages: frozenset[str],
    valid_modes: frozenset[str],
    valid_intensities: frozenset[str],
    valid_turn_bands: frozenset[str],
    source: str,
    license_name: str,
    system_prompt: str | None = None,
    allow_approved: bool = False,
    approved_source: str | None = None,
    naturalness_gate: bool = False,
    casefold_prompts: bool = False,
    seed: int,
    write: bool = True,
) -> dict[str, object]:
    normalized_rows = [
        normalize_row(
            row,
            version=version,
            expected_fields=frozenset(
                {"messages", "response", "language", "mode", "intensity", "turn_band", "source", "license"}
            ),
            valid_languages=valid_languages,
            valid_modes=valid_modes,
            valid_intensities=valid_intensities,
            valid_turn_bands=valid_turn_bands,
            source=source,
            license_name=license_name,
            system_prompt=system_prompt,
            allow_approved=allow_approved,
            approved_source=approved_source,
        )
        for row in rows
    ]
    if len(normalized_rows) != expected_total:
        raise ValueError(f"{version} corpus must contain exactly {expected_total} rows")
    if naturalness_gate:
        assert_authored_rows_are_natural(normalized_rows)

    pair_keys = [pair_key(row) for row in normalized_rows]
    if len(pair_keys) != len(set(pair_keys)):
        raise ValueError(f"{version} corpus contains exact duplicate prompt/response pairs")
    if casefold_prompts:
        prompts = [normalized_final_prompt(row) for row in normalized_rows]
    else:
        prompts = [str(row["messages"][-1]["content"]) for row in normalized_rows]
    if len(prompts) != len(set(prompts)):
        raise ValueError(f"{version} corpus contains duplicate final prompts")

    language_counts = dict(Counter(str(row["language"]) for row in normalized_rows))
    if language_counts != expected_language_counts:
        raise ValueError(f"{version} language counts do not match the approved shape: {language_counts}")
    mode_counts = dict(Counter(str(row["mode"]) for row in normalized_rows))
    if mode_counts != expected_mode_counts:
        raise ValueError(f"{version} mode counts do not match the approved shape: {mode_counts}")
    intensity_counts = dict(Counter(str(row["intensity"]) for row in normalized_rows))
    if intensity_counts != expected_intensity_counts:
        raise ValueError(f"{version} intensity counts do not match the approved shape: {intensity_counts}")
    turn_band_counts = dict(Counter(str(row["turn_band"]) for row in normalized_rows))
    if turn_band_counts != expected_turn_band_counts:
        raise ValueError(f"{version} turn-band counts do not match the approved shape: {turn_band_counts}")

    contextual_count = sum(is_contextual(row, check_history_only=True) for row in normalized_rows)
    if contextual_count < 1200:
        raise ValueError(f"{version} corpus needs at least 1200 contextual rows, found {contextual_count}")
    identity_repair_count = sum(str(row["mode"]) == "identity_repair" for row in normalized_rows)
    if identity_repair_count != expected_mode_counts["identity_repair"]:
        raise ValueError(f"{version} identity-repair count does not match the approved shape")
    long_context_count = sum(
        str(row["turn_band"]) == "long" and _has_context_signal(row) for row in normalized_rows
    )
    if long_context_count != expected_turn_band_counts["long"]:
        raise ValueError(f"{version} long rows must all retain an earlier context signal")

    shuffled_indices = list(range(len(normalized_rows)))
    Random(seed).shuffle(shuffled_indices)
    held_out_end = expected_split_counts["held_out"]
    validation_end = held_out_end + expected_split_counts["validation"]
    held_out = [normalized_rows[index] for index in shuffled_indices[:held_out_end]]
    validation = [normalized_rows[index] for index in shuffled_indices[held_out_end:validation_end]]
    train = [normalized_rows[index] for index in shuffled_indices[validation_end:]]
    splits = {"train": train, "validation": validation, "held_out": held_out}

    split_counts = {name: len(rows) for name, rows in splits.items()}
    if split_counts != expected_split_counts:
        raise ValueError(f"{version} split counts do not match the approved shape: {split_counts}")
    split_prompt_sets = {
        name: {prompt for prompt in map(lambda row: normalized_final_prompt(row) if casefold_prompts else str(row["messages"][-1]["content"]), rows)}
        for name, rows in splits.items()
    }
    if any(
        split_prompt_sets[left] & split_prompt_sets[right]
        for left, right in (("train", "validation"), ("train", "held_out"), ("validation", "held_out"))
    ):
        raise ValueError(f"{version} final prompts overlap between splits")

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
    if naturalness_gate:
        report["naturalness_audit"] = audit_rows(normalized_rows)["summary"]

    if not write:
        return report

    write_splits_atomic(Path(output_dir), train, validation, held_out, report)
    return report
