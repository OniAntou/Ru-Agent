from __future__ import annotations

import contextlib

import pytest
from fastapi.testclient import TestClient

import mini_gpt._server_shared as server_shared
import mini_gpt.headless_runner as headless_runner
import mini_gpt.routers.chat as chat_router
import mini_gpt.routers.observation as observation_router
import mini_gpt.routers.vts as vts_router
import mini_gpt.server as server
from mini_gpt.screen_reaction import ReactionCoordinator
from mini_gpt.vts_config import MotionConfig
from mini_gpt.vts_state import VtsMotionState


@pytest.fixture(autouse=True)
def isolated_state(monkeypatch, tmp_path):
    monkeypatch.setattr(server_shared, "SETTINGS_FILE", tmp_path / "settings.json")
    monkeypatch.setattr(server, "SETTINGS_FILE", tmp_path / "settings.json")
    monkeypatch.setattr(server_shared, "HISTORY_FILE", tmp_path / "history.json")
    monkeypatch.setattr(server, "HISTORY_FILE", tmp_path / "history.json")
    monkeypatch.setattr(server_shared, "SESSION_DIR", tmp_path / "sessions")
    server.core.runner.backend = headless_runner.BACKEND_GGUF
    coordinator = ReactionCoordinator()
    monkeypatch.setattr(server_shared, "_SCREEN_REACTIONS", coordinator, raising=False)
    monkeypatch.setattr(chat_router, "_SCREEN_REACTIONS", coordinator, raising=False)
    monkeypatch.setattr(observation_router, "_SCREEN_REACTIONS", coordinator, raising=False)
    monkeypatch.setattr(server.core, "remember", lambda prompt, reply: None)
    monkeypatch.setattr(server_shared._MEMORY_QUEUE, "put_nowait", lambda item: None)
    monkeypatch.setattr(
        server_shared,
        "_GPU_LIFECYCLE",
        type(
            "NoopGpuLifecycle",
            (),
            {
                "prepare_local_generation": lambda self: None,
                "prepare_speech": lambda self, selection: None,
                "local_generation": contextlib.nullcontext,
            },
        )(),
    )
    yield
    if server_shared._TURN_LOCK.locked():
        server_shared._TURN_LOCK.release()
    if server_shared._MEMORY_TURN_LOCK.locked():
        server_shared._MEMORY_TURN_LOCK.release()


@pytest.fixture
def fake_vts(monkeypatch, isolated_state) -> object:
    class FakeVts:
        is_connected = True
        config = MotionConfig.defaults()

        def __init__(self):
            self.typing_calls: list[bool] = []

        def current_state(self):
            return VtsMotionState.IDLE

        def apply_config(self, config):
            self.config = config

        def note_user_typing(self, typing):
            self.typing_calls.append(typing)

        def note_speaking(self, speaking):
            pass

        def set_mouse_tracking(self, enabled, sensitivity=1.0):
            self.mouse = (bool(enabled), float(sensitivity))

        def current_catalog_sync(self):
            return None

        def trigger_catalog_actions_sync(self, selected, codes):
            pass

    fake = FakeVts()
    monkeypatch.setattr(server_shared, "_VTS_MANAGER", fake)
    monkeypatch.setattr(chat_router, "_VTS_MANAGER", fake, raising=False)
    monkeypatch.setattr(observation_router, "_VTS_MANAGER", fake, raising=False)
    monkeypatch.setattr(vts_router, "_VTS_MANAGER", fake, raising=False)
    monkeypatch.setattr(server_shared, "_vts_manager", lambda: fake)
    monkeypatch.setattr(vts_router, "_vts_manager", lambda: fake, raising=False)
    return fake


def test_vts_config_round_trip(fake_vts) -> None:
    with TestClient(server.app) as client:
        response = client.post(
            "/vts/config",
            json=MotionConfig(sway_amplitude_idle=18.0).to_dict(),
        )
        assert response.status_code == 200
        assert response.json()["status"] == "ok"
        saved = client.get("/vts/config").json()
    assert saved["config"]["sway_amplitude_idle"] == 18.0


def test_vts_config_rejects_invalid_values(fake_vts) -> None:
    with TestClient(server.app) as client:
        response = client.post("/vts/config", json={"sway_amplitude_idle": 999.0})
        assert response.status_code == 200
        assert response.json()["status"] == "error"
        assert "sway_amplitude_idle" in response.json()["message"]


def test_vts_activity_sets_typing(fake_vts) -> None:
    with TestClient(server.app) as client:
        response = client.post("/vts/activity", json={"typing": True})
        assert response.status_code == 200
    assert fake_vts.typing_calls == [True]


def test_vts_status_includes_state_and_config(fake_vts) -> None:
    with TestClient(server.app) as client:
        body = client.get("/vts/status").json()
    assert body["status"] == "ok"
    assert body["connected"] is True
    assert body["state"] == "idle"
    assert "config" in body

def test_mouse_look_roundtrip_persists(fake_vts) -> None:
    with TestClient(server.app) as client:
        resp = client.post("/vts/mouse-look", json={"enabled": True, "sensitivity": 2.0})
        assert resp.status_code == 200
        assert resp.json()["status"] == "ok"
        got = client.get("/vts/mouse-look").json()["mouse_look"]
        assert got == {"enabled": True, "sensitivity": 2.0}


def test_mouse_look_rejects_out_of_range(fake_vts) -> None:
    with TestClient(server.app) as client:
        resp = client.post("/vts/mouse-look", json={"enabled": True, "sensitivity": 4.0})
        assert resp.status_code == 422
        resp2 = client.post("/vts/mouse-look", json={"enabled": True, "sensitivity": 0.0})
        assert resp2.status_code == 422


def test_mouse_look_accepts_boundaries(fake_vts) -> None:
    with TestClient(server.app) as client:
        for s in (0.1, 3.0):
            resp = client.post("/vts/mouse-look", json={"enabled": False, "sensitivity": s})
            assert resp.status_code == 200


def test_overlay_get_reports_running_and_error(monkeypatch, fake_vts) -> None:
    monkeypatch.setattr("mini_gpt.routers.vts.overlay_process_ids", lambda: ())
    monkeypatch.setattr("mini_gpt.routers.vts.overlay_error", lambda: None)
    with TestClient(server.app) as client:
        resp = client.get("/vts/overlay").json()
        assert resp["status"] == "ok"
        assert resp["overlay"]["running"] is False


def test_overlay_enable_spawns(monkeypatch, fake_vts) -> None:
    calls = {"n": 0}
    monkeypatch.setattr("mini_gpt.routers.vts.start_overlay", lambda: (calls.__setitem__("n", 1), True)[1])
    monkeypatch.setattr("mini_gpt.routers.vts.overlay_process_ids", lambda: (777,))
    with TestClient(server.app) as client:
        resp = client.post("/vts/overlay", json={"enabled": True})
        assert calls["n"] == 1
        assert resp.status_code == 200
        assert client.get("/vts/overlay").json()["overlay"]["running"] is True


def test_overlay_disable_kills(monkeypatch, fake_vts) -> None:
    calls = {"n": 0}
    monkeypatch.setattr("mini_gpt.routers.vts.stop_overlay", lambda: (calls.__setitem__("n", 1), True)[1])
    with TestClient(server.app) as client:
        resp = client.post("/vts/overlay", json={"enabled": False})
        assert calls["n"] == 1
        assert resp.status_code == 200


def test_overlay_missing_exe_reports_error(monkeypatch, fake_vts) -> None:
    monkeypatch.setattr("mini_gpt.routers.vts.start_overlay", lambda: False)
    monkeypatch.setattr("mini_gpt.routers.vts.overlay_error", lambda: "Overlay unavailable: ... not found")
    with TestClient(server.app) as client:
        resp = client.post("/vts/overlay", json={"enabled": True})
        assert resp.status_code == 200
        body = resp.json()
        assert body["status"] == "error"
        assert "not found" in body["message"]
        assert client.get("/vts/overlay").json()["overlay"]["enabled"] is False
