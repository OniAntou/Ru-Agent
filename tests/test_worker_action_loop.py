from __future__ import annotations

import copy

import pytest

from ru_runtime.worker.v1.action_loop import (
    ActionProtocolError,
    parse_model_capabilities,
    validate_receipt,
    validate_selection,
)


def _settings() -> dict[str, object]:
    return {
        "model_tools": [
            {
                "tool_id": "system.volume",
                "tool_version": "1.0.0",
                "scopes": ["system.write"],
                "availability": "native_pending",
                "model_enabled": True,
                "capability_class": "native_mutation",
            }
        ]
    }


def test_model_capabilities_are_strict_bounded_and_immutable() -> None:
    settings = _settings()
    capabilities = parse_model_capabilities(settings)
    settings["model_tools"][0]["scopes"].append("terminal.execute")  # type: ignore[index,union-attr]

    capability = capabilities[0]
    assert isinstance(capabilities, tuple)
    assert capability.tool_id == "system.volume"
    assert capability.scopes == ("system.write",)


@pytest.mark.parametrize(
    "mutation",
    (
        "not_array",
        "duplicate",
        "extra_field",
        "too_many",
        "availability",
        "capability_class",
        "model_disabled",
    ),
)
def test_model_capabilities_fail_closed(mutation: str) -> None:
    settings = copy.deepcopy(_settings())
    tools = settings["model_tools"]
    assert isinstance(tools, list)
    if mutation == "not_array":
        settings["model_tools"] = {}
    elif mutation == "duplicate":
        tools.append(copy.deepcopy(tools[0]))
    elif mutation == "extra_field":
        tools[0]["risk"] = "reversible"
    elif mutation == "too_many":
        settings["model_tools"] = [
            {**copy.deepcopy(tools[0]), "tool_id": f"tool.{index}"}
            for index in range(17)
        ]
    elif mutation == "availability":
        tools[0]["availability"] = "unknown"
    elif mutation == "capability_class":
        tools[0]["capability_class"] = "internal"
    else:
        tools[0]["model_enabled"] = False

    with pytest.raises(ActionProtocolError, match="invalid_model_tools"):
        parse_model_capabilities(settings)


def test_selection_requires_one_exact_advertised_binding() -> None:
    capabilities = parse_model_capabilities(_settings())
    selection = validate_selection(
        '{"tool_id":"system.volume","tool_version":"1.0.0",'
        '"arguments":{"operation":"set","level":20}}',
        capabilities,
    )

    assert selection is not None
    assert selection.tool_id == "system.volume"
    assert selection.capability_class == "native_mutation"
    assert selection.arguments == {"operation": "set", "level": 20}

    with pytest.raises(ActionProtocolError, match="capability_unavailable"):
        validate_selection(
            '{"tool_id":"app.open","tool_version":"1.0.0",'
            '"arguments":{"target":"browser"}}',
            capabilities,
        )


def test_legacy_marker_selection_discards_unverified_surrounding_prose() -> None:
    selection = validate_selection(
        "[VOLUME: set 2] Em đã chỉnh xong.",
        parse_model_capabilities(_settings()),
    )

    assert selection is not None
    assert selection.arguments == {"operation": "set", "level": 2}


@pytest.mark.parametrize(
    "text",
    (
        '[{"tool_id":"system.volume","tool_version":"1.0.0","arguments":{"operation":"mute"}},'
        '{"tool_id":"system.volume","tool_version":"1.0.0","arguments":{"operation":"unmute"}}]',
        '{"tool_id":"system.volume","tool_version":"1.0.0","arguments":{"operation":"set","level":101}}',
        '{"tool_id":"system.volume","tool_version":"1.0.0","arguments":{"operation":"mute","extra":true}}',
    ),
)
def test_selection_rejects_multiple_or_invalid_calls(text: str) -> None:
    with pytest.raises(ActionProtocolError, match="invalid_selection"):
        validate_selection(text, parse_model_capabilities(_settings()))


def _selection():
    selection = validate_selection(
        '{"tool_id":"system.volume","tool_version":"1.0.0",'
        '"arguments":{"operation":"set","level":20}}',
        parse_model_capabilities(_settings()),
    )
    assert selection is not None
    return selection


def _receipt() -> dict[str, object]:
    return {
        "request_id": "request-1",
        "session_id": "session-1",
        "correlation_id": "correlation-1",
        "status": "succeeded",
        "output": {"operation": "set", "level": 20, "muted": False},
        "error_code": None,
        "safe_summary": "Volume change verified.",
        "duration_ms": 12,
        "verification_state": "verified",
        "endpoint_role": "default_render_multimedia",
        "endpoint_hash": "0123456789abcdef",
    }


def _validate(payload: dict[str, object], *, deadline_at: float = 10.0, now: float = 5.0):
    return validate_receipt(
        _selection(),
        payload,
        expected_request_id="request-1",
        expected_session_id="session-1",
        expected_correlation_id="correlation-1",
        deadline_at=deadline_at,
        now=now,
    )


def test_verified_success_receipt_is_accepted_without_exposing_endpoint_proof() -> None:
    receipt = _validate(_receipt())

    assert receipt.status == "succeeded"
    assert receipt.verification_state == "verified"
    assert receipt.duration_ms == 12
    assert receipt.readback is not None
    assert receipt.readback.operation == "set"
    assert receipt.readback.level == 20
    assert receipt.readback.muted is False
    assert "endpoint" not in repr(receipt).lower()
    assert "0123456789abcdef" not in repr(receipt)
    assert "level" not in repr(receipt).lower()


@pytest.mark.parametrize(
    "output",
    (
        {"operation": "increase", "level": 20, "muted": False},
        {"operation": "set", "level": 20, "muted": False, "extra": True},
        {"operation": "set", "level": True, "muted": False},
        {"operation": "set", "level": 101, "muted": False},
        {"operation": "set", "level": 20, "muted": 0},
    ),
)
def test_success_receipt_requires_exact_volume_readback(
    output: dict[str, object],
) -> None:
    payload = _receipt()
    payload["output"] = output

    with pytest.raises(ActionProtocolError, match="invalid_receipt"):
        _validate(payload)


@pytest.mark.parametrize("verification", ("pending", "failed", None))
def test_success_without_verified_state_fails_closed(verification: str | None) -> None:
    payload = _receipt()
    if verification is None:
        payload.pop("verification_state")
    else:
        payload["verification_state"] = verification

    with pytest.raises(ActionProtocolError, match="receipt_unverified"):
        _validate(payload)


def test_terminal_failure_preserves_only_safe_error_metadata() -> None:
    payload = _receipt()
    payload.update(
        status="failed",
        error_code="readback_mismatch",
        safe_summary="Volume change could not be verified.",
        verification_state="failed",
    )
    receipt = _validate(payload)

    assert receipt.status == "failed"
    assert receipt.error_code == "readback_mismatch"
    assert receipt.readback is None
    assert not hasattr(receipt, "output")


@pytest.mark.parametrize(
    ("field", "value"),
    (
        ("request_id", "wrong"),
        ("session_id", "wrong"),
        ("correlation_id", "wrong"),
    ),
)
def test_receipt_context_mismatch_is_rejected(field: str, value: str) -> None:
    payload = _receipt()
    payload[field] = value

    with pytest.raises(ActionProtocolError, match="receipt_mismatch"):
        _validate(payload)


def test_receipt_after_deadline_is_rejected() -> None:
    with pytest.raises(ActionProtocolError, match="receipt_late"):
        _validate(_receipt(), deadline_at=4.0, now=5.0)


@pytest.mark.parametrize(
    ("field", "value"),
    (
        ("endpoint_role", "private-device-name"),
        ("endpoint_hash", "ABCDEF0123456789"),
        ("endpoint_hash", "short"),
    ),
)
def test_receipt_endpoint_proof_is_strict(field: str, value: str) -> None:
    payload = _receipt()
    payload[field] = value

    with pytest.raises(ActionProtocolError, match="invalid_receipt"):
        _validate(payload)
