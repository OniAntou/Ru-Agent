from __future__ import annotations

import asyncio
import json
from pathlib import Path

import pytest

from harness.deterministic_worker import _chat
from harness.session import HarnessSession
from ru_runtime.worker.v1.protocol import CHAT_DONE
from ru_runtime.worker.v1.server import WorkerReply


AGENT_SCENARIOS = (
    "agent-volume-contract",
    "agent-cancel",
    "agent-stale-result",
    "agent-private-metadata",
)


def test_failed_volume_fixture_returns_exact_bounded_action_outcome() -> None:
    async def collect_replies() -> list[WorkerReply]:
        tool_results: asyncio.Queue[dict[str, object]] = asyncio.Queue()
        await tool_results.put(
            {
                "request_id": "harness-tool-volume-invalid",
                "session_id": "session-1",
                "correlation_id": "correlation-1",
                "status": "failed",
            }
        )
        return [
            reply
            async for reply in _chat(
                {
                    "session_id": "session-1",
                    "settings": {"agent_mode": True},
                    "__worker_correlation_id": "correlation-1",
                    "__worker_tool_result_queue": tool_results,
                },
                asyncio.Event(),
            )
        ]

    replies = asyncio.run(collect_replies())
    done = next(reply for reply in replies if reply.type == CHAT_DONE)

    assert done.payload == {
        "raw_reply": "Lệnh âm lượng không hợp lệ nên em chưa gửi thay đổi tới Windows.",
        "action_outcome": {
            "schema_version": 1,
            "tool_id": "system.volume",
            "tool_version": "1.0.0",
            "status": "failed",
            "reason_code": "invalid_arguments",
        },
    }
    assert set(done.payload["action_outcome"]) == {
        "schema_version",
        "tool_id",
        "tool_version",
        "status",
        "reason_code",
    }


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


def test_backend_agent_probe_covers_broker_approval_and_failed_volume_contract() -> None:
    session_source = Path("scripts/harness/session.py").read_text(encoding="utf-8")
    worker_source = Path("scripts/harness/deterministic_worker.py").read_text(encoding="utf-8")
    broker_probe_source = Path("scripts/harness/broker_probe.py").read_text(encoding="utf-8")
    ui_driver_source = Path("gui-winui3.Harness/UiAutomationScenario.cs").read_text(
        encoding="utf-8"
    )
    expected_reply = "Lệnh âm lượng không hợp lệ nên em chưa gửi thay đổi tới Windows."

    assert '"tool_id": "system.volume"' in worker_source
    assert '"operation": "set", "level": 101' in worker_source
    assert '"expected_status": "failed"' in session_source
    assert '"notes.create"' in session_source
    assert '"web.search"' not in worker_source
    assert '"RU_WORKSPACE_ROOT": str(workspace_root)' in session_source
    workspace_assignment = next(
        line for line in session_source.splitlines() if '"RU_WORKSPACE_ROOT"' in line
    )
    assert "str(self.project_root)" not in workspace_assignment
    assert "approval_stale" in broker_probe_source
    assert "private_agent_metadata_visible" in session_source
    assert expected_reply in worker_source
    assert expected_reply in session_source
    assert expected_reply in ui_driver_source


def test_deterministic_worker_uses_only_the_non_native_volume_contract() -> None:
    source = Path("scripts/harness/deterministic_worker.py").read_text(encoding="utf-8")

    assert '"system.volume"' in source
    assert '"capability_class": "native_mutation"' in source
    assert '"web.search"' not in source
    assert '"files.list"' not in source
    assert "Get-Date" not in source
    assert '"terminal.shell"' not in source


def test_retired_gui_agent_scenarios_are_absent_from_all_harness_entrypoints() -> None:
    sources = [
        Path("scripts/aspnetcore_compatibility_harness.py").read_text(encoding="utf-8"),
        Path("scripts/harness/session.py").read_text(encoding="utf-8"),
        Path("gui-winui3.Harness/Program.cs").read_text(encoding="utf-8"),
        Path("gui-winui3.Harness/UiAutomationScenario.cs").read_text(encoding="utf-8"),
    ]

    for source in sources:
        assert "agent-read-only" not in source
        assert "agent-terminal-approval" not in source
        assert "agent-terminal-reject" not in source


def test_gui_agent_scenario_catalog_is_exact() -> None:
    python_cli = Path("scripts/aspnetcore_compatibility_harness.py").read_text(encoding="utf-8")
    csharp_cli = Path("gui-winui3.Harness/Program.cs").read_text(encoding="utf-8")

    for scenario in AGENT_SCENARIOS:
        assert scenario in python_cli
        assert scenario in csharp_cli


def test_gui_agent_cancel_and_stale_scenarios_replace_in_flight_session() -> None:
    worker = Path("scripts/harness/deterministic_worker.py").read_text(encoding="utf-8")
    driver = Path("gui-winui3.Harness/UiAutomationScenario.cs").read_text(encoding="utf-8")

    assert 'scenario in {"agent-cancel", "agent-stale-result"}' in worker
    assert 'scenario is "agent-cancel" or "agent-stale-result"' in driver
    assert "ReplaceSessionAsync(timeout)" in driver
    assert "noLateAgentUpdate" in driver


def test_deterministic_agent_harness_keeps_volume_metadata_only() -> None:
    catalog = Path("backend-dotnet/RuAgent.Broker/Tools/ToolCatalog.cs").read_text(encoding="utf-8")
    volume_tool = Path(
        "backend-dotnet/RuAgent.Broker/Tools/Desktop/VolumeTools.cs"
    ).read_text(encoding="utf-8")
    deterministic_worker = Path("scripts/harness/deterministic_worker.py").read_text(encoding="utf-8")

    assert "yield return VolumeTools.Manifest;" in catalog
    assert 'var modelEnabled = manifest.Id == "system.volume";' in catalog
    assert 'new HashSet<string>([ToolScope.SystemWrite]' in volume_tool
    assert 'RiskClass.Reversible' in volume_tool
    assert '"system.volume"' in deterministic_worker
    assert "keybd_event" not in deterministic_worker
