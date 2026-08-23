from __future__ import annotations

import json
from pathlib import Path

import pytest
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


def test_deterministic_chat_requires_order_privacy_and_one_terminal_event() -> None:
    session = HarnessSession.for_test(readiness=True)

    result = session.run(mode="backend-deterministic")
    statuses = {item["id"]: item["status"] for item in result["checks"]}

    assert statuses["chat.synthetic_order"] == "passed"
    assert statuses["chat.private_metadata"] == "passed"
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


def test_broker_approval_lifecycle_is_separate_from_chat_completion() -> None:
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
