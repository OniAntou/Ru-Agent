from __future__ import annotations

import json
from pathlib import Path

import pytest

from harness.session import HarnessSession, agent_tool_loop_completed


def test_session_cleanup_runs_after_readiness_failure() -> None:
    session = HarnessSession.for_test(readiness=False)

    result = session.run(mode="backend-deterministic")

    assert result["summary"]["status"] == "failed"
    assert result["cleanup"] == {
        "children_terminated": True,
        "ports_closed": True,
        "session_descriptor_removed": True,
        "fixture_root_removed": True,
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


def test_agent_tool_loop_allows_approval_results_after_done() -> None:
    events = [
        {"type": "agent.activity"},
        {
            "type": "agent.approval_required",
            "content": "Approval required.",
            "session_id": "session-1",
            "approval_id": "approval-1",
            "tool_id": "web.search",
            "scope": "web.search",
            "expires_at": "2099-01-01T00:00:00+00:00",
        },
        {"type": "done"},
        {"type": "agent.approval_result", "status": "failed"},
        {"type": "agent.approval_result", "error_code": "approval_stale"},
    ]

    assert agent_tool_loop_completed(events)


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
