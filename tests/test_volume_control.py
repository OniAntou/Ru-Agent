from __future__ import annotations

import concurrent.futures
import sys
import threading
from types import ModuleType, SimpleNamespace

import pytest

from ru_runtime.system import volume as volume_module
from ru_runtime.system.volume import (
    VolumeRequest,
    VolumeResult,
    VolumeState,
    WindowsCoreAudioVolumeController,
    execute_default_volume_request,
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


def test_windows_controller_uses_current_pycaw_endpoint_api(monkeypatch: pytest.MonkeyPatch) -> None:
    class Endpoint:
        def GetMasterVolumeLevelScalar(self) -> float:
            return 0.37

        def GetMute(self) -> int:
            return 1

    endpoint = Endpoint()
    device = SimpleNamespace(EndpointVolume=endpoint)
    comtypes = ModuleType("comtypes")
    comtypes.CLSCTX_ALL = 7  # type: ignore[attr-defined]
    comtypes.CoInitialize = lambda: None  # type: ignore[attr-defined]
    comtypes.CoUninitialize = lambda: None  # type: ignore[attr-defined]
    pycaw_package = ModuleType("pycaw")
    pycaw_package.__path__ = []  # type: ignore[attr-defined]
    pycaw_module = ModuleType("pycaw.pycaw")
    pycaw_module.AudioUtilities = SimpleNamespace(GetSpeakers=lambda: device)  # type: ignore[attr-defined]
    pycaw_module.IAudioEndpointVolume = SimpleNamespace(_iid_="endpoint-volume")  # type: ignore[attr-defined]

    monkeypatch.setattr(volume_module.platform, "system", lambda: "Windows")
    monkeypatch.setitem(sys.modules, "comtypes", comtypes)
    monkeypatch.setitem(sys.modules, "pycaw", pycaw_package)
    monkeypatch.setitem(sys.modules, "pycaw.pycaw", pycaw_module)

    controller = WindowsCoreAudioVolumeController()
    try:
        assert controller.get_state() == VolumeState(level=37, muted=True)
    finally:
        controller.close()


def test_volume_execution_balances_com_on_worker_thread(monkeypatch: pytest.MonkeyPatch) -> None:
    apartment = threading.local()
    events: list[tuple[str, int]] = []

    def co_initialize() -> None:
        apartment.initialized = True
        events.append(("initialize", threading.get_ident()))

    def co_uninitialize() -> None:
        events.append(("uninitialize", threading.get_ident()))
        apartment.initialized = False

    class Endpoint:
        def GetMasterVolumeLevelScalar(self) -> float:
            return 0.42

        def GetMute(self) -> int:
            return 0

    def get_speakers() -> SimpleNamespace:
        if not getattr(apartment, "initialized", False):
            raise OSError("CoInitialize has not been called")
        events.append(("get_speakers", threading.get_ident()))
        return SimpleNamespace(EndpointVolume=Endpoint())

    comtypes = ModuleType("comtypes")
    comtypes.CLSCTX_ALL = 7  # type: ignore[attr-defined]
    comtypes.CoInitialize = co_initialize  # type: ignore[attr-defined]
    comtypes.CoUninitialize = co_uninitialize  # type: ignore[attr-defined]
    pycaw_package = ModuleType("pycaw")
    pycaw_package.__path__ = []  # type: ignore[attr-defined]
    pycaw_module = ModuleType("pycaw.pycaw")
    pycaw_module.AudioUtilities = SimpleNamespace(GetSpeakers=get_speakers)  # type: ignore[attr-defined]
    pycaw_module.IAudioEndpointVolume = SimpleNamespace(_iid_="endpoint-volume")  # type: ignore[attr-defined]

    monkeypatch.setattr(volume_module.platform, "system", lambda: "Windows")
    monkeypatch.setitem(sys.modules, "comtypes", comtypes)
    monkeypatch.setitem(sys.modules, "pycaw", pycaw_package)
    monkeypatch.setitem(sys.modules, "pycaw.pycaw", pycaw_module)

    def read_volume() -> tuple[VolumeResult, int]:
        result = execute_default_volume_request(VolumeRequest("get"))
        return result, threading.get_ident()

    with concurrent.futures.ThreadPoolExecutor(max_workers=1) as pool:
        result, worker_thread_id = pool.submit(read_volume).result()

    assert result.succeeded is True
    assert result.level == 42
    assert events == [
        ("initialize", worker_thread_id),
        ("get_speakers", worker_thread_id),
        ("uninitialize", worker_thread_id),
    ]
