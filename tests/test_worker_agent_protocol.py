from __future__ import annotations

import pytest

from ru_runtime.worker.v1.protocol import (
    CHAT_TOOL_CANCEL,
    CHAT_TOOL_REQUEST,
    CHAT_TOOL_RESULT,
    ProtocolError,
    validate_envelope,
)


@pytest.mark.parametrize(
    "message_type",
    [CHAT_TOOL_REQUEST, CHAT_TOOL_RESULT, CHAT_TOOL_CANCEL],
)
def test_structured_agent_messages_are_known(message_type: str) -> None:
    payload = {
        "request_id": "tool-request-1",
        "session_id": "session-1",
        "correlation_id": "correlation-1",
    }
    if message_type == CHAT_TOOL_REQUEST:
        payload.update(
            {
                "tool_id": "files.list",
                "tool_version": "1.0.0",
                "arguments": {},
                "deadline_ms": 30_000,
            }
        )
    elif message_type == CHAT_TOOL_RESULT:
        payload.update(
            {"status": "succeeded", "output": {}, "safe_summary": "Done."}
        )
    envelope = {
        "v": 1,
        "id": "tool-1",
        "type": message_type,
        "payload": payload,
    }

    assert validate_envelope(envelope)["type"] == message_type


def test_tool_request_requires_structured_identity_and_arguments() -> None:
    with pytest.raises(ProtocolError, match="invalid_tool_request"):
        validate_envelope(
            {
                "v": 1,
                "id": "tool-1",
                "type": CHAT_TOOL_REQUEST,
                "payload": {
                    "session_id": "session-1",
                    "correlation_id": "correlation-1",
                    "request_id": "tool-request-1",
                    "tool_id": "files.list",
                    "tool_version": "1.0.0",
                    "arguments": [],
                    "deadline_ms": 30_000,
                },
            }
        )


def test_tool_result_requires_bounded_status_and_safe_summary() -> None:
    with pytest.raises(ProtocolError, match="invalid_tool_result"):
        validate_envelope(
            {
                "v": 1,
                "id": "tool-1",
                "type": CHAT_TOOL_RESULT,
                "payload": {
                    "request_id": "tool-request-1",
                    "status": "unknown",
                    "output": {},
                    "safe_summary": "ok",
                },
            }
        )


def test_tool_result_accepts_approval_required_status() -> None:
    envelope = {
        "v": 1,
        "id": "tool-1",
        "type": CHAT_TOOL_RESULT,
        "payload": {
            "request_id": "tool-request-1",
            "status": "approval_required",
            "output": {"approval_id": "approval-1"},
            "safe_summary": "Approval required.",
            "error_code": "approval_required",
        },
    }

    assert validate_envelope(envelope)["type"] == CHAT_TOOL_RESULT


def test_tool_result_rejects_oversized_output() -> None:
    with pytest.raises(ProtocolError, match="invalid_tool_result"):
        validate_envelope(
            {
                "v": 1,
                "id": "tool-1",
                "type": CHAT_TOOL_RESULT,
                "payload": {
                    "request_id": "tool-request-1",
                    "status": "succeeded",
                    "output": {"content": "x" * (256 * 1024)},
                    "safe_summary": "bounded",
                },
            }
        )


def test_tool_cancel_requires_matching_context() -> None:
    with pytest.raises(ProtocolError, match="invalid_tool_cancel"):
        validate_envelope(
            {
                "v": 1,
                "id": "tool-1",
                "type": CHAT_TOOL_CANCEL,
                "payload": {
                    "request_id": "tool-request-1",
                    "session_id": "",
                    "correlation_id": "correlation-1",
                },
            }
        )
