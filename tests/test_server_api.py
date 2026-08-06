from __future__ import annotations

import contextlib
import json
import queue
import threading
import time

import pytest
from fastapi.testclient import TestClient
from fastapi.testclient import TestClient as RawTestClient

import mini_gpt._server_shared as server_shared
import mini_gpt.headless_runner as headless_runner
import mini_gpt.routers.chat as chat_router
import mini_gpt.routers.observation as observation_router
import mini_gpt.routers.sessions as sessions_router
import mini_gpt.routers.vts as vts_router
import mini_gpt.routers.voice as voice_router
import mini_gpt.server as server
from mini_gpt.screen_reaction import ReactionCoordinator, ReactionRequest, VisionObservation


class AuthenticatedTestClient(TestClient):
    """TestClient that sends the desktop bearer token by default. API auth
    fails closed now, so every request must be authenticated."""

    def __init__(self, app, **kwargs):
        headers = dict(kwargs.pop("headers", {}))
        headers.setdefault("Authorization", "Bearer desktop-secret")
        super().__init__(app, headers=headers, **kwargs)

    def websocket_connect(self, url, subprotocols=None, **kwargs):
        from urllib.parse import parse_qsl, urlencode, urlparse, urlunparse

        parsed = urlparse(url)
        query = dict(parse_qsl(parsed.query))
        query.setdefault("token", "desktop-secret")
        url = urlunparse(parsed._replace(query=urlencode(query)))
        return super().websocket_connect(url, subprotocols=subprotocols, **kwargs)


@pytest.fixture(autouse=True)
def isolated_state(monkeypatch, tmp_path):
    # API auth now fails closed: every test must authenticate.
    monkeypatch.setenv("RU_DESKTOP_TOKEN", "desktop-secret")
    import sys
    monkeypatch.setattr(sys.modules[__name__], "TestClient", AuthenticatedTestClient)
    monkeypatch.setattr(server_shared, "SETTINGS_FILE", tmp_path / "settings.json")
    monkeypatch.setattr(server, "SETTINGS_FILE", tmp_path / "settings.json")
    monkeypatch.setattr(server_shared, "HISTORY_FILE", tmp_path / "history.json")
    monkeypatch.setattr(server, "HISTORY_FILE", tmp_path / "history.json")
    monkeypatch.setattr(server_shared, "SESSION_DIR", tmp_path / "sessions")
    monkeypatch.setattr(sessions_router, "SESSION_DIR", tmp_path / "sessions", raising=False)
    server.core.runner.backend = headless_runner.BACKEND_GGUF
    coordinator = ReactionCoordinator()
    monkeypatch.setattr(server_shared, "_SCREEN_REACTIONS", coordinator, raising=False)
    monkeypatch.setattr(chat_router, "_SCREEN_REACTIONS", coordinator, raising=False)
    monkeypatch.setattr(observation_router, "_SCREEN_REACTIONS", coordinator, raising=False)
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
def valid_observation_payload() -> dict:
    return {
        "observation_id": "obs-1",
        "event_age_ms": 100,
        "observation": {
            "scene": "game",
            "event": "missed shots",
            "confidence": 0.9,
            "novelty": 0.9,
            "comment_worthiness": 0.9,
            "visible_text": ["private message abc"],
        },
    }

def test_status_endpoint() -> None:
    with TestClient(server.app) as client:
        response = client.get("/status")
    assert response.status_code == 200
    assert response.json()["status"] == "ok"

def test_foreign_browser_origin_is_not_allowed() -> None:
    with TestClient(server.app) as client:
        response = client.get("/status", headers={"Origin": "https://attacker.example"})
    assert "access-control-allow-origin" not in response.headers

def test_configured_desktop_token_protects_rest(monkeypatch) -> None:
    monkeypatch.setenv("RU_DESKTOP_TOKEN", "desktop-secret")
    with RawTestClient(server.app) as client:
        missing = client.get("/settings")
        invalid = client.get("/settings", headers={"Authorization": "Bearer wrong"})
        valid = client.get(
            "/settings", headers={"Authorization": "Bearer desktop-secret"}
        )
    assert missing.status_code == 401
    assert invalid.status_code == 401
    assert valid.status_code == 200

def test_unconfigured_desktop_token_fails_closed(monkeypatch) -> None:
    monkeypatch.delenv("RU_DESKTOP_TOKEN", raising=False)
    with RawTestClient(server.app) as client:
        response = client.get("/settings")
    assert response.status_code == 401

def test_configured_desktop_token_protects_websocket(monkeypatch) -> None:
    monkeypatch.setenv("RU_DESKTOP_TOKEN", "desktop-secret")
    with RawTestClient(server.app) as client:
        with pytest.raises(Exception):
            with client.websocket_connect("/chat"):
                pass
        with client.websocket_connect("/chat?token=desktop-secret") as ws:
            ws.close()

def test_rejects_oversized_request_body() -> None:
    with TestClient(server.app) as client:
        response = client.post(
            "/settings",
            content=b"x" * (server.MAX_REQUEST_BODY_BYTES + 1),
            headers={"Content-Type": "application/json"},
        )
    assert response.status_code == 413

def test_settings_round_trip_and_no_api_key(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(server.core.runner, "load", lambda *a, **k: "Ready")
    with TestClient(server.app) as client:
        response = client.post(
            "/settings",
            json={
                "backend": "llama.cpp",
                "model": "local-model-gguf",
                "api_key": "",
                "api_variant": "Default",
                "max_tokens": 256,
                "temperature": 0.5,
                "allow_web_search": False,
            },
        )
        assert response.status_code == 200
        loaded = client.get("/settings")
    assert loaded.status_code == 200
    body = loaded.json()
    assert body["backend"] == "llama.cpp"
    assert body["max_tokens"] == 256
    assert body["temperature"] == 0.5
    assert body["allow_web_search"] is False
    assert "api_key" not in body
    assert "apiKey" not in body

@pytest.mark.parametrize(
    "payload",
    [
        {"temperature": 0},
        {"max_tokens": 0},
        {"max_tokens": 2048},
        {"top_k": 0},
        {"voice_speed": 3.0},
    ],
)
def test_settings_reject_invalid_runtime_bounds(payload, isolated_state) -> None:
    with TestClient(server.app) as client:
        response = client.post("/settings", json=payload)
    assert response.status_code == 422

def test_legacy_out_of_range_settings_are_safely_normalized(isolated_state) -> None:
    server.save_settings_to_file(
        {"max_tokens": 8192, "temperature": 0, "top_k": -1, "voice_speed": 9}
    )
    with TestClient(server.app) as client:
        settings = client.get("/settings").json()
    assert settings["max_tokens"] == 1024
    assert settings["temperature"] == 0.1
    assert settings["top_k"] == 1
    assert settings["voice_speed"] == 2.0

def test_settings_saves_kilo_api_key(monkeypatch, isolated_state) -> None:
    saved = []
    class FakeCred:
        def set_key(self, key: str) -> None:
            saved.append(key)

    monkeypatch.setattr(server.core, "kilo_cred", FakeCred())
    monkeypatch.setattr(server.core.runner, "load", lambda *a, **k: "Ready")
    with TestClient(server.app) as client:
        response = client.post(
            "/settings",
            json={
                "backend": "Kilo Gateway",
                "model": "deepseek-v3",
                "api_key": "kilo-test-key",
                "api_variant": "Default",
            },
        )
        assert response.status_code == 200
        assert response.json()["status"] == "success"
    assert saved == ["kilo-test-key"]

def test_settings_saves_gemini_vision_key(monkeypatch, isolated_state) -> None:
    saved = []
    monkeypatch.setattr(
        server.core, "gemini_vision_cred",
        type("Fake", (), {
            "set_key": lambda self, key: saved.append(key),
            "get_key": lambda self: saved[0] if saved else None,
        })(),
    )
    with TestClient(server.app) as client:
        response = client.post("/settings", json={"gemini_vision_api_key": "gem-key-123"})
        assert response.status_code == 200
        body = client.get("/settings").json()
        assert body["gemini_vision_api_key"] == ""
        assert "gem-key-123" not in json.dumps(body)
        assert body["has_gemini_vision_key"] is True
        config = client.get("/observation/vision-config").json()
    assert saved == ["gem-key-123"]
    assert config == {"status": "ok", "engine": "gemini"}

def test_observation_vision_config_local_without_key(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(
        server.core, "gemini_vision_cred",
        type("Fake", (), {"set_key": lambda self, key: None, "get_key": lambda self: None})(),
    )
    with TestClient(server.app) as client:
        config = client.get("/observation/vision-config").json()
    assert config == {"status": "ok", "engine": "local"}

def test_settings_empty_gemini_key_keeps_existing_key(monkeypatch, isolated_state) -> None:
    deleted = []
    monkeypatch.setattr(
        server.core, "gemini_vision_cred",
        type("Fake", (), {
            "set_key": lambda self, key: None,
            "delete_key": lambda self: deleted.append(True),
            "get_key": lambda self: None,
        })(),
    )
    with TestClient(server.app) as client:
        response = client.post("/settings", json={"gemini_vision_api_key": ""})
    assert response.status_code == 200
    assert deleted == [], "an empty key field must not wipe the saved key"

def test_models_endpoint_rejects_local_backend(monkeypatch, isolated_state) -> None:
    with TestClient(server.app) as client:
        response = client.get("/models", params={"backend": "llama.cpp"})
    assert response.json() == {"status": "error", "message": "unsupported backend: 'llama.cpp'"}

def test_models_endpoint_requires_api_key(monkeypatch, isolated_state) -> None:
    class FakeCred:
        def get_key(self) -> str | None:
            return None

    monkeypatch.setattr(server.core, "kilo_cred", FakeCred())
    with TestClient(server.app) as client:
        response = client.get("/models", params={"backend": "Kilo Gateway"})
    assert response.json()["status"] == "error"

def test_models_endpoint_returns_sorted_models(monkeypatch, isolated_state) -> None:
    class FakeCred:
        def get_key(self) -> str | None:
            return "kilo-test-key"

    fetched = []

    def fake_fetch_models(api_key: str, base_url: str) -> list[dict]:
        fetched.append((api_key, base_url))
        return [{"id": "gpt-4o"}, {"id": "claude-3-5-sonnet"}, {"id": "deepseek-v3"}]

    monkeypatch.setattr(server.core, "kilo_cred", FakeCred())
    monkeypatch.setattr(
        "mini_gpt.openai_runtime.OpenAIRuntime.fetch_models",
        staticmethod(fake_fetch_models),
    )
    with TestClient(server.app) as client:
        response = client.get("/models", params={"backend": "Kilo Gateway"})
    body = response.json()
    assert body["status"] == "ok"
    assert set(body["models"]) == {"gpt-4o", "claude-3-5-sonnet", "deepseek-v3"}
    assert fetched[0][0] == "kilo-test-key"

def test_settings_empty_api_key_does_not_crash(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(server.core.runner, "load", lambda *a, **k: "Ready")
    with TestClient(server.app) as client:
        response = client.post(
            "/settings",
            json={
                "backend": "OpenCode Zen",
                "model": "deepseek-v4-flash-free",
                "api_key": "",
                "api_variant": "Default",
                "max_tokens": 64,
                "temperature": 0.7,
            },
        )
    assert response.status_code == 200

def test_settings_reuses_saved_key_when_payload_key_empty(monkeypatch, isolated_state) -> None:
    loaded = []

    class FakeCred:
        def get_key(self) -> str | None:
            return "kilo-saved-key"

    def fake_load(*args, **kwargs):
        loaded.append(kwargs.get("api_key"))
        return "Ready (Kilo Gateway API)"

    monkeypatch.setattr(server.core, "kilo_cred", FakeCred())
    monkeypatch.setattr(server.core.runner, "load", fake_load)
    with TestClient(server.app) as client:
        response = client.post(
            "/settings",
            json={
                "backend": "Kilo Gateway",
                "model": "deepseek-v3",
                "api_key": "",
                "api_variant": "Default",
            },
        )
        assert response.status_code == 200
        assert response.json()["status"] == "success"
    assert loaded == ["kilo-saved-key"]

def test_settings_load_failure_returns_error_not_crash(monkeypatch, isolated_state) -> None:
    def boom(*a, **k):
        raise RuntimeError("missing GGUF file")

    monkeypatch.setattr(server.core.runner, "load", boom)
    with TestClient(server.app) as client:
        response = client.post(
            "/settings",
            json={
                "backend": "llama.cpp",
                "model": "local-model-gguf",
                "max_tokens": 64,
                "temperature": 0.7,
            },
        )
    assert response.status_code == 200
    body = response.json()
    assert body["status"] == "error"
    assert "missing GGUF file" in body["message"]

def test_settings_checkpoint_path_round_trip(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(server.core.runner, "load", lambda *a, **k: "Ready")
    with TestClient(server.app) as client:
        response = client.post(
            "/settings",
            json={
                "backend": "llama.cpp",
                "model": "local-model-gguf",
                "max_tokens": 64,
                "temperature": 0.7,
                "checkpoint_path": "checkpoints/ru-llama3.1-8b-instruct-v6.2-fresh",
            },
        )
        assert response.status_code == 200
        body = client.get("/settings").json()
    assert body["checkpoint_path"] == "checkpoints/ru-llama3.1-8b-instruct-v6.2-fresh"

def test_settings_rejects_retired_transformers_backend(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(server.core.runner, "load", lambda *a, **k: "Ready")
    with TestClient(server.app) as client:
        response = client.post(
            "/settings",
            json={
                "backend": "Transformers",
                "model": "ru-llama3.1-8b-instruct-v6.2-fresh",
                "max_tokens": 64,
                "temperature": 0.7,
                "checkpoint_path": "checkpoints/ru-llama3.1-8b-instruct-v6.2-fresh",
            },
        )
        assert response.status_code == 200
        assert response.json()["status"] == "error"
        body = client.get("/settings").json()
    assert body["backend"] != "Transformers"

def test_settings_voice_fields_round_trip(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(server.core.runner, "load", lambda *a, **k: "Ready")
    with TestClient(server.app) as client:
        response = client.post(
            "/settings",
            json={
                "backend": "llama.cpp",
                "model": "local-model-gguf",
                "max_tokens": 64,
                "temperature": 0.7,
                "voice_label": "Ái Vy (OmniVoice)",
                "voice_speed": 1.05,
            },
        )
        assert response.status_code == 200
        body = client.get("/settings").json()
    assert body["voice_label"] == "Ái Vy (OmniVoice)"
    assert body["voice_speed"] == 1.05

def test_settings_partial_update_keeps_backend(monkeypatch, isolated_state) -> None:
    load_calls = {"n": 0}

    def fake_load(*a, **k):
        load_calls["n"] += 1
        return "Ready"

    monkeypatch.setattr(server.core.runner, "load", fake_load)
    with TestClient(server.app) as client:
        client.post(
            "/settings",
            json={"backend": "llama.cpp", "model": "local-model-gguf", "max_tokens": 128},
        )
        voice_only = client.post("/settings", json={"voice_speed": 1.1})
        body = client.get("/settings").json()
    assert voice_only.status_code == 200
    assert voice_only.json()["status"] == "success"
    assert body["backend"] == "llama.cpp"
    assert body["voice_speed"] == 1.1
    assert load_calls["n"] == 1, "partial update must not reload the backend"

def test_voice_status_reports_availability(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(server_shared, "voice_available", lambda: False)
    monkeypatch.setattr(voice_router, "voice_available", lambda: False, raising=False)
    with TestClient(server.app) as client:
        response = client.get("/voice/status")
    assert response.status_code == 200
    assert response.json() == {"status": "ok", "available": False}

def test_post_voice_uses_urllib_request_instead_of_fastapi_request(monkeypatch) -> None:
    captured = {}

    class FakeResponse:
        status = 200

        def __enter__(self):
            return self

        def __exit__(self, *_args):
            return None

        def read(self):
            return b'{"ready":true}'

    def fake_urlopen(request, timeout):
        captured["request"] = request
        captured["timeout"] = timeout
        return FakeResponse()

    monkeypatch.setattr(server_shared, "urlopen", fake_urlopen)

    status, body = server._post_voice(
        "/warmup", {"voice_profile": "ru"}, timeout=30.0
    )

    request = captured["request"]
    assert request.full_url == "http://127.0.0.1:5052/warmup"
    assert request.get_method() == "POST"
    assert json.loads(request.data) == {"voice_profile": "ru"}
    assert captured["timeout"] == 30.0
    assert (status, body) == (200, b'{"ready":true}')

def test_voice_tts_returns_wav_bytes(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(server_shared, "ensure_voice_service", lambda: None)
    monkeypatch.setattr(voice_router, "ensure_voice_service", lambda: None, raising=False)
    monkeypatch.setattr(server_shared, "request_voice_audio", lambda text, label, speed: b"RIFFWAVE-data")
    monkeypatch.setattr(voice_router, "request_voice_audio", lambda text, label, speed: b"RIFFWAVE-data", raising=False)
    with TestClient(server.app) as client:
        response = client.post("/voice/tts", json={"text": "xin chào"})
    assert response.status_code == 200
    assert response.headers["content-type"] == "audio/wav"
    assert response.content == b"RIFFWAVE-data"

def test_voice_tts_rejects_empty_text(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(server_shared, "ensure_voice_service", lambda: None)
    monkeypatch.setattr(voice_router, "ensure_voice_service", lambda: None, raising=False)
    with TestClient(server.app) as client:
        response = client.post("/voice/tts", json={"text": "   "})
    assert response.status_code == 200
    assert response.json()["status"] == "error"

def test_voice_tts_strips_internal_markers_before_synthesis(monkeypatch, isolated_state) -> None:
    sent = []

    def fake_audio(text, label, speed):
        sent.append(text)
        return b"RIFFWAVE-data"

    monkeypatch.setattr(server_shared, "ensure_voice_service", lambda: None)
    monkeypatch.setattr(voice_router, "ensure_voice_service", lambda: None, raising=False)
    monkeypatch.setattr(server_shared, "request_voice_audio", fake_audio)
    monkeypatch.setattr(voice_router, "request_voice_audio", fake_audio, raising=False)
    with TestClient(server.app) as client:
        response = client.post(
            "/voice/tts",
            json={"text": "Chào anh nha! [[VOICE_PACE:quick]] [[VTS:joy]]"},
        )
    assert response.status_code == 200
    assert sent == ["Chào anh nha!"]

def test_voice_tts_surfaces_service_failure(monkeypatch, isolated_state) -> None:
    def boom(text, label, speed):
        raise RuntimeError("voice server offline")

    monkeypatch.setattr(server_shared, "ensure_voice_service", lambda: None)
    monkeypatch.setattr(voice_router, "ensure_voice_service", lambda: None, raising=False)
    monkeypatch.setattr(server_shared, "request_voice_audio", boom)
    monkeypatch.setattr(voice_router, "request_voice_audio", boom, raising=False)
    with TestClient(server.app) as client:
        response = client.post("/voice/tts", json={"text": "hi"})
    assert response.status_code == 200
    body = response.json()
    assert body["status"] == "error"
    assert "voice server offline" in body["message"]

def test_voice_warmup_and_release(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(server_shared, "ensure_voice_service", lambda: None)
    monkeypatch.setattr(voice_router, "ensure_voice_service", lambda: None, raising=False)
    monkeypatch.setattr(server_shared, "request_voice_control", lambda path, label: {"ok": True})
    monkeypatch.setattr(voice_router, "request_voice_control", lambda path, label: {"ok": True}, raising=False)
    with TestClient(server.app) as client:
        warmup = client.post("/voice/warmup")
        release = client.post("/voice/release")
    assert warmup.json() == {
        "status": "ok",
        "result": {"ready": True, "voice_profile": "ru"},
    }
    assert release.json() == {"status": "ok", "result": {"ok": True}}

def test_voice_transcribe_returns_text(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(
        server_shared,
        "build_speech_recognizer",
        lambda config: type("Recognizer", (), {"recognize": lambda self, audio: "xin chào"})(),
    )
    monkeypatch.setattr(
        voice_router,
        "build_speech_recognizer",
        lambda config: type("Recognizer", (), {"recognize": lambda self, audio: "xin chào"})(),
    )
    pcm = b"\x00\x00" * 16000
    with TestClient(server.app) as client:
        response = client.post(
            "/voice/transcribe",
            json={"audio_b64": __import__("base64").b64encode(pcm).decode(), "sample_rate": 16000},
        )
    assert response.status_code == 200
    assert response.json() == {"status": "ok", "text": "xin chào"}

def test_voice_transcribe_rejects_bad_audio(monkeypatch, isolated_state) -> None:
    with TestClient(server.app) as client:
        empty = client.post("/voice/transcribe", json={"audio_b64": ""})
        invalid = client.post("/voice/transcribe", json={"audio_b64": "!!!not-base64!!!"})
        bad_rate = client.post("/voice/transcribe", json={"audio_b64": "AA==", "sample_rate": 0})
    assert empty.json()["status"] == "error"
    assert invalid.json()["status"] == "error"
    assert bad_rate.status_code == 422

def test_voice_transcribe_surfaces_recognizer_error(monkeypatch, isolated_state) -> None:
    class NoMatchRecognizer:
        def recognize(self, audio):
            raise server.SpeechRecognitionError("Could not understand audio.")

    monkeypatch.setattr(
        server_shared, "build_speech_recognizer", lambda config: NoMatchRecognizer()
    )
    with TestClient(server.app) as client:
        response = client.post(
            "/voice/transcribe",
            json={"audio_b64": __import__("base64").b64encode(b"\x00\x00" * 8000).decode()},
        )
    assert response.json() == {"status": "error", "message": "Could not understand audio."}

def test_vts_status_and_connect_flows(monkeypatch, isolated_state) -> None:
    from mini_gpt.vts_config import MotionConfig
    from mini_gpt.vts_state import VtsMotionState

    class FakeVts:
        is_connected = False
        config = MotionConfig.defaults()

        def current_state(self):
            return VtsMotionState.IDLE

    fake = FakeVts()
    monkeypatch.setattr(server_shared, "_VTS_MANAGER", fake)
    monkeypatch.setattr(chat_router, "_VTS_MANAGER", fake, raising=False)
    monkeypatch.setattr(observation_router, "_VTS_MANAGER", fake, raising=False)
    monkeypatch.setattr(vts_router, "_VTS_MANAGER", fake, raising=False)
    monkeypatch.setattr(server_shared, "_vts_manager", lambda: fake)
    monkeypatch.setattr(vts_router, "_vts_manager", lambda: fake, raising=False)
    with TestClient(server.app) as client:
        status = client.get("/vts/status")
        connect = client.post("/vts/connect")
    assert status.json()["status"] == "ok"
    assert status.json()["connected"] is False
    assert status.json()["state"] == "idle"
    assert "config" in status.json()
    assert connect.json()["status"] == "error"

def test_vts_speaking_endpoint_updates_motion_state(monkeypatch, isolated_state) -> None:
    speaking = []

    class FakeVts:
        is_connected = True

        def note_speaking(self, value):
            speaking.append(value)

        def current_state(self):
            from mini_gpt.vts_state import VtsMotionState
            return VtsMotionState.IDLE

    monkeypatch.setattr(server_shared, "_VTS_MANAGER", FakeVts())
    monkeypatch.setattr(chat_router, "_VTS_MANAGER", FakeVts(), raising=False)
    monkeypatch.setattr(observation_router, "_VTS_MANAGER", FakeVts(), raising=False)
    monkeypatch.setattr(vts_router, "_VTS_MANAGER", FakeVts(), raising=False)
    with TestClient(server.app) as client:
        started = client.post("/vts/speaking", json={"speaking": True})
        stopped = client.post("/vts/speaking", json={"speaking": False})
    assert started.json()["status"] == "ok"
    assert stopped.json()["status"] == "ok"
    assert speaking == [True, False]

def test_vts_connected_status_and_action(monkeypatch, isolated_state) -> None:
    from mini_gpt.vts_config import MotionConfig
    from mini_gpt.vts_state import VtsMotionState

    calls: list[dict] = []

    class FakeVts:
        is_connected = True
        config = MotionConfig.defaults()

        def current_state(self):
            return VtsMotionState.IDLE

        def trigger_actions_sync(self, actions):
            calls.append({"kind": "semantic", "facial": actions.facial, "body": actions.body})
            return True

    fake = FakeVts()
    monkeypatch.setattr(server_shared, "_VTS_MANAGER", fake)
    monkeypatch.setattr(chat_router, "_VTS_MANAGER", fake, raising=False)
    monkeypatch.setattr(observation_router, "_VTS_MANAGER", fake, raising=False)
    monkeypatch.setattr(vts_router, "_VTS_MANAGER", fake, raising=False)
    with TestClient(server.app) as client:
        status = client.get("/vts/status")
        action = client.post("/vts/action", json={"facial": "joy"})
        empty = client.post("/vts/action", json={})
    assert status.json()["status"] == "ok"
    assert status.json()["connected"] is True
    assert action.json() == {"status": "ok"}
    assert calls == [{"kind": "semantic", "facial": "joy", "body": None}]
    assert empty.json()["status"] == "error"

def test_vts_unknown_semantic_action_is_reported_as_error(monkeypatch, isolated_state) -> None:
    class FakeVts:
        is_connected = True

        def trigger_actions_sync(self, actions):
            return False

    monkeypatch.setattr(server_shared, "_VTS_MANAGER", FakeVts())
    monkeypatch.setattr(chat_router, "_VTS_MANAGER", FakeVts(), raising=False)
    monkeypatch.setattr(observation_router, "_VTS_MANAGER", FakeVts(), raising=False)
    monkeypatch.setattr(vts_router, "_VTS_MANAGER", FakeVts(), raising=False)
    with TestClient(server.app) as client:
        response = client.post("/vts/action", json={"facial": "not-on-this-model"})
    assert response.json()["status"] == "error"

def test_chat_websocket_streams_chunks_then_done(monkeypatch, isolated_state) -> None:
    def fake_generate(prompt, max_new_tokens, temperature, top_k, allow_web_search, turns_history, on_stream_delta, **kwargs):
        on_stream_delta("Ch")
        on_stream_delta("ào")
        return "Chào anh"

    monkeypatch.setattr(server.core, "generate_reply_with_memory", fake_generate)
    with TestClient(server.app) as client:
        session_id = client.post("/sessions/new").json()["session_id"]
        with client.websocket_connect("/chat") as ws:
            ws.send_text(json.dumps({"prompt": "xin chào", "session_id": session_id}))
            first = ws.receive_json()
            second = ws.receive_json()
            done = ws.receive_json()
    assert first == {"type": "chunk", "content": "Ch"}
    assert second == {"type": "chunk", "content": "ào"}
    assert done["type"] == "done"

def test_chat_error_sends_error_then_done(monkeypatch, isolated_state) -> None:
    def boom(prompt, max_new_tokens, temperature, top_k, allow_web_search, turns_history, on_stream_delta, **kwargs):
        raise RuntimeError("model exploded")

    monkeypatch.setattr(server.core, "generate_reply_with_memory", boom)
    with TestClient(server.app) as client:
        session_id = client.post("/sessions/new").json()["session_id"]
        with client.websocket_connect("/chat") as ws:
            ws.send_text(json.dumps({"prompt": "boom", "session_id": session_id}))
            error = ws.receive_json()
    assert error["type"] == "error"
    assert "Đã xảy ra lỗi xử lý yêu cầu" in error["content"]

def test_prompts_are_processed_serially(monkeypatch, isolated_state) -> None:
    started = threading.Event()
    release = threading.Event()
    call_count = {"n": 0}

    def slow_generate(prompt, max_new_tokens, temperature, top_k, allow_web_search, turns_history, on_stream_delta, **kwargs):
        call_count["n"] += 1
        if call_count["n"] == 1:
            started.set()
            assert release.wait(timeout=5), "previous turn never finished"
        on_stream_delta("ok")
        return "ok"

    monkeypatch.setattr(server.core, "generate_reply_with_memory", slow_generate)
    with TestClient(server.app) as client:
        with client.websocket_connect("/chat") as ws:
            ws.send_text(json.dumps({"prompt": "first"}))
            assert started.wait(timeout=3), "first turn did not start"
            ws.send_text(json.dumps({"prompt": "second"}))
            time.sleep(0.5)
            assert call_count["n"] == 1, "second turn started before the first finished"
            release.set()
            while ws.receive_json()["type"] != "done":
                pass
            while ws.receive_json()["type"] != "done":
                pass
    assert call_count["n"] == 2

def test_chat_persists_turns_into_session(monkeypatch, isolated_state) -> None:
    def fake_generate(prompt, max_new_tokens, temperature, top_k, allow_web_search, turns_history, on_stream_delta, **kwargs):
        on_stream_delta("reply")
        return "reply"

    monkeypatch.setattr(server.core, "generate_reply_with_memory", fake_generate)
    with TestClient(server.app) as client:
        created = client.post("/sessions/new")
        session_id = created.json()["session_id"]
        with client.websocket_connect("/chat") as ws:
            ws.send_text(json.dumps({"prompt": "hello", "session_id": session_id}))
            while ws.receive_json()["type"] != "done":
                pass
        loaded = client.post(f"/sessions/{session_id}/activate")
    body = loaded.json()
    assert body["status"] == "ok"
    messages = body["session"]["messages"]
    assert [m["role"] for m in messages] == ["user", "ai"]
    assert messages[0]["content"] == "hello"
    assert messages[1]["content"] == "reply"
    assert body["session"]["title"] == "hello"

def test_web_search_flag_passed_through(monkeypatch, isolated_state) -> None:
    captured: dict[str, object] = {}

    def fake_generate(prompt, max_new_tokens, temperature, top_k, allow_web_search, turns_history, on_stream_delta, **kwargs):
        captured["search"] = allow_web_search
        return "ok"

    monkeypatch.setattr(server.core, "generate_reply_with_memory", fake_generate)
    with TestClient(server.app) as client:
        client.post(
            "/settings",
            json={
                "backend": "llama.cpp",
                "model": "local-model-gguf",
                "max_tokens": 64,
                "temperature": 0.7,
                "allow_web_search": False,
            },
        )
        with client.websocket_connect("/chat") as ws:
            ws.send_text(json.dumps({"prompt": "hi"}))
            while ws.receive_json()["type"] != "done":
                pass
    assert captured["search"] is False

def test_observation_preflight_requires_cloud_and_warms_current_omnivoice(
    monkeypatch,
) -> None:
    warmed: list[object] = []
    lifecycle = type(
        "RecordingGpuLifecycle",
        (),
        {"prepare_speech": lambda self, selection: warmed.append(selection)},
    )()
    monkeypatch.setattr(server_shared, "_GPU_LIFECYCLE", lifecycle)
    monkeypatch.setattr(chat_router, "_GPU_LIFECYCLE", lifecycle, raising=False)
    monkeypatch.setattr(observation_router, "_GPU_LIFECYCLE", lifecycle, raising=False)
    monkeypatch.setattr(voice_router, "_GPU_LIFECYCLE", lifecycle, raising=False)
    monkeypatch.setattr(
        server_shared,
        "effective_settings",
        lambda: {
            **server_shared._DEFAULT_SETTINGS,
            "voice_label": server_shared.VOICE_SELECTIONS[1].label,
        },
    )

    with TestClient(server.app) as client:
        local = client.post("/observation/preflight")
        server.core.runner.backend = headless_runner.BACKEND_OPENCODE
        hosted = client.post("/observation/preflight")

    assert local.json()["status"] == "error"
    assert hosted.json() == {
        "status": "ok",
        "voice_label": "Ái Vy (OmniVoice)",
        "voice_speed": 1.0,
    }
    assert len(warmed) == 1
    assert warmed[0].profile_id == "ai_vy"

def test_observation_react_passes_unfiltered_visible_text_to_cloud(
    monkeypatch, valid_observation_payload
) -> None:
    captured: dict[str, object] = {}
    server.core.runner.backend = headless_runner.BACKEND_OPENCODE
    monkeypatch.setattr(server_shared, "_SCREEN_REACTIONS", ReactionCoordinator(clock=lambda: 100.0))
    monkeypatch.setattr(chat_router, "_SCREEN_REACTIONS", ReactionCoordinator(clock=lambda: 100.0), raising=False)
    monkeypatch.setattr(observation_router, "_SCREEN_REACTIONS", ReactionCoordinator(clock=lambda: 100.0), raising=False)

    def fake_reaction(prompt, **kwargs):
        captured["prompt"] = prompt
        kwargs["on_complete_timing"](0.123)
        return "Đau mắt giùm anh luôn."

    monkeypatch.setattr(server.core.runner, "generate_screen_reaction", fake_reaction)

    with TestClient(server.app) as client:
        response = client.post("/observation/react", json=valid_observation_payload)

    body = response.json()
    assert body["status"] == "ok"
    assert "private message abc" in captured["prompt"]
    assert body["reaction"]["cloud_ms"] == 123
    assert body["reaction"]["text"] == "Đau mắt giùm anh luôn."

def test_observation_react_does_not_write_session_memory_or_learning(
    monkeypatch, valid_observation_payload
) -> None:
    server.core.runner.backend = headless_runner.BACKEND_OPENCODE
    monkeypatch.setattr(server_shared, "_SCREEN_REACTIONS", ReactionCoordinator(clock=lambda: 100.0))
    monkeypatch.setattr(chat_router, "_SCREEN_REACTIONS", ReactionCoordinator(clock=lambda: 100.0), raising=False)
    monkeypatch.setattr(observation_router, "_SCREEN_REACTIONS", ReactionCoordinator(clock=lambda: 100.0), raising=False)
    monkeypatch.setattr(
        server.core.runner,
        "generate_screen_reaction",
        lambda *args, **kwargs: "Một câu ngắn.",
    )
    for owner, name in (
        (server.core, "remember"),
        (server, "_learn_from_turn"),
        (server, "_session_append_messages"),
    ):
        monkeypatch.setattr(
            owner,
            name,
            lambda *args, **kwargs: pytest.fail("screen reaction persisted"),
        )

    with TestClient(server.app) as client:
        response = client.post("/observation/react", json=valid_observation_payload)

    assert response.json()["status"] == "ok"

def test_observation_cancel_marks_pending_request(monkeypatch) -> None:
    coordinator = ReactionCoordinator(clock=lambda: 100.0)
    request = ReactionRequest(
        observation_id="obs-1",
        event_age_ms=100,
        observation=VisionObservation("game", "miss", 0.9, 0.9, 0.9),
    )
    assert coordinator.begin(request).allowed
    monkeypatch.setattr(server_shared, "_SCREEN_REACTIONS", coordinator)
    monkeypatch.setattr(chat_router, "_SCREEN_REACTIONS", coordinator, raising=False)
    monkeypatch.setattr(observation_router, "_SCREEN_REACTIONS", coordinator, raising=False)

    with TestClient(server.app) as client:
        response = client.post("/observation/cancel", json={"observation_id": "obs-1"})

    assert response.json() == {"status": "ok"}

def test_observation_react_does_not_commit_before_ack(
    monkeypatch, valid_observation_payload
) -> None:
    coordinator = ReactionCoordinator(clock=lambda: 100.0)
    monkeypatch.setattr(server_shared, "_SCREEN_REACTIONS", coordinator)
    monkeypatch.setattr(chat_router, "_SCREEN_REACTIONS", coordinator, raising=False)
    monkeypatch.setattr(observation_router, "_SCREEN_REACTIONS", coordinator, raising=False)
    monkeypatch.setattr(
        server.core.runner,
        "generate_screen_reaction",
        lambda *args, **kwargs: "Một câu ngắn.",
    )

    with TestClient(server.app) as client:
        response = client.post("/observation/react", json=valid_observation_payload)

    assert response.json()["status"] == "ok"
    assert coordinator.recent_reactions() == ()
    assert coordinator.begin(
        ReactionRequest(
            observation_id="obs-2",
            event_age_ms=100,
            observation=VisionObservation("game", "another", 0.9, 0.9, 0.9),
        )
    ).reason == "busy"

def test_observation_ack_played_commits_completed_reaction(
    monkeypatch, valid_observation_payload
) -> None:
    coordinator = ReactionCoordinator(clock=lambda: 100.0)
    monkeypatch.setattr(server_shared, "_SCREEN_REACTIONS", coordinator)
    monkeypatch.setattr(chat_router, "_SCREEN_REACTIONS", coordinator, raising=False)
    monkeypatch.setattr(observation_router, "_SCREEN_REACTIONS", coordinator, raising=False)
    monkeypatch.setattr(
        server.core.runner,
        "generate_screen_reaction",
        lambda *args, **kwargs: "Đau mắt giùm anh luôn.",
    )

    with TestClient(server.app) as client:
        reacted = client.post("/observation/react", json=valid_observation_payload)
        acked = client.post(
            "/observation/ack",
            json={"observation_id": "obs-1", "played": True},
        )

    assert reacted.json()["status"] == "ok"
    assert acked.json() == {"status": "ok"}
    assert coordinator.recent_reactions() == ("Đau mắt giùm anh luôn.",)
    assert coordinator.begin(
        ReactionRequest(
            observation_id="obs-2",
            event_age_ms=100,
            observation=VisionObservation("game", "another", 0.9, 0.9, 0.9),
        )
    ).reason == "cooldown"

def test_observation_ack_not_played_fails_without_cooldown(
    monkeypatch, valid_observation_payload
) -> None:
    coordinator = ReactionCoordinator(clock=lambda: 100.0)
    monkeypatch.setattr(server_shared, "_SCREEN_REACTIONS", coordinator)
    monkeypatch.setattr(chat_router, "_SCREEN_REACTIONS", coordinator, raising=False)
    monkeypatch.setattr(observation_router, "_SCREEN_REACTIONS", coordinator, raising=False)
    monkeypatch.setattr(
        server.core.runner,
        "generate_screen_reaction",
        lambda *args, **kwargs: "Một câu ngắn.",
    )

    with TestClient(server.app) as client:
        reacted = client.post("/observation/react", json=valid_observation_payload)
        acked = client.post(
            "/observation/ack",
            json={"observation_id": "obs-1", "played": False},
        )
        next_decision = coordinator.begin(
            ReactionRequest(
                observation_id="obs-2",
                event_age_ms=100,
                observation=VisionObservation("game", "another", 0.9, 0.9, 0.9),
            )
        )

    assert reacted.json()["status"] == "ok"
    assert acked.json() == {"status": "ok"}
    assert coordinator.recent_reactions() == ()
    assert next_decision.allowed is True

def test_observation_ack_unknown_id_is_harmless(monkeypatch) -> None:
    coordinator = ReactionCoordinator(clock=lambda: 100.0)
    monkeypatch.setattr(server_shared, "_SCREEN_REACTIONS", coordinator)
    monkeypatch.setattr(chat_router, "_SCREEN_REACTIONS", coordinator, raising=False)
    monkeypatch.setattr(observation_router, "_SCREEN_REACTIONS", coordinator, raising=False)

    with TestClient(server.app) as client:
        response = client.post(
            "/observation/ack",
            json={"observation_id": "missing-1", "played": True},
        )

    assert response.json() == {"status": "ok"}
    assert coordinator.recent_reactions() == ()

def test_chat_request_cancels_pending_screen_reaction_before_generation(
    monkeypatch,
) -> None:
    cancelled = threading.Event()

    class SpyCoordinator:
        def cancel_pending(self) -> None:
            cancelled.set()

    def fake_generate(
        prompt,
        max_new_tokens,
        temperature,
        top_k,
        allow_web_search,
        turns_history,
        on_stream_delta,
        **kwargs,
    ):
        assert cancelled.is_set()
        return "ok"

    monkeypatch.setattr(server_shared, "_SCREEN_REACTIONS", SpyCoordinator())
    monkeypatch.setattr(chat_router, "_SCREEN_REACTIONS", SpyCoordinator(), raising=False)
    monkeypatch.setattr(observation_router, "_SCREEN_REACTIONS", SpyCoordinator(), raising=False)
    monkeypatch.setattr(server.core, "generate_reply_with_memory", fake_generate)

    with TestClient(server.app) as client:
        with client.websocket_connect("/chat") as ws:
            ws.send_text(json.dumps({"prompt": "ưu tiên anh"}))
            terminal = ws.receive_json()
            while terminal["type"] not in {"done", "error"}:
                terminal = ws.receive_json()

    assert terminal["type"] == "done"
    assert cancelled.is_set()

def test_conversation_context_passed_between_turns(monkeypatch, isolated_state) -> None:
    histories: list[list] = []

    def fake_generate(prompt, max_new_tokens, temperature, top_k, allow_web_search, turns_history, on_stream_delta, **kwargs):
        histories.append(list(turns_history))
        on_stream_delta("reply")
        return "reply"

    monkeypatch.setattr(server.core, "generate_reply_with_memory", fake_generate)
    with TestClient(server.app) as client:
        with client.websocket_connect("/chat") as ws:
            ws.send_text(json.dumps({"prompt": "turn one"}))
            created = ws.receive_json()
            assert created["type"] == "session"
            session_id = created["session_id"]
            while ws.receive_json()["type"] != "done":
                pass
            ws.send_text(json.dumps({"prompt": "turn two", "session_id": session_id}))
            while ws.receive_json()["type"] != "done":
                pass
    assert len(histories[0]) == 0
    assert len(histories[1]) == 1
    assert histories[1][0].prompt == "turn one"

def test_chat_uses_context_from_requested_session_only(monkeypatch, isolated_state) -> None:
    session_a = server._create_session_file()
    session_b = server._create_session_file()
    assert session_a and session_b
    server._session_append_messages(
        session_a,
        [{"role": "user", "content": "secret A"}, {"role": "ai", "content": "answer A"}],
    )
    server._session_append_messages(
        session_b,
        [{"role": "user", "content": "topic B"}, {"role": "ai", "content": "answer B"}],
    )
    histories = []

    def fake_generate(**kwargs):
        histories.append(list(kwargs["turns_history"]))
        return "reply B"

    monkeypatch.setattr(server.core, "generate_reply_with_memory", fake_generate)
    with TestClient(server.app) as client:
        with client.websocket_connect("/chat") as ws:
            ws.send_json({"prompt": "continue B", "session_id": session_b})
            while ws.receive_json()["type"] != "done":
                pass

    assert [(turn.prompt, turn.response) for turn in histories[0]] == [("topic B", "answer B")]

def test_chat_resets_provisional_stream_when_search_restarts(monkeypatch, isolated_state) -> None:
    def fake_generate(**kwargs):
        kwargs["on_stream_delta"]("draft")
        kwargs["on_stream_reset"]()
        kwargs["on_stream_delta"]("final")
        return "final answer"

    monkeypatch.setattr(server.core, "generate_reply_with_memory", fake_generate)
    with TestClient(server.app) as client:
        with client.websocket_connect("/chat") as ws:
            ws.send_json({"prompt": "search"})
            assert ws.receive_json()["type"] == "session"
            messages = [ws.receive_json() for _ in range(4)]

    assert [message["type"] for message in messages] == ["chunk", "reset", "chunk", "done"]
    assert messages[-1]["content"] == "final answer"

def test_private_markers_are_removed_before_every_persistence_path(monkeypatch, isolated_state) -> None:
    from mini_gpt.vts_catalog import VtsCatalog, VtsCatalogEntry

    catalog = VtsCatalog(
        "model-1", "Ru", (VtsCatalogEntry("vts_01", "hotkey", "Joy", "hotkey-1"),)
    )
    triggered = []

    class FakeVts:
        is_connected = True

        def current_catalog_sync(self):
            return catalog

        def trigger_catalog_actions_sync(self, selected, codes):
            triggered.append((selected, codes))

        def note_generating(self, generating):
            pass

        def note_emotion(self, emotion):
            pass

    remembered = []
    captured = {}
    monkeypatch.setattr(server_shared, "_VTS_MANAGER", FakeVts())
    monkeypatch.setattr(chat_router, "_VTS_MANAGER", FakeVts(), raising=False)
    monkeypatch.setattr(observation_router, "_VTS_MANAGER", FakeVts(), raising=False)
    monkeypatch.setattr(vts_router, "_VTS_MANAGER", FakeVts(), raising=False)
    monkeypatch.setattr(server.core, "remember", lambda prompt, reply: remembered.append((prompt, reply)))

    def fake_generate(**kwargs):
        captured.update(kwargs)
        return "Chào anh [[VOICE_PACE:quick]] [[VTS:vts_01]]"

    monkeypatch.setattr(server.core, "generate_reply_with_memory", fake_generate)
    with TestClient(server.app) as client:
        session_id = client.post("/sessions/new").json()["session_id"]
        with client.websocket_connect("/chat") as ws:
            ws.send_json({"prompt": "hello", "session_id": session_id})
            done = ws.receive_json()
        session = client.post(f"/sessions/{session_id}/activate").json()["session"]

    assert done["content"] == "Chào anh"
    assert remembered == [("hello", "Chào anh")]
    assert session["messages"][-1]["content"] == "Chào anh"
    assert "vts_01 = Joy" in captured["vts_instruction"]
    assert triggered == [(catalog, ("vts_01",))]

def test_startup_applies_saved_settings(monkeypatch, isolated_state) -> None:
    server.save_settings_to_file(
        {
            "backend": "OpenCode Zen",
            "model": "deepseek-v4-flash-free",
            "api_variant": "Default",
            "max_tokens": 128,
            "temperature": 0.3,
            "allow_web_search": True,
        }
    )
    monkeypatch.setattr(server.core.opencode_cred, "get_key", lambda: None)
    monkeypatch.setattr(server.core.runner, "load", lambda *a, **k: "Ready")
    result = server.apply_saved_settings()
    assert result == "Ready"
    assert server.core.runner.backend == headless_runner.BACKEND_OPENCODE

def test_startup_auto_starts_voice_service(monkeypatch, isolated_state) -> None:
    started = []
    monkeypatch.setattr(server_shared, "ensure_voice_service", lambda: started.append(True))
    monkeypatch.setattr(voice_router, "ensure_voice_service", lambda: started.append(True), raising=False)
    server._start_voice_service()
    assert started == [True]

def test_sessions_list_new_and_delete(monkeypatch, isolated_state) -> None:
    with TestClient(server.app) as client:
        empty = client.get("/sessions")
        created = client.post("/sessions/new")
        listed = client.get("/sessions")
        deleted = client.delete(f"/sessions/{created.json()['session_id']}")
        gone = client.delete(f"/sessions/{created.json()['session_id']}")
        final = client.get("/sessions")
    assert empty.json() == {"status": "ok", "sessions": []}
    session_id = created.json()["session_id"]
    assert created.json()["status"] == "ok"
    assert [item["id"] for item in listed.json()["sessions"]] == [session_id]
    assert listed.json()["sessions"][0]["message_count"] == 0
    assert deleted.json() == {"status": "ok"}
    assert gone.json()["status"] == "error"
    assert final.json()["sessions"] == []

def test_session_activate_rebuilds_turn_context(monkeypatch, isolated_state) -> None:
    session_id = server._create_session_file()
    assert session_id is not None
    assert server._session_append_messages(
        session_id,
        [
            {"role": "user", "content": "  Chào   anh hôm nay  "},
            {"role": "ai", "content": "Em chào anh nè!"},
        ],
    )
    with TestClient(server.app) as client:
        loaded = client.post(f"/sessions/{session_id}/activate")
    body = loaded.json()
    assert body["status"] == "ok"
    assert body["session"]["title"] == "Chào anh hôm nay"
    assert [m["role"] for m in body["session"]["messages"]] == ["user", "ai"]
    assert [turn.prompt for turn in server._session_turns(session_id)] == ["Chào   anh hôm nay"]

def test_session_activate_ignores_orphan_user_turn(monkeypatch, isolated_state) -> None:
    session_id = server._create_session_file()
    assert session_id is not None
    assert server._session_append_messages(
        session_id,
        [
            {"role": "user", "content": "Câu hỏi đầu"},
            {"role": "ai", "content": "Trả lời đầu"},
            {"role": "user", "content": "Câu hỏi chưa có trả lời"},
        ],
    )
    with TestClient(server.app) as client:
        loaded = client.post(f"/sessions/{session_id}/activate")
    body = loaded.json()
    assert body["status"] == "ok"
    assert [turn.prompt for turn in server._session_turns(session_id)] == ["Câu hỏi đầu"]

def test_ws_creates_session_when_id_missing(monkeypatch, isolated_state) -> None:
    def fake_generate(prompt, max_new_tokens, temperature, top_k, allow_web_search, turns_history, on_stream_delta, **kwargs):
        on_stream_delta("hi")
        return "hi"

    monkeypatch.setattr(server.core, "generate_reply_with_memory", fake_generate)
    with TestClient(server.app) as client:
        with client.websocket_connect("/chat") as ws:
            ws.send_text(json.dumps({"prompt": "xin chào"}))
            session_msg = ws.receive_json()
            while ws.receive_json()["type"] != "done":
                pass
    assert session_msg["type"] == "session"
    session_id = session_msg["session_id"]
    assert server._load_session_file(session_id) is not None

def test_ws_rejects_unknown_session_id(monkeypatch, isolated_state) -> None:
    def fake_generate(prompt, max_new_tokens, temperature, top_k, allow_web_search, turns_history, on_stream_delta, **kwargs):
        return "ok"

    monkeypatch.setattr(server.core, "generate_reply_with_memory", fake_generate)
    with TestClient(server.app) as client:
        with client.websocket_connect("/chat") as ws:
            ws.send_text(json.dumps({"prompt": "hi", "session_id": "missing-session-123"}))
            session_msg = ws.receive_json()
            while ws.receive_json()["type"] != "done":
                pass
    assert session_msg["type"] == "session"
    assert session_msg["session_id"] != "missing-session-123"

def test_session_endpoints_reject_path_traversal(monkeypatch, isolated_state) -> None:
    with TestClient(server.app) as client:
        activate = client.post("/sessions/..%2Fevil/activate")
        delete = client.delete("/sessions/..%2Fevil")
    assert activate.status_code == 404
    assert delete.status_code == 404

def test_migrate_legacy_history_creates_session_and_removes_file(monkeypatch, isolated_state) -> None:
    server.HISTORY_FILE.parent.mkdir(parents=True, exist_ok=True)
    server.HISTORY_FILE.write_text(
        json.dumps([{"role": "user", "content": "Cũ lắm rồi"}, {"role": "ai", "content": "Em còn đây"}]),
        encoding="utf-8",
    )
    server.migrate_legacy_history()
    sessions = server.load_sessions()
    assert len(sessions) == 1
    assert sessions[0]["title"] == "Cũ lắm rồi"
    assert sessions[0]["message_count"] == 2
    assert not server.HISTORY_FILE.exists()

def test_migrate_legacy_history_skips_when_sessions_exist(monkeypatch, isolated_state) -> None:
    server._create_session_file()
    server.HISTORY_FILE.parent.mkdir(parents=True, exist_ok=True)
    server.HISTORY_FILE.write_text(json.dumps([{"role": "user", "content": "x"}]), encoding="utf-8")
    server.migrate_legacy_history()
    assert len(server.load_sessions()) == 1
    assert server.HISTORY_FILE.exists()

def test_self_learning_setting_default_true_and_round_trip(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(server.core.runner, "load", lambda *a, **k: "Ready")
    with TestClient(server.app) as client:
        body = client.get("/settings").json()
        assert body["enable_self_learning"] is True
        assert body["share_memory_with_cloud"] is True
        assert body["cloud_memory_consent"] is True
        saved = client.post(
            "/settings", json={"backend": "llama.cpp", "enable_self_learning": False}
        )
        assert saved.status_code == 200
        loaded = client.get("/settings").json()
    assert loaded["enable_self_learning"] is False

def test_learn_from_turn_disabled_setting_skips_queue(monkeypatch, isolated_state) -> None:
    calls = []
    monkeypatch.setattr(server_shared._MEMORY_QUEUE, "put_nowait", lambda item: calls.append(item))
    server.save_settings_to_file({"enable_self_learning": False})
    server._learn_from_turn("prompt", "reply")
    assert calls == []

def test_learn_from_turn_enqueues_when_enabled(monkeypatch, isolated_state) -> None:
    calls = []
    monkeypatch.setattr(server_shared._MEMORY_QUEUE, "put_nowait", lambda item: calls.append(item))
    server.save_settings_to_file({"enable_self_learning": True})
    server._learn_from_turn("prompt", "reply")
    assert [(item[1], item[2]) for item in calls] == [("prompt", "reply")]

def test_process_memory_turn_skips_when_model_busy(monkeypatch, isolated_state) -> None:
    extracted = []
    monkeypatch.setattr(server_shared, "extract_facts_with_llm", lambda p, r, g: extracted.append((p, r)) or ())
    monkeypatch.setattr(chat_router, "extract_facts_with_llm", lambda p, r, g: extracted.append((p, r)) or (), raising=False)
    server._MEMORY_TURN_LOCK.acquire()
    try:
        server._process_memory_turn("prompt", "reply")
    finally:
        server._MEMORY_TURN_LOCK.release()
    assert extracted == []

def test_process_memory_turn_skips_hosted_extraction_without_cloud_consent(monkeypatch, isolated_state) -> None:
    extracted = []
    server.core.runner.backend = headless_runner.BACKEND_KILO
    server.save_settings_to_file({"share_memory_with_cloud": False})
    monkeypatch.setattr(
        server_shared,
        "extract_facts_with_llm",
        lambda p, r, g: extracted.append((p, r)) or (),
    )

    server._process_memory_turn("prompt", "reply")

    assert extracted == []

def test_process_memory_turn_stores_facts_and_dedups(monkeypatch, isolated_state, tmp_path) -> None:
    monkeypatch.setattr(server.core, "memory_db_path", tmp_path / "memory.db")
    monkeypatch.setattr(server.core, "memory_index_path", tmp_path / "memory_index.faiss")
    monkeypatch.setattr(server.core, "memory_texts_path", tmp_path / "memory_texts.json")
    server.core.memory_store = None
    fact = server.ExtractFact(
        kind=server.MemoryKind.PREFERENCE,
        content="Anh thích cà phê đen",
        confidence=0.9,
        importance=0.6,
    )
    monkeypatch.setattr(
        server_shared, "extract_facts_with_llm", lambda p, r, g: (fact,)
    )
    store = server.core.get_memory_store()
    server._process_memory_turn("anh thích cà phê", "Ru: Được ạ.")
    assert store.count_active() == 1
    server._process_memory_turn("anh thích cà phê", "Ru: Được ạ.")
    assert store.count_active() == 1

def test_process_memory_turn_recovers_from_extraction_error(monkeypatch, isolated_state, tmp_path) -> None:
    monkeypatch.setattr(server.core, "memory_db_path", tmp_path / "memory.db")
    monkeypatch.setattr(server.core, "memory_index_path", tmp_path / "memory_index.faiss")
    monkeypatch.setattr(server.core, "memory_texts_path", tmp_path / "memory_texts.json")
    server.core.memory_store = None
    def boom(p, r, g):
        raise RuntimeError("boom")

    monkeypatch.setattr(server_shared, "extract_facts_with_llm", boom)
    monkeypatch.setattr(chat_router, "extract_facts_with_llm", boom, raising=False)
    server._process_memory_turn("prompt", "reply")
    assert server.core.get_memory_store().count_active() == 0

def test_chat_enqueues_learning_turn(monkeypatch, isolated_state) -> None:
    enqueued = []
    monkeypatch.setattr(server_shared, "_learn_from_turn", lambda p, r: enqueued.append((p, r)))
    monkeypatch.setattr(chat_router, "_learn_from_turn", lambda p, r: enqueued.append((p, r)), raising=False)
    monkeypatch.setattr(server_shared, "extract_facts_with_llm", lambda p, r, g: ())
    monkeypatch.setattr(chat_router, "extract_facts_with_llm", lambda p, r, g: (), raising=False)
    monkeypatch.setattr(server.core, "generate_reply_with_memory", lambda **kw: "reply")
    with TestClient(server.app) as client:
        with client.websocket_connect("/chat") as ws:
            ws.send_text(json.dumps({"prompt": "hello"}))
            while ws.receive_json()["type"] != "done":
                pass
    deadline = time.monotonic() + 5
    while not enqueued and time.monotonic() < deadline:
        time.sleep(0.01)
    assert enqueued == [("hello", "reply")]

def test_learn_from_turn_queue_full_drops_oldest(monkeypatch, isolated_state) -> None:
    fresh = queue.Queue(maxsize=2)
    monkeypatch.setattr(server_shared, "_MEMORY_QUEUE", fresh)
    monkeypatch.setattr(chat_router, "_MEMORY_QUEUE", fresh, raising=False)
    server.save_settings_to_file({"enable_self_learning": True})
    server._learn_from_turn("prompt 1", "reply 1")
    server._learn_from_turn("prompt 2", "reply 2")
    server._learn_from_turn("prompt 3", "reply 3")
    assert [(item[1], item[2]) for item in fresh.queue] == [
        ("prompt 2", "reply 2"),
        ("prompt 3", "reply 3"),
    ]
    fresh.queue.clear()

def test_memory_queue_generation_invalidates_turns_after_clear(monkeypatch, isolated_state) -> None:
    fresh = queue.Queue(maxsize=2)
    monkeypatch.setattr(server_shared, "_MEMORY_QUEUE", fresh)
    server.save_settings_to_file({"enable_self_learning": True})

    server._learn_from_turn("old prompt", "old reply")
    generation = fresh.queue[0][0]
    server._invalidate_memory_queue()

    assert server._memory_turn_generation_is_current(generation) is False
    assert fresh.empty()

def test_process_memory_turn_skips_sensitive_turn(monkeypatch, isolated_state, tmp_path) -> None:
    monkeypatch.setattr(server.core, "memory_db_path", tmp_path / "memory.db")
    monkeypatch.setattr(server.core, "memory_index_path", tmp_path / "memory_index.faiss")
    monkeypatch.setattr(server.core, "memory_texts_path", tmp_path / "memory_texts.json")
    server.core.memory_store = None
    called = []
    monkeypatch.setattr(server_shared, "extract_facts_with_llm", lambda p, r, g: called.append((p, r)) or ())
    monkeypatch.setattr(chat_router, "extract_facts_with_llm", lambda p, r, g: called.append((p, r)) or (), raising=False)
    server._process_memory_turn("api_key = sk-abc123", "Ru: Được ạ.")
    assert called == []
    assert server.core.get_memory_store().count_active() == 0

# -- vts_gui_state helpers ----------------------------------------------

def test_vts_gui_state_defaults_and_roundtrip():
    assert server_shared._load_vts_gui_state() == {
        "overlay_enabled": False,
        "mouse_look_enabled": False,
        "mouse_look_sensitivity": 1.0,
    }
    server_shared._save_vts_gui_state(
        {"overlay_enabled": True, "mouse_look_enabled": True, "mouse_look_sensitivity": 2.5}
    )
    assert server_shared._load_vts_gui_state()["mouse_look_sensitivity"] == 2.5


def test_load_vts_gui_state_rejects_bad_types():
    import json
    from pathlib import Path

    Path(server_shared.SETTINGS_FILE).write_text(
        json.dumps({"vts_gui_state": {"mouse_look_sensitivity": "abc"}}), encoding="utf-8"
    )
    state = server_shared._load_vts_gui_state()
    assert state["mouse_look_sensitivity"] == 1.0


def test_restore_vts_gui_state_applies(monkeypatch):
    import json
    from pathlib import Path

    Path(server_shared.SETTINGS_FILE).write_text(
        json.dumps(
            {
                "vts_gui_state": {
                    "overlay_enabled": True,
                    "mouse_look_enabled": True,
                    "mouse_look_sensitivity": 1.5,
                }
            }
        ),
        encoding="utf-8",
    )
    calls = {"overlay": 0, "mouse": None}

    def fake_start_overlay():
        calls["overlay"] += 1
        return True

    class FakeManager:
        def set_mouse_tracking(self, enabled, sensitivity):
            calls["mouse"] = (enabled, sensitivity)

    monkeypatch.setattr(server_shared, "start_overlay", fake_start_overlay)
    server_shared._restore_vts_gui_state(FakeManager())
    assert calls["overlay"] == 1
    assert calls["mouse"] == (True, 1.5)

def test_activity_log_endpoint_returns_events(monkeypatch, isolated_state):
    import time

    fake_events = [
        {"ts": time.monotonic(), "iso": "2026-08-05T10:00:00", "type": "chat", "session_id": "s1", "data": {"role": "assistant", "content": "hi"}},
    ]
    monkeypatch.setattr("mini_gpt.routers.status.read", lambda limit=200: fake_events)
    with TestClient(server.app) as client:
        resp = client.get("/activity-log")
        assert resp.status_code == 200
        body = resp.json()
        assert body["status"] == "ok"
        assert len(body["events"]) == 1
        assert body["events"][0]["type"] == "chat"


def test_activity_log_respects_limit(monkeypatch, isolated_state):
    captured = {}
    def fake_read(limit=200):
        captured["limit"] = limit
        return []

    monkeypatch.setattr("mini_gpt.routers.status.read", fake_read)
    with TestClient(server.app) as client:
        client.get("/activity-log?limit=50")
    assert captured["limit"] == 50


def test_activity_log_clear_endpoint(monkeypatch, isolated_state):
    cleared = []

    def fake_clear():
        cleared.append(True)

    monkeypatch.setattr("mini_gpt.routers.status.clear", fake_clear)
    with TestClient(server.app) as client:
        resp = client.delete("/activity-log")
        assert resp.status_code == 200
    assert cleared == [True]

def test_chat_hooks_import_append():
    from mini_gpt.routers import chat as chat_router
    assert hasattr(chat_router, "append_activity_log")


def test_vts_hooks_import_append():
    from mini_gpt.routers import vts as vts_router
    assert hasattr(vts_router, "append_activity_log")


def test_observation_hooks_import_append():
    from mini_gpt.routers import observation as obs_router
    assert hasattr(obs_router, "append_activity_log")


