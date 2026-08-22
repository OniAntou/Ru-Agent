"""Typed, readback-verified Windows volume operations.

The runtime and Broker expose the same small operation contract, while the
native controller stays behind this injectable boundary so tests never touch
the user's audio device.
"""

from __future__ import annotations

import logging
import platform
from collections.abc import Callable, Mapping
from ctypes import POINTER, cast
from dataclasses import dataclass
from typing import Any, Literal, Protocol

logger = logging.getLogger(__name__)

VOLUME_TOOL_ID = "system.volume"
VOLUME_TOOL_VERSION = "1.0.0"
LEGACY_RELATIVE_AMOUNT = 4

VolumeOperation = Literal["get", "set", "increase", "decrease", "mute", "unmute"]
_OPERATIONS = frozenset({"get", "set", "increase", "decrease", "mute", "unmute"})
_ARGUMENT_FIELDS = frozenset({"operation", "level", "amount"})


class VolumeAdapterError(RuntimeError):
    """An adapter failure represented by a stable, safe error code."""

    def __init__(self, error_code: str) -> None:
        super().__init__(error_code)
        self.error_code = error_code


@dataclass(frozen=True, slots=True)
class VolumeRequest:
    operation: VolumeOperation | str
    level: int | None = None
    amount: int | None = None


@dataclass(frozen=True, slots=True)
class VolumeState:
    level: int
    muted: bool


@dataclass(frozen=True, slots=True)
class VolumeResult:
    operation: str
    level: int | None
    muted: bool | None
    verified: bool
    error_code: str | None = None

    @property
    def succeeded(self) -> bool:
        return self.verified and self.error_code is None

    def to_public_dict(self) -> dict[str, Any]:
        """Return only the bounded result fields shared with the Broker."""

        return {
            "operation": self.operation,
            "level": self.level,
            "muted": self.muted,
            "verified": self.verified,
        }


class VolumeController(Protocol):
    """Minimal controller required by the canonical executor."""

    def get_state(self) -> VolumeState:
        ...

    def set_level(self, level: int) -> None:
        ...

    def set_muted(self, muted: bool) -> None:
        ...


class ManagedVolumeController(VolumeController, Protocol):
    """A controller whose native resources are owned by one operation."""

    def close(self) -> None:
        ...


def _is_integer(value: object) -> bool:
    return isinstance(value, int) and not isinstance(value, bool)


def _validate_level(level: object) -> int:
    if not _is_integer(level):
        raise ValueError("volume level must be an integer")
    if not 0 <= level <= 100:
        raise ValueError("volume level must be between 0 and 100")
    return level


def _validate_amount(amount: object) -> int:
    if not _is_integer(amount):
        raise ValueError("volume amount must be an integer")
    if not 1 <= amount <= 50:
        raise ValueError("volume amount must be between 1 and 50")
    return amount


def validate_volume_request(request: VolumeRequest) -> VolumeRequest:
    """Validate and normalize one canonical request."""

    operation = request.operation.strip().lower() if isinstance(request.operation, str) else ""
    if operation not in _OPERATIONS:
        raise ValueError("unknown volume operation")

    if operation == "set":
        if request.amount is not None:
            raise ValueError("set does not accept amount")
        return VolumeRequest(operation, level=_validate_level(request.level))

    if operation in {"increase", "decrease"}:
        if request.level is not None:
            raise ValueError(f"{operation} does not accept level")
        amount = LEGACY_RELATIVE_AMOUNT if request.amount is None else _validate_amount(request.amount)
        return VolumeRequest(operation, amount=amount)

    if request.level is not None or request.amount is not None:
        raise ValueError(f"{operation} does not accept level or amount")
    return VolumeRequest(operation)  # type: ignore[arg-type]


def parse_volume_request(arguments: Mapping[str, object]) -> VolumeRequest:
    """Parse structured tool arguments into the canonical request."""

    if not isinstance(arguments, Mapping):
        raise ValueError("volume arguments must be an object")
    unknown = set(arguments) - _ARGUMENT_FIELDS
    if unknown:
        raise ValueError("unknown volume argument")
    if "operation" not in arguments:
        raise ValueError("volume operation is required")
    operation = arguments["operation"]
    if not isinstance(operation, str):
        raise ValueError("volume operation must be a string")
    request = VolumeRequest(
        operation=operation,
        level=arguments.get("level"),
        amount=arguments.get("amount"),
    )
    return validate_volume_request(request)


def _parse_amount(value: str, field: str) -> int:
    try:
        return int(value)
    except (TypeError, ValueError) as error:
        raise ValueError(f"volume {field} must be an integer") from error


def parse_volume_action(action: str) -> VolumeRequest:
    """Parse a chat marker body, including the legacy up/down aliases."""

    if not isinstance(action, str):
        raise ValueError("volume action must be text")
    normalized = " ".join(action.strip().lower().split())
    aliases: dict[str, VolumeRequest] = {
        "up": VolumeRequest("increase", amount=LEGACY_RELATIVE_AMOUNT),
        "tăng": VolumeRequest("increase", amount=LEGACY_RELATIVE_AMOUNT),
        "tang": VolumeRequest("increase", amount=LEGACY_RELATIVE_AMOUNT),
        "tăng âm lượng": VolumeRequest("increase", amount=LEGACY_RELATIVE_AMOUNT),
        "tang am luong": VolumeRequest("increase", amount=LEGACY_RELATIVE_AMOUNT),
        "louder": VolumeRequest("increase", amount=LEGACY_RELATIVE_AMOUNT),
        "down": VolumeRequest("decrease", amount=LEGACY_RELATIVE_AMOUNT),
        "giảm": VolumeRequest("decrease", amount=LEGACY_RELATIVE_AMOUNT),
        "giam": VolumeRequest("decrease", amount=LEGACY_RELATIVE_AMOUNT),
        "giảm âm lượng": VolumeRequest("decrease", amount=LEGACY_RELATIVE_AMOUNT),
        "giam am luong": VolumeRequest("decrease", amount=LEGACY_RELATIVE_AMOUNT),
        "quieter": VolumeRequest("decrease", amount=LEGACY_RELATIVE_AMOUNT),
        "mute": VolumeRequest("mute"),
        "tắt tiếng": VolumeRequest("mute"),
        "tat tieng": VolumeRequest("mute"),
        "im lặng": VolumeRequest("mute"),
        "im lang": VolumeRequest("mute"),
        "unmute": VolumeRequest("unmute"),
        "bật tiếng": VolumeRequest("unmute"),
        "bat tieng": VolumeRequest("unmute"),
    }
    if normalized in aliases:
        return aliases[normalized]

    parts = normalized.split()
    if len(parts) == 1 and parts[0] in {"get", "mute", "unmute"}:
        return VolumeRequest(parts[0])  # type: ignore[arg-type]
    if len(parts) != 2 or parts[0] not in {"set", "increase", "decrease"}:
        raise ValueError("unknown volume action")
    if parts[0] == "set":
        return validate_volume_request(VolumeRequest("set", level=_parse_amount(parts[1], "level")))
    return validate_volume_request(
        VolumeRequest(parts[0], amount=_parse_amount(parts[1], "amount"))  # type: ignore[arg-type]
    )


def _failed_result(
    request: VolumeRequest,
    state: VolumeState | None,
    error_code: str,
) -> VolumeResult:
    return VolumeResult(
        operation=str(request.operation),
        level=state.level if state is not None else None,
        muted=state.muted if state is not None else None,
        verified=False,
        error_code=error_code,
    )


def execute_volume_request(
    request: VolumeRequest,
    controller: VolumeController,
) -> VolumeResult:
    """Execute a validated request and verify writes by reading state back."""

    normalized = validate_volume_request(request)
    before: VolumeState | None = None
    try:
        before = controller.get_state()
        operation = str(normalized.operation)
        if operation == "get":
            return VolumeResult(operation, before.level, before.muted, True)
        if operation == "set":
            controller.set_level(normalized.level)  # type: ignore[arg-type]
            expected_level = normalized.level
            expected_muted = before.muted
        elif operation == "increase":
            expected_level = min(100, before.level + normalized.amount)  # type: ignore[operator]
            controller.set_level(expected_level)
            expected_muted = before.muted
        elif operation == "decrease":
            expected_level = max(0, before.level - normalized.amount)  # type: ignore[operator]
            controller.set_level(expected_level)
            expected_muted = before.muted
        else:
            expected_level = before.level
            expected_muted = operation == "unmute"
            controller.set_muted(expected_muted)

        after = controller.get_state()
        if after.level != expected_level or after.muted != expected_muted:
            return _failed_result(normalized, after, "readback_mismatch")
        return VolumeResult(operation, after.level, after.muted, True)
    except VolumeAdapterError as error:
        logger.warning("Volume adapter failed for operation %s: %s", normalized.operation, error.error_code)
        return _failed_result(normalized, before, error.error_code)
    except Exception:
        logger.exception("Volume adapter failed for operation %s", normalized.operation)
        return _failed_result(normalized, before, "adapter_error")


class WindowsCoreAudioVolumeController:
    """Default-render endpoint controller backed by pycaw and comtypes."""

    def __init__(self) -> None:
        self._endpoint: Any = None
        self._co_uninitialize: Callable[[], None] | None = None
        if platform.system() != "Windows":
            raise VolumeAdapterError("unsupported_platform")
        try:
            from comtypes import CLSCTX_ALL, CoInitialize, CoUninitialize
            from pycaw.pycaw import AudioUtilities, IAudioEndpointVolume
        except ImportError as error:
            raise VolumeAdapterError("audio_backend_unavailable") from error

        try:
            CoInitialize()
            self._co_uninitialize = CoUninitialize
            device = AudioUtilities.GetSpeakers()
            endpoint = getattr(device, "EndpointVolume", None)
            if endpoint is None:
                interface = device.Activate(IAudioEndpointVolume._iid_, CLSCTX_ALL, None)
                endpoint = cast(interface, POINTER(IAudioEndpointVolume))
            self._endpoint = endpoint
        except Exception as error:
            self.close()
            raise VolumeAdapterError("default_endpoint_unavailable") from error

    def close(self) -> None:
        """Release the endpoint before balancing this thread's COM apartment."""

        self._endpoint = None
        co_uninitialize = self._co_uninitialize
        self._co_uninitialize = None
        if co_uninitialize is not None:
            co_uninitialize()

    def get_state(self) -> VolumeState:
        try:
            scalar = float(self._endpoint.GetMasterVolumeLevelScalar())
            level = max(0, min(100, round(scalar * 100)))
            return VolumeState(level=level, muted=bool(self._endpoint.GetMute()))
        except Exception as error:
            raise VolumeAdapterError("read_failed") from error

    def set_level(self, level: int) -> None:
        try:
            self._endpoint.SetMasterVolumeLevelScalar(max(0, min(100, level)) / 100.0, None)
        except Exception as error:
            raise VolumeAdapterError("write_failed") from error

    def set_muted(self, muted: bool) -> None:
        try:
            self._endpoint.SetMute(1 if muted else 0, None)
        except Exception as error:
            raise VolumeAdapterError("write_failed") from error


def create_default_volume_controller() -> ManagedVolumeController:
    return WindowsCoreAudioVolumeController()


def execute_default_volume_request(request: VolumeRequest) -> VolumeResult:
    """Execute one request while balancing the native controller lifecycle."""

    controller = create_default_volume_controller()
    try:
        return execute_volume_request(request, controller)
    finally:
        controller.close()
