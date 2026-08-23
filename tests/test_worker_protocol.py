import json

import pytest

from ru_runtime.worker.v1.protocol import (
    ProtocolError,
    encode_envelope,
    redacted_summary,
    validate_envelope,
)


def test_hello_round_trips_utf8_payload() -> None:
    envelope = {
        "v": 1,
        "id": "conn-1",
        "type": "hello",
        "payload": {
            "token": "opaque-secret",
            "text": "Em nghe đây.",
            "capabilities": ["chat", "voice"],
        },
    }

    encoded = encode_envelope(envelope)
    decoded = validate_envelope(json.loads(encoded))

    assert decoded == envelope
    assert "Em nghe đây." in encoded


@pytest.mark.parametrize(
    ("envelope", "code"),
    [
        ({"v": 2, "id": "x", "type": "hello", "payload": {}}, "unsupported_version"),
        ({"v": 1, "id": "", "type": "hello", "payload": {}}, "invalid_id"),
        ({"v": 1, "id": "x", "type": "unknown", "payload": {}}, "unknown_type"),
        (
            {"v": 1, "id": "x", "type": "hello.ok", "seq": -1, "payload": {}},
            "invalid_sequence",
        ),
        ({"v": 1, "id": "x", "type": "hello", "payload": []}, "invalid_payload"),
    ],
)
def test_invalid_envelopes_fail_with_stable_redacted_codes(envelope: dict, code: str) -> None:
    with pytest.raises(ProtocolError) as captured:
        validate_envelope(envelope)

    assert captured.value.code == code
    assert "opaque-secret" not in str(captured.value)


def test_request_id_is_bounded() -> None:
    with pytest.raises(ProtocolError, match="invalid_id"):
        validate_envelope({"v": 1, "id": "x" * 129, "type": "hello", "payload": {}})


@pytest.mark.parametrize(
    "message_type",
    [
        "hello.ok",
        "chat.reset",
        "chat.chunk",
        "chat.done",
        "memory.facts",
        "runtime.state",
        "runtime.response",
        "voice.control",
        "voice.done",
        "worker.health.ok",
    ],
)
def test_ordered_response_types_require_sequence(message_type: str) -> None:
    payload = {"raw_reply": "Em nghe đây."} if message_type == "chat.done" else {}
    with pytest.raises(ProtocolError, match="missing_sequence"):
        validate_envelope(
            {
                "v": 1,
                "id": "req-1",
                "type": message_type,
                "payload": payload,
            }
        )


def test_chat_done_accepts_one_bounded_action_outcome() -> None:
    envelope = validate_envelope(
        {
            "v": 1,
            "id": "chat-1",
            "type": "chat.done",
            "seq": 2,
            "payload": {
                "raw_reply": "Windows không chấp nhận thay đổi âm lượng.",
                "action_outcome": {
                    "schema_version": 1,
                    "tool_id": "system.volume",
                    "tool_version": "1.0.0",
                    "status": "failed",
                    "reason_code": "write_failed",
                },
            },
        }
    )

    assert envelope["payload"]["action_outcome"]["reason_code"] == "write_failed"


def test_chat_done_accepts_outcomes_for_any_bounded_advertised_tool() -> None:
    envelope = validate_envelope(
        {
            "v": 1,
            "id": "chat-2",
            "type": "chat.done",
            "seq": 3,
            "payload": {
                "raw_reply": "bounded reply",
                "action_outcome": {
                    "schema_version": 1,
                    "tool_id": "files.edit",
                    "tool_version": "1.0.0",
                    "status": "failed",
                    "reason_code": "edit_not_unique",
                },
            },
        }
    )

    outcome = envelope["payload"]["action_outcome"]
    assert (outcome["tool_id"], outcome["status"]) == ("files.edit", "failed")


@pytest.mark.parametrize(
    "outcome",
    (
        {
            "schema_version": 1,
            "tool_id": "system.volume",
            "tool_version": "1.0.0",
            "status": "failed",
            "reason_code": "write_failed",
            "output": {"level": 2},
        },
        {
            "schema_version": 1,
            "tool_id": "system.volume",
            "tool_version": "1.0.0",
            "status": "failed",
            "reason_code": None,
        },
        {
            "schema_version": 1,
            "tool_id": "system.volume",
            "tool_version": "1.0.0",
            "status": "failed",
            "reason_code": "a" * 65,
        },
        {
            "schema_version": 1,
            "tool_id": "system.volume",
            "tool_version": "1.0.0",
            "status": "succeeded",
            "reason_code": "write_failed",
        },
        {
            "schema_version": 1,
            "tool_id": "system.volume",
            "tool_version": "1.0.0",
            "status": "unknown",
            "reason_code": "write_failed",
        },
        {
            "schema_version": 1,
            "tool_id": "",
            "tool_version": "1.0.0",
            "status": "failed",
            "reason_code": "write_failed",
        },
        {
            "schema_version": 1,
            "tool_id": "x" * 129,
            "tool_version": "1.0.0",
            "status": "failed",
            "reason_code": "write_failed",
        },
    ),
)
def test_chat_done_rejects_malformed_action_outcomes(outcome: object) -> None:
    with pytest.raises(ProtocolError, match="invalid_action_outcome"):
        validate_envelope(
            {
                "v": 1,
                "id": "chat-1",
                "type": "chat.done",
                "seq": 2,
                "payload": {
                    "raw_reply": "bounded reply",
                    "action_outcome": outcome,
                },
            }
        )


@pytest.mark.parametrize(
    "payload",
    (
        {},
        {"raw_reply": ""},
        {"raw_reply": "x" * 32_001},
        {"raw_reply": "bounded", "extra": True},
    ),
)
def test_chat_done_rejects_unbounded_or_unknown_payload_fields(payload: dict) -> None:
    with pytest.raises(ProtocolError, match="invalid_chat_done"):
        validate_envelope(
            {
                "v": 1,
                "id": "chat-1",
                "type": "chat.done",
                "seq": 2,
                "payload": payload,
            }
        )


def test_redacted_summary_omits_id_token_and_payload() -> None:
    summary = redacted_summary(
        {
            "v": 1,
            "id": "private-request-id",
            "type": "hello",
            "payload": {"token": "do-not-leak", "prompt": "private prompt"},
        }
    )

    assert "type=hello" in summary
    assert "private-request-id" not in summary
    assert "do-not-leak" not in summary
    assert "private prompt" not in summary
