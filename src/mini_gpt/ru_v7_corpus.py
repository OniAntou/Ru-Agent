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
from .ru_style import RU_SYSTEM_PROMPT
from .ru_v7_dialogues import LICENSE, SOURCE, V7_LANGUAGE_COUNTS, V7_MODE_COUNTS, V7_TOTAL_ROWS

SPLIT_COUNTS = {"train": 135, "validation": 22, "held_out": 23}
VALID_LANGUAGES = frozenset(V7_LANGUAGE_COUNTS)
VALID_MODES = frozenset(V7_MODE_COUNTS)
VALID_FLIRT_LEVELS = frozenset({"none", "light"})
MAX_PROMPT_CHARACTERS = 420
MAX_RESPONSE_CHARACTERS = 420
MAX_LIGHT_FLIRT_ROWS = 18

_LIST_PATTERN = re.compile(r"(?m)^\s*(?:\d+\.|[-*])\s+")
_AI_DISCLAIMER_PATTERN = re.compile(
    r"(?i)\b(?:as an ai language model|i cannot access|i do not have (?:the ability|a physical body))\b"
    r"|(?:tôi|mình) là một (?:mô hình|người mẫu) ngôn ngữ ai|với tư cách là"
)
_GENERIC_REFUSAL_PATTERN = re.compile(
    r"(?i)\b(?:i\s+(?:cannot|can't)\s+(?:help|create|do)|i\s+cannot)\b"
    r"|\b(?:em không thể|em không hiểu|tôi không thể)\b"
)
_STAGE_DIRECTION_PATTERN = re.compile(r"\*[^*]{1,80}\*|\b(?:nya|ah~)\b", re.IGNORECASE)
_MINOR_PATTERN = re.compile(
    r"(?i)\b(?:child|children|minor|underage|kid|kids)\b|(?:trẻ\s*(?:em|con)|vị thành niên|con nít)"
)
_GRAPHIC_PATTERN = re.compile(
    r"(?i)\b(?:fuck|fucking|cock|pussy|tits|boobs|cum|semen|orgasm|moan|rape|anal|dick)\b"
    r"|(?:đụ|địt|cặc|lồn|vú|tinh dịch|xuất tinh|hiếp dâm|rên rỉ|làm tình)"
)
_VIOLENCE_PATTERN = re.compile(
    r"(?i)\b(?:kill|murder|slice|slash|castrate|choke|violent|violence|torture|whip|stab)\b"
    r"|(?:giết|cắt cổ|thiến|bóp cổ|bạo lực|tra tấn|đánh đập|chém|đâm)"
)
_POSSESSIVE_PATTERN = re.compile(
    r"(?i)\b(?:you belong to me|only mine|my property|no one else)\b"
    r"|(?:chỉ được phép|của riêng em|không được nhìn ai khác|em sẽ ghen|đừng rời em)"
)
_HARMFUL_INSTRUCTION_PATTERN = re.compile(
    r"(?i)\b(?:make a bomb|build a weapon|poison|self-harm|suicide)\b"
    r"|(?:chế tạo bom|chế tạo vũ khí|đầu độc|tự sát|tự hại)"
)
_VI_SELF_PATTERN = re.compile(r"(?i)\b(?:tôi|mình)\b")
_VI_USER_PATTERN = re.compile(r"(?i)\bankh\b|\banh\b")
_EN_SELF_PATTERN = re.compile(r"(?i)\b(?:i|i'm|i’ve|i've|me|my)\b")
_EN_USER_PATTERN = re.compile(r"(?i)\b(?:you|your)\b")


def _canonical_rows(rows: list[dict[str, object]]) -> str:
    serialized = "\n".join(json.dumps(row, ensure_ascii=False, sort_keys=True, separators=(",", ":")) for row in rows)
    return sha256(serialized.encode("utf-8")).hexdigest()


def _is_contextual(row: dict[str, object]) -> bool:
    messages = row["messages"]
    assert isinstance(messages, list)
    return any(message["role"] == "assistant" for message in messages[:-1])


def _conversation_text(messages: list[dict[str, str]]) -> str:
    return "\n".join(message["content"] for message in messages[1:])


def _self_focus_reason(response: str, language: str) -> str | None:
    if language == "vi":
        self_count = len(_VI_SELF_PATTERN.findall(response))
        user_signal = _VI_USER_PATTERN.search(response)
    elif language == "en":
        self_count = len(_EN_SELF_PATTERN.findall(response))
        user_signal = _EN_USER_PATTERN.search(response)
    else:
        self_count = len(_VI_SELF_PATTERN.findall(response)) + len(_EN_SELF_PATTERN.findall(response))
        user_signal = _VI_USER_PATTERN.search(response) or _EN_USER_PATTERN.search(response)
    if self_count >= 3 and user_signal is None:
        return "self_focus"
    return None


def _quality_reason(
    messages: list[dict[str, str]],
    response: str,
    language: str,
    mode: str,
    flirt_level: str,
) -> str | None:
    prompt = messages[-1]["content"]
    combined = f"{_conversation_text(messages)}\n{response}"
    if not prompt or len(response) < 12:
        return "too_short"
    if len(prompt) > MAX_PROMPT_CHARACTERS or len(response) > MAX_RESPONSE_CHARACTERS:
        return "too_long"
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
    self_focus = _self_focus_reason(response, language)
    if self_focus is not None:
        return self_focus
    if language == "vi" and re.search(r"(?i)\btôi\b|(?<!một )\bmình\b|(?<!người )\bbạn\b(?!\s+anh\b)", response):
        return "vietnamese_voice"
    if flirt_level not in VALID_FLIRT_LEVELS:
        return "invalid_flirt_level"
    if mode == "follow_up" and "?" not in response:
        return "follow_up_without_question"
    return None


def _normalize_row(raw_row: dict[str, object]) -> dict[str, object]:
    expected_fields = {"messages", "response", "language", "mode", "source", "license", "flirt_level"}
    if set(raw_row) != expected_fields:
        raise ValueError("V7 record has unexpected fields")
    raw_messages = raw_row["messages"]
    if not isinstance(raw_messages, list) or len(raw_messages) < 2:
        raise ValueError("V7 record must contain system and conversation messages")
    messages: list[dict[str, str]] = []
    for raw_message in raw_messages:
        if not isinstance(raw_message, dict) or set(raw_message) != {"role", "content"}:
            raise ValueError("V7 message must contain role and content")
        role, content = raw_message["role"], raw_message["content"]
        if role not in {"system", "user", "assistant"} or not isinstance(content, str):
            raise ValueError("V7 message has an unsupported role or content")
        content = normalize_text(content)
        if not content:
            raise ValueError("V7 message content cannot be empty")
        messages.append({"role": role, "content": content})
    if messages[0] != {"role": "system", "content": RU_SYSTEM_PROMPT}:
        raise ValueError("V7 record must start with the Ru system prompt")
    turn_roles = [message["role"] for message in messages[1:]]
    if turn_roles[0] != "user" or turn_roles[-1] != "user" or any(left == right for left, right in zip(turn_roles, turn_roles[1:])):
        raise ValueError("V7 conversation roles must alternate and end with user")
    response = raw_row["response"]
    language = raw_row["language"]
    mode = raw_row["mode"]
    source = raw_row["source"]
    license_name = raw_row["license"]
    flirt_level = raw_row["flirt_level"]
    if not all(isinstance(value, str) for value in (response, language, mode, source, license_name, flirt_level)):
        raise ValueError("V7 response metadata must be strings")
    response = normalize_text(response)
    if language not in VALID_LANGUAGES:
        raise ValueError("V7 record has an unsupported language")
    if mode not in VALID_MODES:
        raise ValueError("V7 record has an unsupported response mode")
    if source != SOURCE or license_name != LICENSE:
        raise ValueError("V7 record provenance must be teacher-curated project-owned")
    reason = _quality_reason(messages, response, language, mode, flirt_level)
    if reason is not None:
        raise ValueError(f"V7 record failed quality gate: {reason}")
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
    path.write_text("\n".join(json.dumps(row, ensure_ascii=False) for row in rows) + "\n", encoding="utf-8")


def build_ru_v7_corpus(rows: list[dict[str, object]], output_dir: str | Path, seed: int = 42) -> dict[str, object]:
    normalized = [_normalize_row(row) for row in rows]
    if len(normalized) != V7_TOTAL_ROWS:
        raise ValueError(f"V7 corpus must contain exactly {V7_TOTAL_ROWS} rows")
    if Counter(str(row["language"]) for row in normalized) != Counter(V7_LANGUAGE_COUNTS):
        raise ValueError("V7 corpus language counts do not match the approved shape")
    if Counter(str(row["mode"]) for row in normalized) != Counter(V7_MODE_COUNTS):
        raise ValueError("V7 corpus mode counts do not match the approved shape")
    if sum(str(row["flirt_level"]) == "light" for row in normalized) > MAX_LIGHT_FLIRT_ROWS:
        raise ValueError("V7 corpus contains too many light-flirt rows")
    contextual_examples = sum(_is_contextual(row) for row in normalized)
    if contextual_examples < 6:
        raise ValueError("V7 corpus must contain at least six contextual conversations")
    prompts = [str(row["messages"][-1]["content"]) for row in normalized]
    if len(set(prompts)) != len(prompts):
        raise ValueError("V7 corpus contains duplicate final prompts")
    keys = [json.dumps({"messages": row["messages"], "response": row["response"]}, ensure_ascii=False, sort_keys=True) for row in normalized]
    if len(set(keys)) != len(keys):
        raise ValueError("V7 corpus contains duplicate conversations")

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
        "mode_counts": dict(Counter(str(row["mode"]) for row in normalized)),
        "flirt_counts": dict(Counter(str(row["flirt_level"]) for row in normalized)),
        "contextual_examples": contextual_examples,
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
