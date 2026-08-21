from __future__ import annotations

from harness.session import HarnessSession


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
