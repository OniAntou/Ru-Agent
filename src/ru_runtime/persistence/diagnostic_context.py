from __future__ import annotations

import contextlib
import contextvars
import re
import uuid
from typing import Generator, Optional

VALID_CORRELATION_PATTERN = re.compile(r"^[a-zA-Z0-9_-]{8,64}$")

_current_correlation_id: contextvars.ContextVar[Optional[str]] = contextvars.ContextVar(
    "current_correlation_id", default=None
)


def generate_correlation_id() -> str:
    return f"c_{uuid.uuid4().hex}"


def is_valid_correlation_id(candidate: Optional[str]) -> bool:
    if not candidate or not isinstance(candidate, str):
        return False
    candidate = candidate.strip()
    if len(candidate) < 8 or len(candidate) > 64:
        return False
    return bool(VALID_CORRELATION_PATTERN.match(candidate))


def sanitize_correlation_id(candidate: Optional[str]) -> str:
    if is_valid_correlation_id(candidate):
        return candidate.strip()  # type: ignore[union-attr]
    return generate_correlation_id()


def get_correlation_id() -> Optional[str]:
    return _current_correlation_id.get()


def set_correlation_id(correlation_id: Optional[str]) -> contextvars.Token[Optional[str]]:
    sanitized = sanitize_correlation_id(correlation_id) if correlation_id else None
    return _current_correlation_id.set(sanitized)


@contextlib.contextmanager
def correlation_scope(correlation_id: Optional[str]) -> Generator[Optional[str], None, None]:
    token = set_correlation_id(correlation_id)
    try:
        yield get_correlation_id()
    finally:
        _current_correlation_id.reset(token)
