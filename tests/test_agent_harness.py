from __future__ import annotations

import json
from pathlib import Path

import pytest
from harness.session import HarnessSession

ROOT = Path(__file__).resolve().parents[1]

AGENT_SCENARIOS = (
    "agent-volume-contract",
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


def test_backend_agent_probe_covers_broker_approval_and_v2_tool_loop() -> None:
    session_source = (ROOT / "scripts/harness/session.py").read_text(encoding="utf-8")
    worker_source = (ROOT / "scripts/harness/deterministic_worker.py").read_text(encoding="utf-8")
    broker_probe_source = (ROOT / "scripts/harness/broker_probe.py").read_text(encoding="utf-8")
    ui_driver_source = (ROOT / "gui-winui3.Harness/UiAutomationScenario.cs").read_text(
        encoding="utf-8"
    )
    assert '"tool_id": "files.read"' in worker_source
    assert '"path": "harness-agent.txt"' in worker_source
    assert "_probe_agent_api" in session_source
    assert '"tool.proposed"' in session_source
    assert '"tool.execution_completed"' in session_source
    assert '"notes.create"' in session_source
    assert '"web.search"' not in worker_source
    assert '"RU_WORKSPACE_ROOT": str(workspace_root)' in session_source
    workspace_assignment = next(
        line for line in session_source.splitlines() if '"RU_WORKSPACE_ROOT"' in line
    )
    assert "str(self.project_root)" not in workspace_assignment
    assert "approval_stale" in broker_probe_source
    assert "private_agent_metadata_visible" in session_source
    assert "Harness agent tool loop completed." in worker_source
    assert "Harness agent tool loop completed." in ui_driver_source
    assert "Lệnh âm lượng không hợp lệ nên em chưa gửi thay đổi tới Windows." not in ui_driver_source
    assert '"workspace_path": str(workspace_path.resolve())' in session_source
    assert '"harness-agent.txt"' in session_source


def test_deterministic_worker_uses_only_the_read_only_agent_contract() -> None:
    source = (ROOT / "scripts/harness/deterministic_worker.py").read_text(encoding="utf-8")

    assert '"files.read"' in source
    assert '"path": "harness-agent.txt"' in source
    assert '"capability_class": "native_mutation"' not in source
    assert '"web.search"' not in source
    assert '"files.list"' not in source
    assert "Get-Date" not in source
    assert '"terminal.shell"' not in source


def test_deterministic_worker_does_not_reach_retired_production_modules() -> None:
    sources = [
        (ROOT / "scripts/harness/deterministic_worker.py").read_text(encoding="utf-8"),
        (ROOT / "scripts/harness/worker_fixture.py").read_text(encoding="utf-8"),
        (ROOT / "scripts/harness/generation_stub/prompt_context.py").read_text(encoding="utf-8"),
        (ROOT / "scripts/harness/generation_stub/system_prompt_composer.py").read_text(
            encoding="utf-8"
        ),
    ]

    for source in sources:
        assert "ru_runtime.generation" not in source
        assert "ru_runtime.worker.v1" not in source
        assert "ru_runtime.worker.v2" not in source


def test_retired_gui_agent_scenarios_are_absent_from_all_harness_entrypoints() -> None:
    sources = [
        (ROOT / "scripts/aspnetcore_compatibility_harness.py").read_text(encoding="utf-8"),
        (ROOT / "scripts/harness/session.py").read_text(encoding="utf-8"),
        (ROOT / "gui-winui3.Harness/Program.cs").read_text(encoding="utf-8"),
        (ROOT / "gui-winui3.Harness/UiAutomationScenario.cs").read_text(encoding="utf-8"),
        (ROOT / "scripts/harness/deterministic_worker.py").read_text(encoding="utf-8"),
    ]

    for source in sources:
        assert "agent-read-only" not in source
        assert "agent-terminal-approval" not in source
        assert "agent-terminal-reject" not in source


def test_gui_agent_scenario_catalog_is_exact() -> None:
    python_cli = (ROOT / "scripts/aspnetcore_compatibility_harness.py").read_text(encoding="utf-8")
    csharp_cli = (ROOT / "gui-winui3.Harness/Program.cs").read_text(encoding="utf-8")

    for scenario in AGENT_SCENARIOS:
        assert scenario in python_cli
        assert scenario in csharp_cli


def test_gui_agent_cancel_and_stale_scenarios_replace_in_flight_session() -> None:
    worker = (ROOT / "scripts/harness/deterministic_worker.py").read_text(encoding="utf-8")
    driver = (ROOT / "gui-winui3.Harness/UiAutomationScenario.cs").read_text(encoding="utf-8")

    assert 'scenario in {"agent-cancel", "agent-stale-result"}' in worker
    assert 'scenario is "agent-cancel" or "agent-stale-result"' in driver
    assert "ReplaceSessionAsync(timeout)" in driver
    assert "noLateAgentUpdate" in driver


def test_deterministic_agent_harness_keeps_volume_metadata_only() -> None:
    catalog = (ROOT / "backend-dotnet/RuAgent.Broker/Tools/ToolCatalog.cs").read_text(encoding="utf-8")
    binding_registry = (
        ROOT / "backend-dotnet/RuAgent.Broker/Tools/ToolBindingRegistry.cs"
    ).read_text(encoding="utf-8")
    volume_tool = (
        ROOT / "backend-dotnet/RuAgent.Broker/Tools/Desktop/VolumeTools.cs"
    ).read_text(encoding="utf-8")
    deterministic_worker = (ROOT / "scripts/harness/deterministic_worker.py").read_text(encoding="utf-8")

    assert "yield return VolumeTools.Manifest;" in catalog
    assert "ModelCapabilities(" in binding_registry
    assert 'new HashSet<string>([ToolScope.SystemWrite]' in volume_tool
    assert 'RiskClass.Reversible' in volume_tool
    assert '"files.read"' in deterministic_worker
    assert "keybd_event" not in deterministic_worker
