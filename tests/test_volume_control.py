from __future__ import annotations

import pytest

from ru_runtime.system.volume import (
    VolumeRequest,
    VolumeState,
    execute_volume_request,
    parse_volume_action,
    parse_volume_request,
)


class FakeVolumeController:
    def __init__(self, *, level: int = 40, muted: bool = False) -> None:
        self.state = VolumeState(level=level, muted=muted)
        self.reads = 0
        self.writes: list[tuple[str, int | bool]] = []

    def get_state(self) -> VolumeState:
        self.reads += 1
        return self.state

    def set_level(self, level: int) -> None:
        self.writes.append(("level", level))
        self.state = VolumeState(level=level, muted=self.state.muted)

    def set_muted(self, muted: bool) -> None:
        self.writes.append(("muted", muted))
        self.state = VolumeState(level=self.state.level, muted=muted)


class ReadbackMismatchController(FakeVolumeController):
    def set_level(self, level: int) -> None:
        self.writes.append(("level", level))


def test_parse_canonical_and_legacy_volume_actions() -> None:
    assert parse_volume_action("set 2") == VolumeRequest("set", level=2)
    assert parse_volume_action("increase 5") == VolumeRequest("increase", amount=5)
    assert parse_volume_action("decrease 5") == VolumeRequest("decrease", amount=5)
    assert parse_volume_action("get") == VolumeRequest("get")
    assert parse_volume_action("mute") == VolumeRequest("mute")
    assert parse_volume_action("unmute") == VolumeRequest("unmute")
    assert parse_volume_action("up") == VolumeRequest("increase", amount=4)
    assert parse_volume_action("down") == VolumeRequest("decrease", amount=4)


def test_parse_volume_request_rejects_unknown_fields_and_invalid_ranges() -> None:
    assert parse_volume_request({"operation": "set", "level": 2}) == VolumeRequest(
        "set", level=2
    )

    with pytest.raises(ValueError, match="unknown volume argument"):
        parse_volume_request({"operation": "get", "extra": True})
    with pytest.raises(ValueError, match="level"):
        parse_volume_request({"operation": "set", "level": 101})
    with pytest.raises(ValueError, match="amount"):
        parse_volume_request({"operation": "increase", "amount": 0})
    with pytest.raises(ValueError, match="integer"):
        parse_volume_request({"operation": "set", "level": True})


def test_set_volume_reads_back_exact_level() -> None:
    controller = FakeVolumeController(level=40)

    result = execute_volume_request(VolumeRequest("set", level=2), controller)

    assert result.operation == "set"
    assert result.level == 2
    assert result.muted is False
    assert result.verified is True
    assert result.error_code is None
    assert controller.reads >= 2
    assert controller.writes == [("level", 2)]


def test_relative_volume_is_clamped_and_verified() -> None:
    controller = FakeVolumeController(level=98)

    result = execute_volume_request(VolumeRequest("increase", amount=5), controller)

    assert result.level == 100
    assert result.verified is True
    assert controller.writes == [("level", 100)]


def test_readback_mismatch_is_not_success() -> None:
    controller = ReadbackMismatchController(level=40)

    result = execute_volume_request(VolumeRequest("set", level=2), controller)

    assert result.verified is False
    assert result.error_code == "readback_mismatch"
    assert result.level == 40

