from __future__ import annotations

from collections import Counter, defaultdict
import re
from typing import Iterable

from ._text_utils import normalize_text


_AI_META = re.compile(
    r"(?i)\b(?:as an ai|ai language model|language model|ai-generated|generated response)\b"
)
_MAX_OPENING_RATIO = 0.15


def normalized_final_prompt(row: dict[str, object]) -> str:
    messages = row.get("messages")
    if not isinstance(messages, list) or not messages:
        raise ValueError("row must contain messages")
    final_message = messages[-1]
    if not isinstance(final_message, dict):
        raise ValueError("final message must be an object")
    prompt = final_message.get("content")
    if not isinstance(prompt, str):
        raise ValueError("final message content must be text")
    return normalize_text(prompt).casefold()


def naturalness_rejection_reason(prompt: str, response: str) -> str | None:
    if not normalize_text(response):
        return "empty_response"
    if _AI_META.search(response):
        return "ai_meta_language"
    if ".." in response:
        return "repeated_punctuation"
    if re.search(r"(?i)(?<!\w)anh\s+anh(?!\w)", response):
        return "duplicated_address"
    return None


def _response_opening(response: str) -> str:
    words = re.findall(r"[^\W_]+", normalize_text(response).casefold(), flags=re.UNICODE)
    return " ".join(words[:2])


def audit_rows(rows: Iterable[dict[str, object]]) -> dict[str, object]:
    materialized = list(rows)
    prompt_counts: Counter[str] = Counter()
    response_counts: Counter[str] = Counter()
    opening_counts: Counter[str] = Counter()
    language_counts: Counter[str] = Counter()
    mode_counts: Counter[str] = Counter()
    turn_band_counts: Counter[str] = Counter()
    samples: dict[tuple[str, str, str], dict[str, object]] = {}
    rejections: list[dict[str, object]] = []

    for index, row in enumerate(materialized):
        prompt = normalized_final_prompt(row)
        response = str(row.get("response", ""))
        prompt_counts[prompt] += 1
        response_counts[normalize_text(response).casefold()] += 1
        opening = _response_opening(response)
        if opening:
            opening_counts[opening] += 1
        reason = naturalness_rejection_reason(prompt, response)
        if reason is not None:
            rejections.append({"index": index, "reason": reason, "prompt": prompt, "response": response})

        language = str(row.get("language", "unknown"))
        mode = str(row.get("mode", "unknown"))
        turn_band = str(row.get("turn_band", "unknown"))
        language_counts[language] += 1
        mode_counts[mode] += 1
        turn_band_counts[turn_band] += 1
        samples.setdefault(
            (language, mode, turn_band),
            {"index": index, "prompt": prompt, "response": response},
        )

    collisions = sorted(prompt for prompt, count in prompt_counts.items() if count > 1)
    repeated_responses = sorted(response for response, count in response_counts.items() if response and count > 1)
    largest_opening_count = max(opening_counts.values(), default=0)
    return {
        "summary": {
            "row_count": len(materialized),
            "rejection_count": len(rejections),
            "casefold_prompt_collisions": collisions,
            "casefold_prompt_collision_count": len(collisions),
            "repeated_response_count": len(repeated_responses),
            "largest_response_opening_count": largest_opening_count,
            "largest_response_opening_ratio": (
                largest_opening_count / len(materialized) if materialized else 0.0
            ),
            "language_counts": dict(sorted(language_counts.items())),
            "mode_counts": dict(sorted(mode_counts.items())),
            "turn_band_counts": dict(sorted(turn_band_counts.items())),
            "response_openings": dict(opening_counts.most_common(20)),
        },
        "rejections": rejections,
        "repeated_responses": repeated_responses,
        "samples": [
            {"language": key[0], "mode": key[1], "turn_band": key[2], **sample}
            for key, sample in sorted(samples.items())
        ],
    }


def assert_authored_rows_are_natural(rows: Iterable[dict[str, object]]) -> None:
    audit = audit_rows(rows)
    summary = audit["summary"]
    if summary["rejection_count"]:
        reasons = sorted({str(item["reason"]) for item in audit["rejections"]})
        raise ValueError(f"v6.1 corpus contains unnatural responses: {', '.join(reasons)}")
    if summary["casefold_prompt_collision_count"]:
        raise ValueError("v6.1 corpus contains casefold duplicate final prompts")
    if summary["repeated_response_count"]:
        raise ValueError("v6.1 corpus contains repeated full responses")
    if summary["largest_response_opening_ratio"] > _MAX_OPENING_RATIO:
        raise ValueError("v6.1 corpus overuses one response opening")
