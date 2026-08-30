from __future__ import annotations

import json
import subprocess
from pathlib import Path

import harness.session as session_module
import pytest
from harness.processes import ChildLifecycle
from harness.session import (
    HarnessSession,
    _contains_private_agent_metadata,
    agent_tool_loop_completed,
    broker_approval_lifecycle_completed,
)


def test_session_cleanup_runs_after_readiness_failure() -> None:
    session = HarnessSession.for_test(readiness=False)

    result = session.run(mode="backend-deterministic")

    assert result["summary"]["status"] == "failed"
    assert result["cleanup"] == {
        "children_terminated": True,
        "ports_closed": True,
        "session_descriptor_removed": True,
        "fixture_root_removed": True,
        "workspace_root_removed": True,
        "temporary_root_removed": True,
    }


def test_deterministic_agent_requires_tool_loop_privacy_and_voice_checks() -> None:
    session = HarnessSession.for_test(readiness=True)

    result = session.run(mode="backend-deterministic")
    statuses = {item["id"]: item["status"] for item in result["checks"]}

    assert statuses["agent.synthetic_tool_loop"] == "passed"
    assert statuses["agent.private_metadata"] == "passed"
    assert statuses["voice.synthetic_stt"] == "passed"
    assert statuses["voice.synthetic_tts"] == "passed"


def test_agent_tool_loop_requires_one_matching_terminal_activity_before_done() -> None:
    events = [
        {"type": "agent.activity", "tool_id": "system.volume", "status": "requested"},
        {
            "type": "agent.activity",
            "tool_id": "system.volume",
            "status": "failed",
            "error_code": "invalid_arguments",
        },
        {"type": "done"},
    ]

    assert agent_tool_loop_completed(events, expected_status="failed")
    assert not agent_tool_loop_completed(events, expected_status="succeeded")


@pytest.mark.parametrize(
    "events",
    [
        [
            {"type": "agent.activity", "tool_id": "system.volume", "status": "requested"},
            {"type": "done"},
        ],
        [
            {"type": "agent.activity", "tool_id": "system.volume", "status": "requested"},
            {"type": "agent.activity", "tool_id": "system.volume", "status": "mystery"},
            {"type": "done"},
        ],
        [
            {"type": "agent.activity", "content": "failed"},
            {"type": "done"},
        ],
        [
            {"type": "done"},
            {"type": "agent.activity", "tool_id": "system.volume", "status": "failed"},
        ],
        [
            {"type": "agent.activity", "tool_id": "system.volume", "status": "failed"},
            {"type": "agent.activity", "tool_id": "system.volume", "status": "failed"},
            {"type": "done"},
        ],
        [
            {"type": "agent.activity", "tool_id": "files.list", "status": "failed"},
            {"type": "done"},
        ],
    ],
)
def test_agent_tool_loop_rejects_ambiguous_or_mismatched_sequences(
    events: list[dict[str, object]],
) -> None:
    assert not agent_tool_loop_completed(events, expected_status="failed")


def test_broker_approval_lifecycle_is_separate_from_agent_completion() -> None:
    lifecycle = {
        "approval_required": True,
        "approval_dispatch": True,
        "approval_replay": True,
        "session_closed": True,
        "terminal_status": "succeeded",
        "replay_status": "denied",
    }

    assert broker_approval_lifecycle_completed(lifecycle)
    assert not agent_tool_loop_completed(
        [
            {"type": "agent.approval_required"},
            {"type": "agent.approval_result", "status": "succeeded"},
            {"type": "done"},
        ],
        expected_status="succeeded",
    )


def test_agent_privacy_check_allows_stable_invalid_arguments_error_code() -> None:
    assert not _contains_private_agent_metadata(
        [{"type": "agent.activity", "error_code": "invalid_arguments"}]
    )
    assert _contains_private_agent_metadata(
        [{"type": "agent.activity", "arguments": {"level": 101}}]
    )


def test_gui_driver_output_is_converted_to_safe_checks(tmp_path: Path) -> None:
    payload = {
        "schema_version": 2,
        "scenario": "turn",
        "checks": [
            {
                "id": "gui.voice_turn",
                "status": "passed",
                "duration_ms": 12.5,
                "reason": None,
            }
        ],
        "event_counts": {"voice.reply.completed": 1},
        "existing_instance_detected": False,
        "app_closed": True,
    }

    checks = HarnessSession._convert_gui_checks(payload)

    assert checks[0].check_id == "gui.voice_turn"
    assert checks[0].status == "passed"
    assert checks[0].details == {"event_counts": {"voice.reply.completed": 1}}


def test_gui_driver_output_rejects_private_or_unknown_fields() -> None:
    with pytest.raises(ValueError, match="private"):
        HarnessSession._parse_gui_driver_output(
            json.dumps(
                {
                    "schema_version": 2,
                    "scenario": "turn",
                    "checks": [],
                    "event_counts": {},
                    "existing_instance_detected": False,
                    "app_closed": True,
                    "transcript": "must not pass",
                }
            )
        )

    with pytest.raises(ValueError, match="unsupported"):
        HarnessSession._convert_gui_checks(
            {
                "schema_version": 2,
                "scenario": "turn",
                "checks": [
                    {
                        "id": "gui.voice_turn",
                        "status": "passed",
                        "duration_ms": 1.0,
                        "reason": None,
                        "details": {"private": True},
                    }
                ],
                "event_counts": {},
                "existing_instance_detected": False,
                "app_closed": True,
            }
        )


def test_verified_volume_fixture_is_bounded_and_has_no_private_metadata() -> None:
    result = {
        "operation": "set",
        "level": 2,
        "muted": False,
        "verified": True,
    }

    assert set(result) == {"operation", "level", "muted", "verified"}
    assert result["level"] == 2
    assert result["verified"] is True
    assert "device_name" not in result
    assert "native_error" not in result
    assert "arguments" not in result


def test_session_owns_and_removes_a_temporary_workspace() -> None:
    session = HarnessSession.for_test(readiness=True)
    workspace = session._workspace_root
    fixture = workspace / "notes" / "approval-fixture.md"
    fixture.parent.mkdir(parents=True, exist_ok=True)
    fixture.write_text("bounded fixture", encoding="utf-8")

    cleanup = session.close()

    assert cleanup["workspace_root_removed"] is True
    assert not workspace.exists()


class _ExitedProcess:
    def poll(self) -> int:
        return 1

    def terminate(self) -> None:
        pass

    def kill_tree(self) -> None:
        pass

    def wait(self, timeout: float | None = None) -> int:
        return 1


def test_dotnet_projects_are_prebuilt_before_any_child_launch(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    session = HarnessSession.for_test(readiness=True)
    calls: list[tuple[str, tuple[str, ...]]] = []
    environments: list[dict[str, str]] = []

    def record_prebuild() -> None:
        calls.append(("prebuild", ()))

    def record_child(command: list[str], **kwargs: object) -> object:
        role = kwargs["role"]
        assert isinstance(role, str)
        calls.append((role, tuple(command)))
        environment = kwargs["environment"]
        assert isinstance(environment, dict)
        environments.append(environment)
        return object()

    monkeypatch.setattr(
        session,
        "_prepare_dotnet_projects",
        record_prebuild,
        raising=False,
    )
    monkeypatch.setattr(session_module, "start_owned_child", record_child)

    try:
        session._start_children()
        assert [role for role, _ in calls] == ["prebuild", "worker", "broker", "edge"]
        assert "--no-build" in calls[2][1]
        assert "--no-build" in calls[3][1]
        assert "RuBackend__RuntimeMigrationMode" not in environments[0]
        assert environments[0]["RU_HARNESS_WORKER_TOKEN"] == environments[0]["RU_AGENT_WORKER_TOKEN"]
        assert environments[0]["RU_HARNESS_WORKER_TOKEN"] == environments[0]["RU_DEVICE_WORKER_TOKEN"]
        assert "RU_WORKER_TOKEN" not in environments[0]
        assert environments[2]["RuBackend__AgentWorkerUrl"].endswith("/worker/v2")
        assert environments[2]["RuBackend__DeviceWorkerUrl"].endswith("/worker/v1")
    finally:
        session._children.clear()
        session.close()


def test_dotnet_prebuilds_are_sequential_and_bounded(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    session = HarnessSession.for_test(readiness=True)
    calls: list[tuple[tuple[str, ...], bool, int]] = []

    def record_run(
        command: list[str],
        *,
        cwd: Path,
        check: bool,
        timeout: int,
    ) -> subprocess.CompletedProcess[str]:
        del cwd
        calls.append((tuple(command), check, timeout))
        return subprocess.CompletedProcess(command, 0)

    monkeypatch.setattr(session_module.subprocess, "run", record_run)

    try:
        session._prepare_dotnet_projects()
    finally:
        session.close()

    assert len(calls) == 2
    assert "RuAgent.Broker.csproj" in calls[0][0][3]
    assert "RuBackend.csproj" in calls[1][0][3]
    assert all(check is True and 0 < timeout <= 120 for _, check, timeout in calls)


def test_startup_failure_reports_the_exited_child_without_log_content(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    session = HarnessSession.for_test(readiness=True)
    session._children = [ChildLifecycle(role="edge", process=_ExitedProcess())]
    monkeypatch.setattr(session, "_start_children", lambda **_: None)

    try:
        checks = session._run_backend("backend-deterministic")
    finally:
        session._children.clear()
        session.close()

    startup = checks[0]
    assert startup.status == "blocked_by_startup"
    assert startup.reason == "child_unexpected_exit"
    assert startup.details == {
        "role": "edge",
        "exit_code": 1,
        "classification": "unexpected_exit",
    }


def test_runtime_live_never_attempts_a_retired_python_worker(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    session = HarnessSession.for_test(readiness=True)
    session.project_root = tmp_path
    legacy_worker = tmp_path / "src" / "ru_runtime" / "worker" / "v1" / "server.py"
    legacy_worker.parent.mkdir(parents=True)
    legacy_worker.write_text("legacy", encoding="utf-8")
    monkeypatch.setattr(
        session,
        "_start_children",
        lambda **_: pytest.fail("runtime-live attempted to launch a child"),
    )

    try:
        checks = session._run_backend("runtime-live")
    finally:
        session.close()

    assert checks == [
        session_module.CheckResult(
            "startup.readiness",
            "blocked_by_startup",
            0.0,
            reason="python_compatibility_retired",
            details={
                "next_gate": "managed_runtime_native",
            },
        )
    ]
