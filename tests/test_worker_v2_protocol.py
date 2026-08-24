from __future__ import annotations

import json
import math
from pathlib import Path

import pytest

from ru_runtime.worker.v2.protocol import (
    AGENT_STEP_CANCEL,
    AGENT_STEP_DELTA,
    AGENT_STEP_DONE,
    AGENT_STEP_START,
    ERROR,
    HELLO,
    HELLO_OK,
    AssistantStep,
    ModelToolCall,
    ProtocolError,
    decode_envelope,
    encode_envelope,
    step_to_payload,
    validate_envelope,
)


def start_envelope() -> dict:
    return {
        "v": 2,
        "id": "step-1",
        "type": AGENT_STEP_START,
        "correlation_id": "corr-1",
        "payload": {
            "prompt": "đọc README",
            "turns": [],
            "memory_context": None,
            "settings": {"model_tools": []},
        },
    }


def test_worker_v2_golden_fixtures_lock_python_and_csharp_wire_contract() -> None:
    fixture_path = Path(__file__).parent / "fixtures" / "worker-v2-protocol.json"
    fixture = json.loads(fixture_path.read_text(encoding="utf-8"))

    assert fixture["protocol_version"] == 2
    for item in fixture["valid_envelopes"]:
        raw = json.dumps(item["envelope"], ensure_ascii=False, separators=(",", ":"))
        assert decode_envelope(raw) == item["envelope"], item["name"]
    for item in fixture["invalid_raw_envelopes"]:
        with pytest.raises(ProtocolError, match=item["code"]):
            decode_envelope(item["json"])


def test_v2_deeply_nested_json_maps_recursion_to_bounded_protocol_error() -> None:
    nesting = 5_000
    raw = (
        '{"v":2,"id":"conn-1","type":"hello","payload":{"token":'
        + ("[" * nesting)
        + '"secret"'
        + ("]" * nesting)
        + "}}"
    )

    with pytest.raises(ProtocolError, match="invalid_json"):
        decode_envelope(raw)


def test_v2_envelope_round_trips_authenticated_message_types() -> None:
    for message_type, payload in (
        (HELLO, {"token": "secret"}),
        (HELLO_OK, {"version": 2, "capabilities": ["agent.step"]}),
        (AGENT_STEP_START, {"prompt": "đọc README", "settings": {"model_tools": []}}),
        (AGENT_STEP_DELTA, {"text": "Em đang đọc."}),
        (AGENT_STEP_DONE, {"text": "Xong.", "tool_calls": [], "finish_reason": "stop"}),
        (AGENT_STEP_CANCEL, {}),
        (ERROR, {"code": "provider_timeout", "retryable": True}),
    ):
        envelope = {
            "v": 2,
            "id": "step-1",
            "type": message_type,
            "payload": payload,
        }
        if message_type == HELLO_OK:
            envelope["seq"] = 0
        elif message_type in {AGENT_STEP_DELTA, AGENT_STEP_DONE, ERROR}:
            envelope["seq"] = 1
        assert validate_envelope(json.loads(encode_envelope(envelope))) == envelope


def test_v2_step_shape_has_one_bounded_tool_call_without_receipt() -> None:
    step = AssistantStep(
        text=None,
        tool_calls=(
            ModelToolCall(
                provider_call_id="provider-1",
                tool_id="files.read",
                tool_version="1.0.0",
                arguments={"path": "README.md"},
            ),
        ),
        finish_reason="tool_calls",
    )

    assert step_to_payload(step) == {
        "text": None,
        "tool_calls": [
            {
                "provider_call_id": "provider-1",
                "tool_id": "files.read",
                "tool_version": "1.0.0",
                "arguments": {"path": "README.md"},
            }
        ],
        "finish_reason": "tool_calls",
    }


def test_v2_request_messages_reject_response_sequences() -> None:
    for message_type, payload in (
        (HELLO, {"token": "secret"}),
        (AGENT_STEP_START, {"prompt": "x", "settings": {"model_tools": []}}),
        (AGENT_STEP_CANCEL, {}),
    ):
        with pytest.raises(ProtocolError, match="invalid_sequence"):
            validate_envelope(
                {
                    "v": 2,
                    "id": "step-1",
                    "type": message_type,
                    "seq": 1,
                    "payload": payload,
                }
            )


def test_v2_response_sequences_are_positive_after_hello() -> None:
    for message_type, payload in (
        (AGENT_STEP_DELTA, {"text": "x"}),
        (AGENT_STEP_DONE, {"text": "x", "tool_calls": [], "finish_reason": "stop"}),
        (ERROR, {"code": "provider_timeout", "retryable": True}),
    ):
        with pytest.raises(ProtocolError, match="invalid_sequence"):
            validate_envelope(
                {
                    "v": 2,
                    "id": "step-1",
                    "type": message_type,
                    "seq": 0,
                    "payload": payload,
                }
            )

    with pytest.raises(ProtocolError, match="invalid_sequence"):
        validate_envelope(
            {
                "v": 2,
                "id": "conn-1",
                "type": HELLO_OK,
                "seq": 1,
                "payload": {"version": 2, "capabilities": ["agent.step"]},
            }
        )


def test_v2_finish_reason_and_tool_calls_cannot_contradict_each_other() -> None:
    call = ModelToolCall(None, "files.read", "1.0.0", {"path": "README.md"})

    with pytest.raises(ValueError, match="finish reason"):
        AssistantStep(text=None, tool_calls=(call,), finish_reason="stop")

    with pytest.raises(ProtocolError, match="invalid_step"):
        validate_envelope(
            {
                "v": 2,
                "id": "step-1",
                "type": AGENT_STEP_DONE,
                "seq": 1,
                "payload": {
                    "text": None,
                    "tool_calls": [
                        {
                            "provider_call_id": None,
                            "tool_id": "files.read",
                            "tool_version": "1.0.0",
                            "arguments": {"path": "README.md"},
                        }
                    ],
                    "finish_reason": "stop",
                },
            }
        )


def test_v2_rejects_chat_tool_result_and_unbounded_tool_arguments() -> None:
    with pytest.raises(ProtocolError, match="unsupported_message"):
        validate_envelope(
            {
                "v": 2,
                "id": "step-1",
                "type": "chat.tool.result",
                "payload": {},
            }
        )


@pytest.mark.parametrize(
    ("field", "value"),
    (
        ("approval_token", "must-not-cross-worker-boundary"),
        ("receipt", {"status": "succeeded"}),
        ("terminal", {"command": "git status"}),
    ),
)
def test_v2_rejects_unknown_envelope_authority_fields(field: str, value: object) -> None:
    envelope = start_envelope()
    envelope[field] = value

    with pytest.raises(ProtocolError, match="invalid_envelope"):
        validate_envelope(envelope)


def test_v2_rejects_unknown_or_unbounded_nested_start_fields() -> None:
    unknown_payload = start_envelope()
    unknown_payload["payload"]["approval_token"] = "must-not-cross-worker-boundary"
    with pytest.raises(ProtocolError, match="invalid_request"):
        validate_envelope(unknown_payload)

    unknown_turn = start_envelope()
    unknown_turn["payload"]["turns"] = [
        {"prompt": "p", "response": "r", "receipt": {"status": "succeeded"}}
    ]
    with pytest.raises(ProtocolError, match="invalid_request"):
        validate_envelope(unknown_turn)

    oversized_turn = start_envelope()
    oversized_turn["payload"]["turns"] = [{"prompt": "p", "response": "x" * 32_001}]
    with pytest.raises(ProtocolError, match="invalid_request"):
        validate_envelope(oversized_turn)


def test_v2_rejects_unsafe_correlation_ids_before_they_can_reach_logs() -> None:
    envelope = start_envelope()
    envelope["correlation_id"] = "prompt=private secret\nforged-log-line"

    with pytest.raises(ProtocolError, match="invalid_correlation_id"):
        validate_envelope(envelope)


def test_v2_bounds_model_tool_schemas_at_protocol_ingress() -> None:
    envelope = start_envelope()
    envelope["payload"]["settings"]["model_tools"] = [
        {
            "tool_id": "files.read",
            "tool_version": "1.0.0",
            "scopes": ["workspace.read"],
            "availability": "executable",
            "model_enabled": True,
            "capability_class": "tool",
            "verification_required": False,
            "input_schema": {"type": "object", "description": "x" * (32 * 1024)},
        }
    ]

    with pytest.raises(ProtocolError, match="invalid_request"):
        validate_envelope(envelope)


def test_v2_rejects_non_finite_arguments_and_overlong_semver() -> None:
    with pytest.raises(ValueError, match="arguments"):
        ModelToolCall(None, "files.read", "1.0.0", {"offset": math.nan})

    with pytest.raises(ValueError, match="version"):
        ModelToolCall(None, "files.read", f"1.{'0' * 40}.0", {})

    with pytest.raises(ProtocolError, match="invalid_step"):
        validate_envelope(
            {
                "v": 2,
                "id": "step-1",
                "type": AGENT_STEP_DONE,
                "seq": 1,
                "payload": {
                    "text": None,
                    "tool_calls": [
                        {
                            "provider_call_id": None,
                            "tool_id": "files.read",
                            "tool_version": "1.0.0",
                            "arguments": {"x": "a" * (64 * 1024)},
                        }
                    ],
                    "finish_reason": "tool_calls",
                },
            }
        )


def test_v2_source_contract_has_no_execution_loop_or_private_authority_imports() -> None:
    package = Path(__file__).parents[1] / "src" / "ru_runtime" / "worker" / "v2"
    source = "\n".join(path.read_text(encoding="utf-8") for path in package.glob("*.py"))
    lowered = source.lower()
    for forbidden in (
        "tool_result_queue",
        "receipt wait",
        "approval request",
        "broker",
        "event_store",
        "max_tool_calls_per_turn",
    ):
        assert forbidden not in lowered
