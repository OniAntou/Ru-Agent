import json

import pytest

from ru_runtime.worker.v1.protocol import (
    KNOWN_TYPES,
    ORDERED_RESPONSE_TYPES,
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
            "capabilities": ["runtime", "voice", "vts", "observation"],
        },
    }

    encoded = encode_envelope(envelope)
    assert validate_envelope(json.loads(encoded)) == envelope
    assert "Em nghe đây." in encoded


def test_v1_capabilities_do_not_include_retired_chat_or_memory_protocols() -> None:
    assert {"runtime.load", "runtime.request", "voice.start", "vts.catalog"}.issubset(
        KNOWN_TYPES
    )
    assert "chat.start" not in KNOWN_TYPES
    assert "chat.done" not in KNOWN_TYPES
    assert "memory.extract" not in KNOWN_TYPES
    assert "memory.facts" not in ORDERED_RESPONSE_TYPES


@pytest.mark.parametrize(
    ("envelope", "code"),
    [
        ({"v": 2, "id": "x", "type": "hello", "payload": {}}, "unsupported_version"),
        ({"v": 1, "id": "", "type": "hello", "payload": {}}, "invalid_id"),
        ({"v": 1, "id": "x", "type": "unknown", "payload": {}}, "unknown_type"),
        ({"v": 1, "id": "x", "type": "hello.ok", "seq": -1, "payload": {}}, "invalid_sequence"),
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


@pytest.mark.parametrize("message_type", sorted(ORDERED_RESPONSE_TYPES))
def test_ordered_response_types_require_sequence(message_type: str) -> None:
    with pytest.raises(ProtocolError, match="missing_sequence"):
        validate_envelope(
            {
                "v": 1,
                "id": "req-1",
                "type": message_type,
                "payload": {},
            }
        )


@pytest.mark.parametrize("message_type", ("chat.start", "chat.done", "memory.extract"))
def test_retired_v1_message_types_are_rejected(message_type: str) -> None:
    with pytest.raises(ProtocolError, match="unknown_type"):
        validate_envelope(
            {"v": 1, "id": "retired-1", "type": message_type, "payload": {}}
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
