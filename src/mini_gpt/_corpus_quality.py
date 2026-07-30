from __future__ import annotations

import re

from ._text_utils import normalize_text

MAX_PROMPT_CHARACTERS = 420
MAX_RESPONSE_CHARACTERS = 420
LIST_PATTERN = re.compile(r"(?m)^\s*(?:\d+\.|[-*])\s+")
AI_DISCLAIMER_PATTERN = re.compile(
    r"(?i)\b(as an ai language model|i cannot access|i do not have (?:the ability|a physical body)|i exist solely as)\b"
    r"|tôi là một (?:mô hình|người mẫu) ngôn ngữ ai|với tư cách là"
)


def casual_rejection_reason(prompt: str, response: str) -> str | None:
    prompt = normalize_text(prompt)
    response = normalize_text(response)
    combined = f"{prompt}\n{response}"
    if not prompt or len(response) < 2:
        return "too_short"
    if len(prompt) > MAX_PROMPT_CHARACTERS or len(response) > MAX_RESPONSE_CHARACTERS:
        return "too_long"
    if "```" in combined or re.search(r"<[^>]{1,80}>", combined):
        return "code_or_markup"
    if re.search(r"https?://|www\.", combined, re.IGNORECASE):
        return "link"
    if LIST_PATTERN.search(response):
        return "list_heavy"
    if AI_DISCLAIMER_PATTERN.search(response):
        return "ai_disclaimer"
    return None
