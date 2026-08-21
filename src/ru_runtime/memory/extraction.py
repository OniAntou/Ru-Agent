from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import PureWindowsPath

from ru_runtime.policy.text import normalize_text
from .store import MemoryKind

# Shared with StructuredMemoryStore.retrieve() in memory.py (inline pattern there).
_PATH_PATTERN = re.compile(r"\b[A-Za-z]:\\[^\s,.;!?]+")
_MAX_DECISION_CHARS = 500
_USE_PATTERN = re.compile(r"\b(?:sẽ dùng|dùng|uses?|will use)\b\s+(?=[^\s.,;!?])", re.IGNORECASE)
_SENSITIVE_PATTERN = re.compile(
    r"\b(?:api[ _-]?key|password|mật khẩu|token|secret|bearer|authorization|credential|"
    r"passphrase|access[ _-]?key|số tài khoản|bệnh án)\b|"
    r"(?:sk|pk|ghp|gho|github_pat)_[A-Za-z0-9_-]+|"
    r"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+|"
    r"https?://|\[\[(?:VTS|EMO|VOICE_PACE):|\[(?:SEARCH|APP_OPEN|VOLUME):",
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
    return re.sub(
        r"\[\[VTS:[^\]]*\]\]|\[(?:SEARCH|APP_OPEN|VOLUME):[^\]]*\]",
        "",
        text,
        flags=re.IGNORECASE,
    ).strip()


def is_sensitive_or_unsafe(text: str) -> bool:
    # Length alone is never "sensitive": gating on length silently disabled
    # self-learning for every reply longer than 1000 characters. Truncation
    # happens at the extraction boundary instead.
    return not text.strip() or bool(_SENSITIVE_PATTERN.search(text))


def _scope_from(text: str) -> str | None:
    match = _PATH_PATTERN.search(text)
    if match is None:
        return None
    return str(PureWindowsPath(match.group(0)))


def _preference_pattern(address_name: str) -> re.Pattern[str]:
    # "Em trai anh thích bóng đá" is about the brother, not the user: reject a
    # possessive chain right before the address name ("trai anh", "bạn anh").
    return re.compile(
        rf"(?<!\btrai\s)(?<!\bbạn\s)\b{re.escape(address_name)}\s+(?:thích|muốn|ưu tiên)\s+(.{{1,200}}?)(?:\s+và\s+(?:dự án|project)\b|[.!?]|$)",
        re.IGNORECASE,
    )


def extract_candidates(prompt: str, response: str, *, address_name: str = "anh") -> tuple[MemoryCandidate, ...]:
    prompt = strip_internal_metadata(normalize_text(prompt))
    response = strip_internal_metadata(normalize_text(response))
    if is_sensitive_or_unsafe(prompt) or is_sensitive_or_unsafe(response):
        return ()
    preference = _preference_pattern(address_name).search(prompt)
    candidates: list[MemoryCandidate] = []
    if preference is not None:
        content = preference.group(1).strip()
        if content:
            candidates.append(
                MemoryCandidate(
                    MemoryKind.PREFERENCE,
                    f"{address_name.capitalize()} thích {content}.",
                    None,
                    prompt,
                    1.0,
                    0.8,
                )
            )
    scope = _scope_from(prompt)
    if scope is not None and _USE_PATTERN.search(prompt):
        scope_lower = scope.lower()
        prompt_lower = prompt.lower()
        idx = prompt_lower.find(scope_lower)
        if idx >= 0:
            decision = prompt[idx:].strip().rstrip(".")[:_MAX_DECISION_CHARS]
            candidates.append(MemoryCandidate(MemoryKind.DECISION, decision, scope, prompt, 1.0, 0.9))
    return tuple(candidates)
