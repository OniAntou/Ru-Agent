import json

import pytest

from mini_gpt.worker_protocol import (
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
        "voice.control",
        "voice.done",
        "worker.health.ok",
    ],
)
def test_ordered_response_types_require_sequence(message_type: str) -> None:
    with pytest.raises(ProtocolError, match="missing_sequence"):
        validate_envelope({"v": 1, "id": "req-1", "type": message_type, "payload": {}})


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
