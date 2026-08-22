from __future__ import annotations

import json
from pathlib import Path

import pytest

from harness.session import HarnessSession


AGENT_SCENARIOS = (
    "agent-read-only",
    "agent-terminal-approval",
    "agent-terminal-reject",
    "agent-cancel",
    "agent-stale-result",
    "agent-private-metadata",
)


@pytest.mark.parametrize("scenario", AGENT_SCENARIOS)
def test_agent_gui_scenarios_are_accepted_before_process_start(scenario: str) -> None:
    session = HarnessSession.for_test(readiness=True)
    try:
        checks = session.run_gui(
            app_path=Path("missing-ru.exe"),
            driver_project=Path("missing-driver.csproj"),
            scenario=scenario,  # type: ignore[arg-type]
            timeout_seconds=30,
        )
        assert checks[0].check_id == "gui.driver_start"
        assert checks[0].status == "failed"
    finally:
        session.close()


def test_agent_driver_payload_rejects_private_fields() -> None:
    payload = {
        "schema_version": 2,
        "scenario": "agent-private-metadata",
        "checks": [],
        "event_counts": {"agent.activity": 1},
        "existing_instance_detected": False,
        "app_closed": True,
    }
    parsed = HarnessSession._parse_gui_driver_output(json.dumps(payload))
    assert parsed["scenario"] == "agent-private-metadata"

    payload["checks"] = [
        {
            "id": "gui.agent-private-metadata",
            "status": "passed",
            "duration_ms": 1.0,
            "reason": None,
            "safe_summary": "must be rejected",
        }
    ]
    with pytest.raises(ValueError, match="unsupported"):
        HarnessSession._convert_gui_checks(payload)
