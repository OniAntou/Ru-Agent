"""Small immutable records shared by Harness probes and artifacts."""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass, field
from typing import Literal

CheckStatus = Literal["passed", "failed", "skipped", "blocked_by_startup"]
ChildExitClassification = Literal[
    "running",
    "unexpected_exit",
    "expected_stop",
    "cleanup_failure",
]


@dataclass(frozen=True, slots=True)
class CheckResult:
    check_id: str
    status: CheckStatus
    duration_ms: float
    reason: str | None = None
    details: Mapping[str, object] = field(default_factory=dict)


@dataclass(frozen=True, slots=True)
class ChildExit:
    role: str
    code: int | None
    classification: ChildExitClassification
