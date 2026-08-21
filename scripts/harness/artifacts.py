"""Privacy-safe schema-v2 artifact construction for Harness runs."""

from __future__ import annotations

import math
import re
from collections.abc import Iterable, Mapping, Sequence
from typing import Any

from . import HARNESS_VERSION
from .model import CheckResult

_MAX_STRING_LENGTH = 512
_MAX_CONTAINER_ITEMS = 256
_MAX_NESTING = 8
_UNSAFE_KEY_PARTS = (
    "authorization",
    "bearer",
    "token",
    "secret",
    "password",
    "request_body",
    "response_body",
    "body",
    "prompt",
    "transcript",
    "audio",
    "binary",
    "payload",
)
_BEARER_PATTERN = re.compile(r"\bbearer\s+[^\s,;]+", re.IGNORECASE)
_BASE64_BINARY_PATTERN = re.compile(r"^[A-Za-z0-9+/]{128,}={0,2}$")
_FAILURE_STATUSES = {"failed", "blocked_by_startup", "cleanup_failure"}


def _unsafe_detail(message: str) -> ValueError:
    return ValueError(f"unsafe artifact detail: {message}")


def _check_string(value: str, *, secrets: tuple[str, ...], field_name: str) -> str:
    if len(value) > _MAX_STRING_LENGTH:
        raise _unsafe_detail(f"{field_name} is too long")
    lowered = value.casefold()
    for secret in secrets:
        if secret and secret.casefold() in lowered:
            raise _unsafe_detail(f"{field_name} contains a secret")
    if _BEARER_PATTERN.search(value):
        raise _unsafe_detail(f"{field_name} contains a bearer value")
    if _BASE64_BINARY_PATTERN.fullmatch(value.strip()):
        raise _unsafe_detail(f"{field_name} resembles a binary payload")
    return value


def _safe_value(
    value: object,
    *,
    secrets: tuple[str, ...],
    field_name: str,
    depth: int = 0,
) -> object:
    if depth > _MAX_NESTING:
        raise _unsafe_detail(f"{field_name} is nested too deeply")
    if value is None or isinstance(value, (bool, int)):
        return value
    if isinstance(value, float):
        if not math.isfinite(value):
            raise _unsafe_detail(f"{field_name} is not finite")
        return value
    if isinstance(value, str):
        return _check_string(value, secrets=secrets, field_name=field_name)
    if isinstance(value, (bytes, bytearray, memoryview)):
        raise _unsafe_detail(f"{field_name} contains binary data")
    if isinstance(value, Mapping):
        if len(value) > _MAX_CONTAINER_ITEMS:
            raise _unsafe_detail(f"{field_name} has too many entries")
        safe_mapping: dict[str, object] = {}
        for key, item in value.items():
            if not isinstance(key, str):
                raise _unsafe_detail(f"{field_name} has a non-string key")
            key_name = key.casefold()
            if any(part in key_name for part in _UNSAFE_KEY_PARTS):
                raise _unsafe_detail(f"{field_name}.{key} is request or private data")
            safe_mapping[key] = _safe_value(
                item,
                secrets=secrets,
                field_name=f"{field_name}.{key}",
                depth=depth + 1,
            )
        return safe_mapping
    if isinstance(value, Sequence) and not isinstance(value, (str, bytes, bytearray)):
        if len(value) > _MAX_CONTAINER_ITEMS:
            raise _unsafe_detail(f"{field_name} has too many entries")
        return [
            _safe_value(
                item,
                secrets=secrets,
                field_name=f"{field_name}[{index}]",
                depth=depth + 1,
            )
            for index, item in enumerate(value)
        ]
    raise _unsafe_detail(f"{field_name} has an unsupported value")


def _safe_mapping(value: Mapping[str, object], *, secrets: tuple[str, ...], field_name: str) -> dict[str, object]:
    safe = _safe_value(value, secrets=secrets, field_name=field_name)
    assert isinstance(safe, dict)
    return safe


def _safe_check(result: CheckResult, *, secrets: tuple[str, ...], index: int) -> dict[str, object]:
    if result.status not in {"passed", "failed", "skipped", "blocked_by_startup", "cleanup_failure"}:
        raise ValueError(f"invalid Harness check status: {result.status}")
    if not isinstance(result.check_id, str) or not result.check_id.strip():
        raise ValueError("Harness check id must be non-empty")
    if not isinstance(result.duration_ms, (int, float)) or not math.isfinite(result.duration_ms):
        raise ValueError("Harness check duration must be finite")
    if result.duration_ms < 0:
        raise ValueError("Harness check duration cannot be negative")
    if result.reason is not None and not isinstance(result.reason, str):
        raise ValueError("Harness check reason must be a string")
    return {
        "id": _check_string(result.check_id, secrets=secrets, field_name=f"checks[{index}].id"),
        "status": result.status,
        "duration_ms": round(float(result.duration_ms), 3),
        "reason": (
            _check_string(result.reason, secrets=secrets, field_name=f"checks[{index}].reason")
            if result.reason is not None
            else None
        ),
        "details": _safe_mapping(
            result.details,
            secrets=secrets,
            field_name=f"checks[{index}].details",
        ),
    }


def build_artifact(
    *,
    commit: str,
    mode: str,
    started_at: str,
    duration_ms: float,
    components: Mapping[str, object],
    checks: Iterable[CheckResult],
    coverage: Mapping[str, object],
    cleanup: Mapping[str, object],
    errors: Iterable[object],
    secrets: Iterable[str] = (),
) -> dict[str, object]:
    """Build and validate the only public Harness result representation."""

    secret_values = tuple(secret for secret in secrets if isinstance(secret, str) and secret)
    if not isinstance(duration_ms, (int, float)) or not math.isfinite(duration_ms) or duration_ms < 0:
        raise ValueError("Harness duration must be finite and non-negative")
    safe_commit = _check_string(commit, secrets=secret_values, field_name="commit")
    safe_mode = _check_string(mode, secrets=secret_values, field_name="mode")
    safe_started_at = _check_string(started_at, secrets=secret_values, field_name="started_at")
    safe_checks = [
        _safe_check(result, secrets=secret_values, index=index)
        for index, result in enumerate(checks)
    ]
    safe_errors = [
        _safe_value(error, secrets=secret_values, field_name=f"errors[{index}]")
        for index, error in enumerate(errors)
    ]
    failed_checks = sum(
        check["status"] in _FAILURE_STATUSES
        for check in safe_checks
    )
    if safe_errors:
        failed_checks = max(failed_checks, len(safe_errors))
    return {
        "schema_version": 2,
        "harness_version": HARNESS_VERSION,
        "commit": safe_commit,
        "mode": safe_mode,
        "started_at": safe_started_at,
        "duration_ms": round(float(duration_ms), 3),
        "components": _safe_mapping(components, secrets=secret_values, field_name="components"),
        "checks": safe_checks,
        "coverage": _safe_mapping(coverage, secrets=secret_values, field_name="coverage"),
        "cleanup": _safe_mapping(cleanup, secrets=secret_values, field_name="cleanup"),
        "errors": safe_errors,
        "summary": {
            "status": "failed" if failed_checks else "passed",
            "failed_checks": failed_checks,
        },
    }
