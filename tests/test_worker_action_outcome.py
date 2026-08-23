from __future__ import annotations

import pytest

from ru_runtime.worker.v1.action_outcome import (
    ActionOutcomeError,
    is_causal_follow_up,
    parse_action_outcome,
    parse_volume_readback,
    render_action_clarification,
    render_action_explanation,
    render_action_failure,
    render_action_success,
    safe_action_noun,
)


def test_action_outcome_round_trips_only_bounded_metadata() -> None:
    outcome = parse_action_outcome(
        {
            "schema_version": 1,
            "tool_id": "system.volume",
            "tool_version": "1.0.0",
            "status": "failed",
            "reason_code": "readback_mismatch",
        }
    )

    assert outcome.to_payload() == {
        "schema_version": 1,
        "tool_id": "system.volume",
        "tool_version": "1.0.0",
        "status": "failed",
        "reason_code": "readback_mismatch",
    }
    assert "level" not in repr(outcome)


@pytest.mark.parametrize(
    "extra",
    ("arguments", "output", "prompt", "device_name", "exception"),
)
def test_action_outcome_rejects_private_or_unknown_fields(extra: str) -> None:
    payload = {
        "schema_version": 1,
        "tool_id": "system.volume",
        "tool_version": "1.0.0",
        "status": "failed",
        "reason_code": "write_failed",
        extra: "private",
    }

    with pytest.raises(ActionOutcomeError, match="invalid_action_outcome"):
        parse_action_outcome(payload)


@pytest.mark.parametrize(
    "prompt",
    ("sao vậy?", "tại sao?", "vì sao?", "sao không được?", "why?", "why not?"),
)
def test_short_causal_follow_up_is_exact_and_bounded(prompt: str) -> None:
    assert is_causal_follow_up(prompt) is True


@pytest.mark.parametrize(
    "prompt",
    (
        "Sao hôm nay trời nóng vậy?",
        "why not change it again?",
        "sao vậy anh",
        "why" * 17,
    ),
)
def test_unrelated_or_oversized_follow_up_stays_on_normal_model_path(
    prompt: str,
) -> None:
    assert is_causal_follow_up(prompt) is False


def test_verified_readback_renders_without_model_prose() -> None:
    readback = parse_volume_readback(
        "set",
        {"operation": "set", "level": 2, "muted": False},
    )

    assert render_action_success(readback) == "Em đã đặt âm lượng về 2% rồi anh."
    assert render_action_failure("âm lượng", "readback_mismatch") == (
        "Windows không xác nhận mức âm lượng sau khi thay đổi."
    )
    assert render_action_clarification("volume_command_ambiguous") == (
        "Anh muốn đặt âm lượng về bao nhiêu phần trăm, hay tăng hoặc giảm bao nhiêu?"
    )


def test_unknown_tool_degrades_to_the_generic_action_noun() -> None:
    assert safe_action_noun("files.edit") == ""
    assert safe_action_noun(" system.volume ") == "âm lượng"
    assert safe_action_noun(None) == ""

    assert render_action_failure(safe_action_noun("files.edit"), "timed_out") == (
        "Thao tác đã hết thời gian chờ."
    )
    assert render_action_failure(safe_action_noun("system.volume"), "cancelled") == (
        "Thao tác âm lượng đã bị huỷ."
    )


def test_outcomes_for_any_bounded_advertised_tool_round_trip() -> None:
    outcome = parse_action_outcome(
        {
            "schema_version": 1,
            "tool_id": "files.edit",
            "tool_version": "1.0.0",
            "status": "failed",
            "reason_code": "edit_not_unique",
        }
    )

    assert outcome.to_payload() == {
        "schema_version": 1,
        "tool_id": "files.edit",
        "tool_version": "1.0.0",
        "status": "failed",
        "reason_code": "edit_not_unique",
    }


def test_action_explanation_uses_the_same_safe_reason_map() -> None:
    outcome = parse_action_outcome(
        {
            "schema_version": 1,
            "tool_id": "system.volume",
            "tool_version": "1.0.0",
            "status": "failed",
            "reason_code": "default_endpoint_unavailable",
        }
    )

    assert render_action_explanation(outcome) == (
        "Vì Windows chưa có thiết bị phát mặc định mà em có thể truy cập."
    )


@pytest.mark.parametrize(
    "payload",
    (
        None,
        [],
        {
            "schema_version": True,
            "tool_id": "system.volume",
            "tool_version": "1.0.0",
            "status": "failed",
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
            "tool_id": "system.volume",
            "tool_version": "1.0.0",
            "status": "failed",
            "reason_code": "a" * 65,
        },
        {
            "schema_version": 1,
            "tool_id": "system.volume",
            "tool_version": "1.0.0",
            "status": "failed",
            "reason_code": 7,
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
            "status": "failed",
            "reason_code": None,
        },
    ),
)
def test_action_outcome_rejects_malformed_status_and_reason_contracts(
    payload: object,
) -> None:
    with pytest.raises(ActionOutcomeError, match="invalid_action_outcome"):
        parse_action_outcome(payload)


def test_success_outcome_requires_no_reason() -> None:
    outcome = parse_action_outcome(
        {
            "schema_version": 1,
            "tool_id": "system.volume",
            "tool_version": "1.0.0",
            "status": "succeeded",
            "reason_code": None,
        }
    )

    assert outcome.status == "succeeded"
    assert outcome.reason_code is None


@pytest.mark.parametrize(
    ("operation", "payload", "expected"),
    (
        (
            "get",
            {"operation": "get", "level": 40, "muted": False},
            "Âm lượng hiện tại là 40% và đang bật tiếng, anh.",
        ),
        (
            "get",
            {"operation": "get", "level": 0, "muted": True},
            "Âm lượng hiện tại là 0% và đang tắt tiếng, anh.",
        ),
        (
            "increase",
            {"operation": "increase", "level": 55, "muted": False},
            "Em đã tăng âm lượng lên 55% rồi anh.",
        ),
        (
            "decrease",
            {"operation": "decrease", "level": 25, "muted": False},
            "Em đã giảm âm lượng xuống 25% rồi anh.",
        ),
        (
            "mute",
            {"operation": "mute", "level": 25, "muted": True},
            "Em đã tắt tiếng máy rồi anh.",
        ),
        (
            "unmute",
            {"operation": "unmute", "level": 25, "muted": False},
            "Em đã bật lại tiếng máy rồi anh.",
        ),
    ),
)
def test_each_verified_operation_has_exact_deterministic_success_copy(
    operation: str,
    payload: dict[str, object],
    expected: str,
) -> None:
    assert render_action_success(parse_volume_readback(operation, payload)) == expected


@pytest.mark.parametrize(
    ("expected_operation", "payload"),
    (
        ("set", {"operation": "set", "level": 2}),
        ("set", {"operation": "set", "level": 2, "muted": False, "extra": 1}),
        ("set", {"operation": "increase", "level": 2, "muted": False}),
        ("set", {"operation": "set", "level": True, "muted": False}),
        ("set", {"operation": "set", "level": -1, "muted": False}),
        ("set", {"operation": "set", "level": 101, "muted": False}),
        ("set", {"operation": "set", "level": 2, "muted": 0}),
        ("unknown", {"operation": "unknown", "level": 2, "muted": False}),
    ),
)
def test_malformed_or_mismatched_volume_readback_is_never_success(
    expected_operation: str,
    payload: object,
) -> None:
    with pytest.raises(ActionOutcomeError, match="invalid_volume_readback"):
        parse_volume_readback(expected_operation, payload)


def test_unknown_failure_reason_uses_conservative_copy() -> None:
    assert render_action_failure("âm lượng", "future_failure") == (
        "Thao tác âm lượng thất bại và em không có lý do an toàn cụ thể hơn."
    )
    assert render_action_failure(None, "future_failure") == (
        "Thao tác thất bại và em không có lý do an toàn cụ thể hơn."
    )
