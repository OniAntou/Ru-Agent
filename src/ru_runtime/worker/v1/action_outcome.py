from __future__ import annotations

import re
import unicodedata
from collections.abc import Mapping
from dataclasses import dataclass
from typing import Literal, cast


ActionOutcomeStatus = Literal[
    "succeeded",
    "failed",
    "denied",
    "approval_required",
    "cancelled",
    "timed_out",
    "clarification_required",
]

_FIELDS = frozenset(
    ("schema_version", "tool_id", "tool_version", "status", "reason_code")
)
_STATUSES = frozenset(
    (
        "succeeded",
        "failed",
        "denied",
        "approval_required",
        "cancelled",
        "timed_out",
        "clarification_required",
    )
)
_SAFE_CODE = re.compile(r"^[a-z0-9][a-z0-9_.-]{0,63}$")
_CAUSAL_FOLLOW_UPS = frozenset(
    ("sao vậy", "tại sao", "vì sao", "sao không được", "why", "why not")
)
_FAILURES = {
    "capability_unavailable": (
        "Tính năng điều khiển âm lượng chưa sẵn sàng trong phiên này."
    ),
    "agent_unavailable": (
        "Tính năng điều khiển âm lượng chưa sẵn sàng trong phiên này."
    ),
    "scope_not_granted": (
        "Phiên thao tác này chưa được cấp quyền điều khiển âm lượng."
    ),
    "invalid_arguments": (
        "Lệnh âm lượng không hợp lệ nên em chưa gửi thay đổi tới Windows."
    ),
    "unsupported_platform": "Điều khiển âm lượng hiện chỉ hỗ trợ Windows.",
    "default_endpoint_unavailable": (
        "Windows chưa có thiết bị phát mặc định mà em có thể truy cập."
    ),
    "read_failed": "Em không đọc được trạng thái âm lượng hiện tại.",
    "write_failed": "Windows không chấp nhận thay đổi âm lượng.",
    "readback_mismatch": (
        "Windows không xác nhận mức âm lượng sau khi thay đổi."
    ),
    "adapter_error": (
        "Bộ điều khiển âm lượng gặp lỗi nên em không coi thao tác là thành công."
    ),
    "receipt_unverified": (
        "Kết quả thay đổi chưa được xác minh nên em không coi thao tác là thành công."
    ),
    "receipt_mismatch": "Kết quả trả về không khớp với thao tác vừa yêu cầu.",
    "invalid_receipt": (
        "Kết quả thao tác không hợp lệ nên em không coi đó là thành công."
    ),
    "cancelled": "Thao tác âm lượng đã bị huỷ.",
    "timed_out": "Thao tác âm lượng đã hết thời gian chờ.",
    "receipt_late": "Thao tác âm lượng đã hết thời gian chờ.",
}
_FALLBACK = "Thao tác âm lượng thất bại và em không có lý do an toàn cụ thể hơn."


class ActionOutcomeError(ValueError):
    pass


@dataclass(frozen=True, slots=True)
class ActionOutcome:
    schema_version: int
    tool_id: str
    tool_version: str
    status: ActionOutcomeStatus
    reason_code: str | None

    def to_payload(self) -> dict[str, object]:
        return {
            "schema_version": self.schema_version,
            "tool_id": self.tool_id,
            "tool_version": self.tool_version,
            "status": self.status,
            "reason_code": self.reason_code,
        }


@dataclass(frozen=True, slots=True)
class VolumeReadback:
    operation: str
    level: int
    muted: bool


def parse_action_outcome(value: object) -> ActionOutcome:
    if not isinstance(value, Mapping) or set(value) != _FIELDS:
        raise ActionOutcomeError("invalid_action_outcome")

    schema_version = value.get("schema_version")
    tool_id = value.get("tool_id")
    tool_version = value.get("tool_version")
    status = value.get("status")
    reason_code = value.get("reason_code")
    if (
        schema_version != 1
        or isinstance(schema_version, bool)
        or tool_id != "system.volume"
        or tool_version != "1.0.0"
        or not isinstance(status, str)
        or status not in _STATUSES
        or (status == "succeeded" and reason_code is not None)
        or (
            status != "succeeded"
            and (
                not isinstance(reason_code, str)
                or _SAFE_CODE.fullmatch(reason_code) is None
            )
        )
    ):
        raise ActionOutcomeError("invalid_action_outcome")

    return ActionOutcome(
        1,
        "system.volume",
        "1.0.0",
        cast(ActionOutcomeStatus, status),
        cast(str | None, reason_code),
    )


def parse_optional_action_outcome(value: object) -> ActionOutcome | None:
    return None if value is None else parse_action_outcome(value)


def parse_volume_readback(expected_operation: str, value: object) -> VolumeReadback:
    if not isinstance(value, Mapping) or set(value) != {
        "operation",
        "level",
        "muted",
    }:
        raise ActionOutcomeError("invalid_volume_readback")

    operation = value.get("operation")
    level = value.get("level")
    muted = value.get("muted")
    if (
        operation != expected_operation
        or operation
        not in {"get", "set", "increase", "decrease", "mute", "unmute"}
        or isinstance(level, bool)
        or not isinstance(level, int)
        or not 0 <= level <= 100
        or not isinstance(muted, bool)
    ):
        raise ActionOutcomeError("invalid_volume_readback")
    return VolumeReadback(operation, level, muted)


def is_causal_follow_up(prompt: str) -> bool:
    if not isinstance(prompt, str) or len(prompt) > 64:
        return False
    normalized = " ".join(
        unicodedata.normalize("NFKC", prompt).casefold().split()
    ).rstrip("?!.")
    return normalized in _CAUSAL_FOLLOW_UPS


def render_volume_success(readback: VolumeReadback) -> str:
    if readback.operation == "get":
        state = "đang tắt tiếng" if readback.muted else "đang bật tiếng"
        return f"Âm lượng hiện tại là {readback.level}% và {state}, anh."
    if readback.operation == "set":
        return f"Em đã đặt âm lượng về {readback.level}% rồi anh."
    if readback.operation == "increase":
        return f"Em đã tăng âm lượng lên {readback.level}% rồi anh."
    if readback.operation == "decrease":
        return f"Em đã giảm âm lượng xuống {readback.level}% rồi anh."
    if readback.operation == "mute":
        return "Em đã tắt tiếng máy rồi anh."
    return "Em đã bật lại tiếng máy rồi anh."


def render_volume_clarification(reason_code: str) -> str:
    if reason_code == "volume_value_out_of_range":
        return (
            "Mức âm lượng phải từ 0 đến 100; mức tăng hoặc giảm phải từ 1 đến 50."
        )
    if reason_code == "volume_capability_question":
        return (
            "Em có thể dùng công cụ âm lượng khi tính năng này sẵn sàng. "
            "Anh muốn đặt, tăng, giảm, tắt tiếng hay bật tiếng?"
        )
    return "Anh muốn đặt âm lượng về bao nhiêu phần trăm, hay tăng hoặc giảm bao nhiêu?"


def render_volume_failure(reason_code: str | None) -> str:
    return _FAILURES.get(reason_code or "", _FALLBACK)


def render_action_explanation(outcome: ActionOutcome) -> str:
    if outcome.status == "clarification_required":
        reason = render_volume_clarification(outcome.reason_code or "")
    else:
        reason = render_volume_failure(outcome.reason_code)
    if reason.startswith("Windows"):
        return f"Vì {reason}"
    return f"Vì {reason[0].lower()}{reason[1:]}"
