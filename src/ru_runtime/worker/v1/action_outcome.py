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
_MAX_TOOL_ID_CHARS = 128
_MAX_TOOL_VERSION_CHARS = 32
_ACTION_NOUNS = {"system.volume": "âm lượng"}
_FAILURES = {
    "capability_unavailable": (
        "Chế độ Chat chỉ đọc dữ liệu. "
        "Bật Agent mode để em điều khiển {action} nha."
    ),
    "agent_unavailable": (
        "Tính năng điều khiển {action} chưa sẵn sàng trong phiên này."
    ),
    "scope_not_granted": (
        "Phiên thao tác này chưa được cấp quyền điều khiển {action}."
    ),
    "invalid_arguments": (
        "Lệnh {action} không hợp lệ nên em chưa gửi thay đổi tới Windows."
    ),
    "unsupported_platform": "Điều khiển {action} hiện chỉ hỗ trợ Windows.",
    "default_endpoint_unavailable": (
        "Windows chưa có thiết bị phát mặc định mà em có thể truy cập."
    ),
    "read_failed": "Em không đọc được trạng thái {action} hiện tại.",
    "write_failed": "Windows không chấp nhận thay đổi {action}.",
    "readback_mismatch": (
        "Windows không xác nhận mức {action} sau khi thay đổi."
    ),
    "adapter_error": (
        "Bộ điều khiển {action} gặp lỗi nên em không coi thao tác là thành công."
    ),
    "receipt_unverified": (
        "Kết quả thay đổi chưa được xác minh nên em không coi thao tác là thành công."
    ),
    "receipt_mismatch": "Kết quả trả về không khớp với thao tác vừa yêu cầu.",
    "invalid_receipt": (
        "Kết quả thao tác không hợp lệ nên em không coi đó là thành công."
    ),
    "cancelled": "Thao tác {action} đã bị huỷ.",
    "timed_out": "Thao tác {action} đã hết thời gian chờ.",
    "receipt_late": "Thao tác {action} đã hết thời gian chờ.",
}
_FAILURE_FALLBACK = (
    "Thao tác {action} thất bại và em không có lý do an toàn cụ thể hơn."
)
_CLARIFICATIONS = {
    "volume_value_out_of_range": (
        "Mức âm lượng phải từ 0 đến 100; mức tăng hoặc giảm phải từ 1 đến 50."
    ),
    "volume_capability_question": (
        "Em có thể dùng công cụ âm lượng khi tính năng này sẵn sàng. "
        "Anh muốn đặt, tăng, giảm, tắt tiếng hay bật tiếng?"
    ),
}
_CLARIFICATION_FALLBACK = (
    "Anh muốn đặt âm lượng về bao nhiêu phần trăm, hay tăng hoặc giảm bao nhiêu?"
)


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
        or not isinstance(tool_id, str)
        or not tool_id.strip()
        or len(tool_id) > _MAX_TOOL_ID_CHARS
        or not isinstance(tool_version, str)
        or not tool_version.strip()
        or len(tool_version) > _MAX_TOOL_VERSION_CHARS
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
        tool_id.strip(),
        tool_version.strip(),
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


def safe_action_noun(tool_id: object) -> str:
    """Bounded Vietnamese action noun for an advertised tool id.

    An empty noun degrades every failure template to the generic
    "Thao tác …" phrasing instead of leaking a tool id into user copy.
    """
    if isinstance(tool_id, str):
        noun = _ACTION_NOUNS.get(tool_id.strip().casefold())
        if noun is not None:
            return noun
    return ""


def render_action_success(readback: VolumeReadback) -> str:
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


def render_action_clarification(reason_code: str | None) -> str:
    if reason_code in _CLARIFICATIONS:
        return _CLARIFICATIONS[reason_code]
    return _CLARIFICATION_FALLBACK


def render_action_failure(safe_action: str | None, reason_code: str | None) -> str:
    noun = safe_action.strip()[:64] if isinstance(safe_action, str) else ""
    template = _FAILURES.get(reason_code or "", _FAILURE_FALLBACK)
    return " ".join(template.replace("{action}", noun).split())


def render_action_explanation(outcome: ActionOutcome) -> str:
    if outcome.status == "clarification_required":
        reason = render_action_clarification(outcome.reason_code)
    else:
        reason = render_action_failure(safe_action_noun(outcome.tool_id), outcome.reason_code)
    if reason.startswith("Windows"):
        return f"Vì {reason}"
    return f"Vì {reason[0].lower()}{reason[1:]}"
