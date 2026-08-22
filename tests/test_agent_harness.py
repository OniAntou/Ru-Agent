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


def test_backend_agent_probe_covers_approval_dispatch_replay_and_privacy() -> None:
    source = Path("scripts/harness/session.py").read_text(encoding="utf-8")

    assert "agent.approval_required" in source
    assert "agent.approval_result" in source
    assert "approval_stale" in source
    assert "agent.approval_dispatch" in source
    assert "agent.approval_replay" in source
    assert "private_agent_metadata_visible" in source


def test_deterministic_approval_fixture_never_uses_a_real_shell() -> None:
    source = Path("scripts/harness/deterministic_worker.py").read_text(encoding="utf-8")

    assert "RU_HARNESS_AGENT_APPROVAL" in source
    assert '"web.search"' in source
    assert "Get-Date" not in source
    assert '"terminal.shell"' not in source


def test_gui_agent_approval_scenarios_click_and_clear_the_approval_card() -> None:
    source = Path("gui-winui3.Harness/UiAutomationScenario.cs").read_text(encoding="utf-8")

    assert '"AgentApprovalCard"' in source
    assert '"AgentApproveButton"' in source
    assert '"AgentRejectButton"' in source
    assert '"agent.approval.result"' in source
    assert "Invoke(approvalButton)" in source


def test_deterministic_agent_harness_keeps_volume_metadata_only() -> None:
    catalog = Path("backend-dotnet/RuAgent.Broker/Tools/ToolCatalog.cs").read_text(encoding="utf-8")
    volume_tool = Path(
        "backend-dotnet/RuAgent.Broker/Tools/Desktop/VolumeTools.cs"
    ).read_text(encoding="utf-8")
    deterministic_worker = Path("scripts/harness/deterministic_worker.py").read_text(encoding="utf-8")

    assert 'registry.Register(VolumeTools.Manifest)' in catalog
    assert 'new HashSet<string>([ToolScope.SystemWrite]' in volume_tool
    assert 'RiskClass.Reversible' in volume_tool
    assert '"system.volume"' not in deterministic_worker
    assert "keybd_event" not in deterministic_worker
