from __future__ import annotations

import pytest
from harness.artifacts import build_artifact
from harness.model import CheckResult


def test_build_artifact_uses_schema_v2_and_safe_check_details() -> None:
    artifact = build_artifact(
        evidence_class="contract",
        commit="abc123",
        mode="backend-deterministic",
        started_at="2026-08-22T00:00:00Z",
        duration_ms=12.5,
        components={"edge": {"status": "passed"}},
        checks=[CheckResult("auth.correct_bearer", "passed", 1.2)],
        coverage={"http": {"total": 41, "passed": 41, "failed": 0, "skipped": 0}},
        cleanup={"ports_closed": True},
        errors=[],
    )

    assert artifact["schema_version"] == 2
    assert artifact["evidence_class"] == "contract"
    assert artifact["mode"] == "backend-deterministic"
    assert artifact["summary"] == {"status": "passed", "failed_checks": 0}


def test_artifact_rejects_secrets_and_body_like_details() -> None:
    result = CheckResult(
        "auth.correct_bearer",
        "failed",
        1.0,
        reason="Bearer private-token response body private prompt",
    )

    with pytest.raises(ValueError, match="unsafe artifact detail"):
        build_artifact(
            evidence_class="contract",
            commit="abc123",
            mode="backend-deterministic",
            started_at="2026-08-22T00:00:00Z",
            duration_ms=1.0,
            components={},
            checks=[result],
            coverage={},
            cleanup={},
            errors=[],
            secrets=["private-token", "private prompt"],
        )


@pytest.mark.parametrize("evidence_class", ["deterministic", "live", "", None, 1])
def test_artifact_rejects_unknown_evidence_classes(evidence_class: object) -> None:
    with pytest.raises(ValueError, match="^invalid evidence class$"):
        build_artifact(
            evidence_class=evidence_class,  # type: ignore[arg-type]
            commit="abc123",
            mode="backend-deterministic",
            started_at="2026-08-22T00:00:00Z",
            duration_ms=1.0,
            components={},
            checks=[],
            coverage={},
            cleanup={},
            errors=[],
        )
