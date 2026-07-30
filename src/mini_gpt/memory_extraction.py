from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import PureWindowsPath

from ._text_utils import normalize_text
from .memory import MemoryKind

_PATH_PATTERN = re.compile(r"\b[A-Za-z]:\\[^\s,.;!?]+")
_PREFERENCE_PATTERN = re.compile(
    r"\banh\s+(?:thích|muốn|ưu tiên)\s+(.+?)(?:\s+và\s+(?:dự án|project)\b|[.!?]|$)",
    re.IGNORECASE,
)
_SENSITIVE_PATTERN = re.compile(
    r"\b(?:api[ _-]?key|password|mật khẩu|token|secret|sk-[\w-]+|ghp_[\w-]+|số tài khoản|bệnh án)\b|https?://|\[\[VTS:",
    re.IGNORECASE,
)


@dataclass(frozen=True, slots=True)
class MemoryCandidate:
    kind: MemoryKind
    content: str
    scope: str | None
    evidence: str
    confidence: float
    importance: float


def strip_internal_metadata(text: str) -> str:
    return re.sub(r"\[\[VTS:.*?\]\]", "", text, flags=re.IGNORECASE).strip()


def is_sensitive_or_unsafe(text: str) -> bool:
    return not text.strip() or len(text) > 1000 or bool(_SENSITIVE_PATTERN.search(text))


def _scope_from(text: str) -> str | None:
    match = _PATH_PATTERN.search(text)
    if match is None:
        return None
    return str(PureWindowsPath(match.group(0)))


def extract_candidates(prompt: str, response: str) -> tuple[MemoryCandidate, ...]:
    raw_prompt = normalize_text(prompt)
    raw_response = normalize_text(response)
    if is_sensitive_or_unsafe(raw_prompt) or is_sensitive_or_unsafe(raw_response):
        return ()
    prompt = strip_internal_metadata(raw_prompt)
    preference = _PREFERENCE_PATTERN.search(prompt)
    candidates: list[MemoryCandidate] = []
    if preference is not None:
        content = preference.group(1).strip()
        if content:
            candidates.append(
                MemoryCandidate(
                    MemoryKind.PREFERENCE,
                    f"Anh prefers {content}.",
                    None,
                    prompt,
                    1.0,
                    0.8,
                )
            )
    scope = _scope_from(prompt)
    if scope is not None and re.search(r"\b(?:sẽ dùng|dùng|uses?|will use)\b", prompt, re.IGNORECASE):
        scope_lower = scope.lower()
        prompt_lower = prompt.lower()
        idx = prompt_lower.find(scope_lower)
        if idx >= 0:
            decision = prompt[idx:].strip().rstrip(".")
            candidates.append(MemoryCandidate(MemoryKind.DECISION, decision, scope, prompt, 1.0, 0.9))
    return tuple(candidates)
